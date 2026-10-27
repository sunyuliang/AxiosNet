using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI;

// =====================================================================
// 请求模型：路由写在类上（[Get] / [Post] / ...），参数绑定写在属性上
// （[Path] / [Query] / [Header] / [Body]），IApiRequest<TResponse> 的泛型即返回类型。
// =====================================================================

/// <summary>GET /api/users/{id}：路径段绑定。</summary>
[Get("api/users/{id}")]
public sealed class GetUserRequest : IApiRequest<UserProfile>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>GET /api/users：未标注特性的属性按 Query 绑定（键名 = 属性名），数组展开为 ids[]=1&amp;ids[]=2。</summary>
[Get("api/users")]
public sealed class ListUsersRequest : IApiRequest<UserProfile[]>
{
    public string? Role { get; set; }

    public int[]? Ids { get; set; }
}

/// <summary>POST /api/users：Payload 作为 JSON 请求体，Configure 钩子给每个请求加 X-Trace 头（服务器原样回显）。</summary>
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

/// <summary>POST /api/auth/login：<c>[AllowAnonymous]</c> 让整条授权链路跳过。</summary>
[Post("api/auth/login")]
[AllowAnonymous]
public sealed class LoginRequest : IApiRequest<TokenResult>
{
    [Body]
    public LoginPayload Credentials { get; set; } = null!;
}

/// <summary>GET /api/secure/profile：受保护端点，客户端自动拼 <c>Authorization: Bearer {token}</c>。</summary>
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

/// <summary>POST /api/signed/echo：签名端点，请求体带 X-Signature，响应也由服务器签名。</summary>
[Post("api/signed/echo")]
public sealed class SignedEchoRequest : IApiRequest<SignedEchoResult>
{
    [Body]
    public UserPayload Payload { get; set; } = null!;
}

/// <summary>签名端点的响应：Verified 表示服务器已确认请求签名有效。</summary>
public sealed record SignedEchoResult(bool Verified, string Name, int Age);

/// <summary>POST /api/upload：声明式 multipart，模型自身即表单（<c>[Multipart]</c> + <c>[Part]</c>）。</summary>
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

/// <summary>GET /api/stream/events：SSE 流式端点，泛型参数是单帧类型。</summary>
[Get("api/stream/events")]
public sealed class StreamEventsRequest : IApiRequest<StreamChunk>
{
}

/// <summary>SSE 每帧的载荷（<c>data: {"index":1,"text":"..."}</c>）。</summary>
public sealed record StreamChunk(int Index, string Text);

/// <summary>GET /api/stream/pages：JSON 数组流式端点，按顶层元素划分帧边界。</summary>
[Get("api/stream/pages")]
public sealed class StreamPagesRequest : IApiRequest<StreamChunk>
{
}

/// <summary>GET /api/stream/big-frame：单个元素约 9 MB，默认 8 MB 单帧上限下会被拒。</summary>
[Get("api/stream/big-frame")]
public sealed class StreamBigFrameRequest : IApiRequest<StreamChunk>
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
