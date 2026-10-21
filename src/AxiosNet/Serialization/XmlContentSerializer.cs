using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace AxiosNet.Serialization;

/// <summary>
/// 系统内置的 <b>XML</b> 序列化器（<see cref="System.Xml.Serialization.XmlSerializer"/>，零第三方依赖）。
/// 与默认的 <see cref="SystemTextJsonSerializer"/> 同级：想用 XML 时直接换上它即可，
/// 文本格式只需要实现两个方法。
/// <para>
/// 用法：
/// <code>
/// AxiosContent.Serializer = XmlContentSerializer.Instance;                       // 全局显式注入
/// AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);   // 或注册：未注入时按 Content-Type 自动匹配
/// new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });  // 实例/单次
///
/// await http.PostAsync("orders", order);                 // 头自动是 application/xml（未显式设 ContentType 时）
/// await http.PostAsync("orders", order, c => c.ContentType = "application/xml; charset=utf-8");  // 要显式控制时才设
/// var order = (await http.GetAsync&lt;Order&gt;("orders/1")).Data;
/// </code>
/// </para>
/// <para>
/// 与 <see cref="SystemTextJsonSerializer"/> 的差异（都是 XmlSerializer 本身的约束）：
/// <list type="bullet">
/// <item>目标类型必须有<b>公开无参构造</b>，且只有公开可读写属性/字段会被序列化；
/// 匿名类型、接口、<c>Dictionary&lt;,&gt;</c> 等**不支持**（请改用 DTO）。</item>
/// <item>基础数据类型不经过这里（框架直接取文本发送 / 解析），所以
/// <c>PostAsync("u", 42)</c>、<c>GetAsync&lt;int&gt;</c> 在 XML 下同样可用。</item>
/// <item><c>dynamic</c> 是 JSON 便利能力，XML 响应下会抛 <c>ERR_BAD_RESPONSE</c>；
/// 要原文请用 <see cref="ResponseType.Text"/> 或非泛型 <c>GetAsync()</c>。</item>
/// </list>
/// </para>
/// <para>
/// 实现说明：<see cref="XmlSerializer"/> 的构造（反射 + 动态生成序列化程序集）开销很大，
/// 因此按目标类型缓存且线程安全；输出统一声明 <c>encoding="utf-8"</c>（与库写 socket 的编码一致），
/// 默认命名空间为空以免请求体里塞满 <c>xmlns:xsi</c>/<c>xmlns:xsd</c> 噪音；
/// 反序列化禁用 DTD（防 XXE / 十亿笑声）。
/// </para>
/// </summary>
public sealed class XmlContentSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享（内部按类型缓存 <see cref="XmlSerializer"/>）。</summary>
    public static XmlContentSerializer Instance { get; } = new();

    private static readonly ConcurrentDictionary<Type, XmlSerializer> Cache = new();

    private XmlContentSerializer()
    {
    }

    /// <summary>
    /// <c>application/xml</c>：注入本序列化器后请求头会自动用它（调用方未显式设置
    /// <see cref="AxiosConfig.ContentType"/> 时），不必再手动配对头与正文。
    /// </summary>
    public string? MediaType => AxiosContentType.Xml;

    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "XmlSerializer 无法序列化 null 根对象。");
        }

        var serializer = For(type);

        // StringWriter 默认声明 encoding="utf-16"，而库按 RequestEncoding（默认 UTF-8）写 socket：
        // 声明与实际编码必须一致，否则严格的服务端会直接拒收。
        using var text = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Encoding = Encoding.UTF8 }))
        {
            serializer.Serialize(writer, value, NamespacesFor(type));
        }

        return text.ToString();
    }

    public object? Deserialize(string text, Type type, AxiosConfig config)
    {
        // XmlReader.Create(TextReader) 会忽略 XML 声明里的 encoding（文本已经是字符了），
        // 且可以在这里关掉 DTD：响应来自外部，XXE 与「十亿笑声」都不该有机会。
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

    /// <summary>
    /// 根元素使用「空前缀 + 类型声明的命名空间」：无命名空间的 DTO 因此不再带
    /// <c>xmlns:xsi</c>/<c>xmlns:xsd</c>；显式写了 <c>[XmlRoot(Namespace=...)]</c> 的类型仍按声明输出。
    /// </summary>
    private static XmlSerializerNamespaces NamespacesFor(Type type)
    {
        var declared = type.GetCustomAttribute<XmlRootAttribute>(inherit: false)?.Namespace
            ?? type.GetCustomAttribute<XmlTypeAttribute>(inherit: false)?.Namespace;

        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, declared ?? string.Empty);
        return namespaces;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
