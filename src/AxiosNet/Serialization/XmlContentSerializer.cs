using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 系统内置的 <b>XML</b> 序列化器（<see cref="System.Xml.Serialization.XmlSerializer"/>，零第三方依赖），
/// 与默认的 <see cref="SystemTextJsonSerializer"/> 同级。
/// <para>
/// 用法：
/// <code>
/// AxiosContent.Serializer = XmlContentSerializer.Instance;                       // 全局显式注入
/// AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);   // 或注册：按 Content-Type 自动匹配
/// new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });  // 实例 / 单次
///
/// await http.PostAsync("orders", order);                 // 头自动是 application/xml
/// var order = (await http.GetAsync&lt;Order&gt;("orders/1")).Data;
/// </code>
/// </para>
/// <para>
/// 约束（都来自 XmlSerializer）：目标类型必须有公开无参构造，只序列化公开可读写成员；基础类型不经过这里；
/// <c>dynamic</c> 不支持；依赖运行时反射，不支持 Native AOT。
/// </para>
/// </summary>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "XmlSerializer 依赖运行时反射生成序列化器，不支持 Native AOT；内部已抑制但不承诺裁剪安全。")]
public sealed class XmlContentSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享（内部按类型缓存 <see cref="XmlSerializer"/>）。</summary>
    public static XmlContentSerializer Instance { get; } = new();

    private static readonly ConcurrentDictionary<Type, XmlSerializer> Cache = new();

    private XmlContentSerializer()
    {
    }

    /// <summary><c>application/xml</c>：注入本序列化器后请求头会自动用它（调用方未显式设置时）。</summary>
    public string? MediaType => AxiosContentType.Xml;

    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "XmlSerializer 无法序列化 null 根对象。");
        }

        var serializer = For(type);

        // XML 声明里的 encoding 必须与实际写 socket 的字节编码一致，解析优先级与 RequestBodyFactory 相同。
        var encoding = RequestContentType.GetCharsetEncoding(config.ContentType)
            ?? config.SerializationOrNull?.RequestEncoding
            ?? Encoding.UTF8;

        using var text = new DeclaredEncodingStringWriter(encoding);
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Encoding = encoding }))
        {
            serializer.Serialize(writer, value, NamespacesFor(type));
        }

        return text.ToString();
    }

    public object? Deserialize(string text, Type type, AxiosConfig config)
    {
        // 响应来自外部：关掉 DTD，XXE 与「十亿笑声」都不该有机会。
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        return For(type).Deserialize(reader);
    }

    /// <summary>按类型缓存：<see cref="XmlSerializer"/> 构造昂贵，实例本身可并发复用。</summary>
    private static XmlSerializer For(Type type) => Cache.GetOrAdd(type, static t => new XmlSerializer(t));

    /// <summary>根元素声明的命名空间缓存（按类型）。</summary>
    private static readonly ConcurrentDictionary<Type, string> RootNamespaces = new();

    /// <summary>根元素使用「空前缀 + 类型声明的命名空间」，无命名空间的 DTO 因此不带 <c>xmlns:xsi</c> / <c>xmlns:xsd</c>。</summary>
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

    /// <summary>让 XML 声明里的 <c>encoding</c> 跟随调用方声明的编码（<c>StringWriter</c> 默认写 utf-16）。</summary>
    private sealed class DeclaredEncodingStringWriter : StringWriter
    {
        private readonly Encoding _encoding;

        public DeclaredEncodingStringWriter(Encoding encoding) => _encoding = encoding;

        public override Encoding Encoding => _encoding;
    }
}
