using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 系统内置的 <b>XML</b> 序列化器（<see cref="System.Xml.Serialization.XmlSerializer"/>，零第三方依赖），
/// 与默认的 <see cref="SystemTextJsonSerializer"/> 同级。
/// </summary>
/// <example>
/// <code>
/// AxiosContent.Default = XmlContentSerializer.Instance;                          // 进程兜底（只在按媒体类型查不到时用）
/// AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);   // 或注册：按 Content-Type 自动匹配
/// new AxiosClient(new AxiosConfig { Serialization = { BodySerializers = new ContentSerializerRegistry { Default = XmlContentSerializer.Instance } } });  // 实例默认
///
/// await http.PostAsync("orders", order);                 // 头自动是 application/xml
/// var order = (await http.GetAsync&lt;Order&gt;("orders/1")).Data;
/// </code>
/// </example>
/// <remarks>
/// 目标类型必须有公开无参构造，只序列化公开可读写成员；基础类型不经过这里；
/// <c>dynamic</c> 不支持；依赖运行时反射，不支持 Native AOT。
/// </remarks>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "XmlSerializer 依赖运行时反射生成序列化器，不支持 Native AOT；内部已抑制但不承诺裁剪安全。")]
public sealed class XmlContentSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享（内部按类型缓存 <see cref="XmlSerializer"/>）。</summary>
    public static XmlContentSerializer Instance { get; } = new();

    // 类型 → 已生成的 XmlSerializer：建一次很贵，且可重复使用。
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Cache = new();

    private XmlContentSerializer()
    {
    }

    /// <summary><c>application/xml</c>：注入本序列化器后请求头会自动用它（调用方未显式设置时）。</summary>
    public string? MediaType => AxiosContentType.Xml;

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer 依赖运行时反射读取类型成员；Native AOT / 裁剪下请注入自定义 IContentSerializer。")]
#if NET8_0_OR_GREATER
    [RequiresDynamicCode("XmlSerializer 在运行时生成序列化程序集；Native AOT 下请注入自定义 IContentSerializer。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3051",
        Justification = "IContentSerializer 接口方法不带 RUC/RCD（其余实现是 AOT 安全的）；本实现已在方法体内按运行时能力守卫。")]
#endif
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2046",
        Justification = "IContentSerializer 接口方法不带 RUC（其余实现是裁剪安全的）；本实现已在方法体内按运行时能力守卫。")]
    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        EnsureSupported();
        if (value is null)
        {
            throw new System.ArgumentNullException(nameof(value), "XmlSerializer 无法序列化 null 根对象。");
        }

        var serializer = For(type);

        // XML 声明里的 encoding 必须与实际写 socket 的字节编码一致，解析优先级与 RequestBodyFactory 相同。
        var encoding = RequestContentType.ResolveCharset(config.ContentType, config)
            ?? config.SerializationOrNull?.TextEncoding
            ?? Encoding.UTF8;

        using var text = new DeclaredEncodingStringWriter(encoding);
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Encoding = encoding }))
        {
            serializer.Serialize(writer, value, NamespacesFor(type));
        }

        return text.ToString();
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer 依赖运行时反射读取类型成员；Native AOT / 裁剪下请注入自定义 IContentSerializer。")]
#if NET8_0_OR_GREATER
    [RequiresDynamicCode("XmlSerializer 在运行时生成序列化程序集；Native AOT 下请注入自定义 IContentSerializer。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3051",
        Justification = "IContentSerializer 接口方法不带 RUC/RCD（其余实现是 AOT 安全的）；本实现已在方法体内按运行时能力守卫。")]
#endif
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2046",
        Justification = "IContentSerializer 接口方法不带 RUC（其余实现是裁剪安全的）；本实现已在方法体内按运行时能力守卫。")]
    public object? Deserialize(string text, Type type, AxiosConfig config)
    {
        EnsureSupported();

        // 禁用 DTD 与外部解析。
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        return For(type).Deserialize(reader);
    }

#if NETSTANDARD2_0
    /// <summary>XML 走文本出口，不提供二进制序列化；返回 <c>null</c> 让库改用 <see cref="Serialize(object, Type, AxiosConfig)"/>。</summary>
    /// <remarks>显式实现而不是依赖接口默认方法：netstandard2.0 兼容资产不支持默认接口实现。</remarks>
    public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config) => null;

    /// <summary>字节入口按响应字符集解码后转调文本入口，与接口默认实现同一优先级。</summary>
    /// <remarks>显式实现而不是依赖接口默认方法：netstandard2.0 兼容资产不支持默认接口实现。</remarks>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Deserialize(
            ResponseTextDecoder.Decode(data.Span, config.Response?.CharSet, config.SerializationOrNull?.TextEncoding),
            type,
            config);
#endif

    /// <summary>AOT / 裁剪下把「序列化器不工作」提前成一条可读错误，而不是运行期随机失败。</summary>
    private static void EnsureSupported()
    {
        if (RuntimeSupport.ForceNoDynamicCode || !RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                "XmlContentSerializer 依赖 XmlSerializer 的运行时反射与代码生成，Native AOT 下不可用："
                + "请注入自定义 IContentSerializer（例如 JSON / Protobuf），或改用支持动态代码的运行时。");
        }
    }

    /// <summary>按类型缓存 <see cref="XmlSerializer"/> 实例（可并发复用）。</summary>
    private static XmlSerializer For(Type type) => Cache.GetOrAdd(type, static t => new XmlSerializer(t));

    /// <summary>根元素声明的命名空间缓存（按类型）。</summary>
    private static readonly ConcurrentDictionary<Type, string> RootNamespaces = new();

    /// <summary>根元素使用「空前缀 + 类型声明的命名空间」。</summary>
    private static XmlSerializerNamespaces NamespacesFor(Type type)
    {
        // 命名空间实例每次新建：XmlSerializer 序列化过程中可能往里补条目，共享一个可变实例有竞态。
        var declared = RootNamespaces.GetOrAdd(type, static t =>
            t.GetCustomAttribute<XmlRootAttribute>(inherit: false)?.Namespace
            ?? t.GetCustomAttribute<XmlTypeAttribute>(inherit: false)?.Namespace
            ?? string.Empty);

        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, declared);
        return namespaces;
    }

    /// <summary>让 XML 声明里的 <c>encoding</c> 跟随调用方声明的编码。</summary>
    private sealed class DeclaredEncodingStringWriter : StringWriter
    {
        private readonly Encoding _encoding;

        public DeclaredEncodingStringWriter(Encoding encoding) => _encoding = encoding;

        public override Encoding Encoding => _encoding;
    }
}
