using System.Net.Http;

namespace AxiosNet.WebAPI;

/// <summary>
/// 路由声明：HTTP 方法 + 路径模板。请求模型标在类上、接口级声明标在接口方法上，每份声明必须且只能标注一个本特性或其子类。
/// </summary>
/// <remarks>
/// 路径模板中的 <c>{name}</c> 占位符由 <see cref="PathAttribute"/> 属性 / 参数填充（值做 URL 转义）；模板可以是相对路径（合并实例 BaseAddress）或绝对 URL。
/// 可直接传字符串方法名（归一化为大写），也可用内置子类；自定义谓词继承本类。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public class HttpApiAttribute : Attribute
{
    /// <summary>HTTP 方法与路径模板。两个参数都要求非空。</summary>
    /// <param name="method">HTTP 方法（BCL <see cref="HttpMethod"/> 实例，内置子类传 <see cref="HttpMethod.Get"/> 等）。</param>
    /// <param name="path">路径模板，含 <c>{name}</c> 占位符；相对路径或绝对 URL。</param>
    protected HttpApiAttribute(HttpMethod method, string path)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method), "HTTP 方法不能为 null。");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径模板不能为空白。", nameof(path));
        }

        Method = method;
        Path = path.Trim();
    }

    /// <summary>HTTP 方法与路径模板（字符串形式，可免子类直接使用）；方法名归一化为大写。</summary>
    /// <param name="method">HTTP 方法名，大小写不敏感（如 <c>"get"</c> / <c>"POST"</c>）。</param>
    /// <param name="path">路径模板，含 <c>{name}</c> 占位符；相对路径或绝对 URL。</param>
    public HttpApiAttribute(string method, string path)
        : this(CreateMethod(method), path)
    {
    }

    /// <summary>字符串方法名 → 大写归一化的 <see cref="HttpMethod"/>。</summary>
    private static HttpMethod CreateMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("HTTP 方法不能为空白。", nameof(method));
        }

        return new HttpMethod(method.Trim().ToUpperInvariant());
    }

    /// <summary>本次请求的 HTTP 方法。</summary>
    public HttpMethod Method { get; }

    /// <summary>路径模板（去掉首尾空白后原样保留；占位符在解析请求模型 / 接口方法时替换）。</summary>
    public string Path { get; }
}

/// <summary>GET 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class GetAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public GetAttribute(string path) : base(HttpMethod.Get, path)
    {
    }
}

/// <summary>POST 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PostAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public PostAttribute(string path) : base(HttpMethod.Post, path)
    {
    }
}

/// <summary>PUT 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PutAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public PutAttribute(string path) : base(HttpMethod.Put, path)
    {
    }
}

/// <summary>PATCH 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PatchAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public PatchAttribute(string path) : base(HttpMethod.Patch, path)
    {
    }
}

/// <summary>DELETE 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class DeleteAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public DeleteAttribute(string path) : base(HttpMethod.Delete, path)
    {
    }
}

/// <summary>HEAD 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class HeadAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public HeadAttribute(string path) : base(HttpMethod.Head, path)
    {
    }
}

/// <summary>OPTIONS 请求的路由声明。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class OptionsAttribute : HttpApiAttribute
{
    /// <inheritdoc />
    public OptionsAttribute(string path) : base(HttpMethod.Options, path)
    {
    }
}
