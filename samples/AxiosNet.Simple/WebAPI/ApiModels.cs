using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI;

// =====================================================================
// 请求模型：使用 AxiosNet.WebAPI 时用户只需要写这一部分——
// 路由在类上（[Get] / [Post] / ...），参数绑定在属性上（[Path] / [Query] / [Header] / [Body]），
// IApiRequest<TResponse> 的泛型即返回类型，调用处 client.RequestAsync(request) 不用再写泛型。
// =====================================================================

/// <summary>GET /api/users/{id}：路径段绑定。</summary>
[Get("api/users/{id}")]
public sealed class GetUserRequest : IApiRequest<UserProfile>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>
/// GET /api/users：Role / Ids 没有标注任何参数特性，默认按 Query 绑定（键名 = 属性名）；
/// 数组走 AxiosNet 的 ids[]=1&amp;ids[]=2 规则。
/// </summary>
[Get("api/users")]
public sealed class ListUsersRequest : IApiRequest<UserProfile[]>
{
    public string? Role { get; set; }

    public int[]? Ids { get; set; }
}

/// <summary>
/// POST /api/users：整个 Payload 作为 JSON 请求体。
/// 单负载模型其实可以省掉 [Body]（POST/PUT/PATCH + 恰好一个未标注的非标量属性即默认 Body），
/// 这里保留显式标注作为用法示范——显式永远合法，语义也更自文档化。
/// 另外继承 ApiRequest&lt;T&gt; 覆写 Configure，给每个请求加一个 X-Trace 头（演示服务器会原样回显）。
/// </summary>
[Post("api/users")]
public sealed class CreateUserRequest : ApiRequest<CreatedUser>
{
    [Body]
    public UserPayload Payload { get; set; } = null!;

    protected override void Configure(AxiosConfig config)
    {
        config.Headers.Set("X-Trace", $"sample-{Guid.NewGuid():N}"[..22]);
    }
}

/// <summary>
/// POST /api/auth/login：登录本身不应带凭据——<c>[AllowAnonymous]</c> 让整条授权链路跳过
/// （令牌提供方与自定义提供方都不会被调用），不再需要在提供方里按请求类型分支。
/// </summary>
[Post("api/auth/login")]
[AllowAnonymous]
public sealed class LoginRequest : IApiRequest<TokenResult>
{
    [Body]
    public LoginPayload Credentials { get; set; } = null!;
}

/// <summary>
/// GET /api/secure/profile：需要 Bearer 令牌的受保护端点（授权示例）。
/// <c>[Authorize]</c> 声明后，客户端配了 <c>AccessTokenProvider</c> 就会自动拼
/// <c>Authorization: Bearer {token}</c>；要换方案写 <c>[Authorize("Basic")]</c>。
/// </summary>
[Get("api/secure/profile")]
[Authorize]
public sealed class SecureProfileRequest : IApiRequest<SecureProfile>
{
    [Header("X-Device")]
    public string Device { get; set; } = "sample-console";
}

/// <summary>DELETE /api/users/{id}：方法由路由特性决定。</summary>
[HttpApi("delete", "api/users/{id}")]
public sealed class DeleteUserRequest : IApiRequest<DeleteResult>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>GET /api/users/999：演示服务器对不存在的 id 返回 404（错误处理示例）。</summary>
[Get("api/users/{id}")]
public sealed class GetMissingUserRequest : IApiRequest<UserProfile>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>
/// POST /api/signed/echo：签名端点。请求体经 SigningSerializer 签名（X-Signature），
/// 服务器验签后回显并给自己的响应也签名——客户端侧同一位序列化器负责响应验签。
/// </summary>
[Post("api/signed/echo")]
public sealed class SignedEchoRequest : IApiRequest<SignedEchoResult>
{
    [Body]
    public UserPayload Payload { get; set; } = null!;
}

/// <summary>签名端点的响应：Verified 表示服务器已确认请求签名有效。</summary>
public sealed record SignedEchoResult(bool Verified, string Name, int Age);

/// <summary>
/// POST /api/upload：声明式 multipart——模型自身即表单（<c>[Multipart]</c> + <c>[Part]</c>）。
/// 未标注参数特性的属性默认按部件绑定（字段名 = 属性名）；服务器按字节精确解析并回报部件清单。
/// </summary>
[Post("api/upload")]
[Multipart]
public sealed class MultipartUploadRequest : IApiRequest<UploadResult>
{
    /// <summary>普通字段部件（字符串永远是字段，不发文件部件）。</summary>
    [Part]
    public string Title { get; set; } = string.Empty;

    /// <summary>文件部件：<c>[Part(FileName)]</c> 覆盖默认的「filename = 字段名」。</summary>
    [Part(FileName = "data.bin")]
    public byte[] Blob { get; set; } = [];

    /// <summary>字段部件集合：展平为 meta[k]=v。</summary>
    [Part("meta")]
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>multipart 上传回执：服务器解析后的部件清单。</summary>
public sealed record UploadResult(int PartCount, string Title, UploadedPart[] Parts);

/// <summary>单个部件的解析结果。</summary>
public sealed record UploadedPart(string Name, string? FileName, string? ContentType, int Length);

/// <summary>
/// GET /api/stream/events：SSE 流式端点（演示服务器按 <c>text/event-stream</c> 分帧下发）。
/// 流式请求的泛型参数是<b>单帧</b>类型，不是整个集合。
/// </summary>
[Get("api/stream/events")]
public sealed class StreamEventsRequest : IApiRequest<StreamChunk>
{
}

/// <summary>SSE 每帧的载荷（<c>data: {"index":1,"text":"..."}</c>）。</summary>
public sealed record StreamChunk(int Index, string Text);

/// <summary>
/// GET /api/stream/pages：JSON 数组流式端点（演示服务器 chunked 下发 <c>[{...},{...}]</c>）。
/// 与 SSE 用同一个帧类型，差别只在「帧边界怎么找」——数组按顶层元素，SSE 按空行。
/// </summary>
[Get("api/stream/pages")]
public sealed class StreamPagesRequest : IApiRequest<StreamChunk>
{
}

// =====================================================================
// 返回模型：与请求模型一一对应（也可直接用 string / byte[] / Stream 等原始形态）。
// =====================================================================

public sealed record UserProfile(long Id, string Name, int Age, string Role);

/// <summary>创建结果：比 UserProfile 多一个 Trace 回显（证明 Configure 钩子写入的头真实到达了服务器）。</summary>
public sealed record CreatedUser(long Id, string Name, int Age, string Role, string Trace);

public sealed record UserPayload(string Name, int Age);

public sealed record LoginPayload(string Username, string Password);

public sealed record TokenResult(string Token, int ExpiresIn);

public sealed record SecureProfile(string User, string IssuedBy, string Device);

public sealed record DeleteResult(long Id, bool Deleted);
