
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式校验：`ValidateModels` / `ValidateInterfaces` 报错聚合、错误缓存、抽象与开放泛型跳过。
/// </summary>
public sealed class ModelValidationTests
{
    // ------------------------------------------------------------------
    // ValidateModels：抽象 / 开放泛型基类
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateModels_AssemblyScan_SkipsAbstractAndOpenGenericBases()
    {
        var errors = ApiClient.ValidateModels(typeof(ModelValidationTests).Assembly);

        // 抽象基类（含开放泛型定义）是合法模式：路由特性不继承，派生类各自声明路由。
        Assert.DoesNotContain(errors, error => error.Contains("PagedRequest"));
    }

    [Fact]
    public void ValidateModels_ExplicitAbstractType_StillReports()
    {
        var errors = ApiClient.ValidateModels(typeof(PagedRequest<Person>));

        var error = Assert.Single(errors);
        Assert.Contains("可实例化", error);
    }

    [Fact]
    public void ApiClient_GuardsBlankBaseAddress()
    {
        Assert.Throws<ArgumentException>(() => new ApiClient(""));
        Assert.Throws<ArgumentException>(() => new ApiClient("   "));
    }

    [Fact]
    public async Task Request_NullModel_ThrowsArgumentNull()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.RequestAsync<Person>(null!));
        Assert.Throws<ArgumentNullException>(() => client.GetUri<Person>(null!));
    }

    [Fact]
    public void MissingRouteAttribute_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new NoRouteRequest()));

        Assert.Contains("路由特性", ex.Message);
    }

    [Fact]
    public void PlaceholderWithoutPathBinding_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new UnboundPlaceholderRequest()));

        Assert.Contains("{uid}", ex.Message);
    }

    [Fact]
    public void PathBindingWithoutPlaceholder_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new UnusedPathRequest()));

        Assert.Contains("[Path(\"id\")]", ex.Message);
    }

    [Fact]
    public void UnclosedBraceInTemplate_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new MalformedTemplateRequest()));

        Assert.Contains("无法识别的花括号", ex.Message);
    }

    [Fact]
    public void DuplicatePlaceholder_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new DuplicatePlaceholderRequest()));

        Assert.Contains("重复使用占位符", ex.Message);
    }

    [Fact]
    public void TwoBodyProperties_AreRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new TwoBodiesRequest()));

        Assert.Contains("[Body]", ex.Message);
        Assert.Contains("一个请求最多一个请求体", ex.Message);
    }

    [Fact]
    public void PathBindingWithoutPlaceholder_IsRejected_EvenWithOtherAttributes()
    {
        // 多特性并存后，[Path] + [Query] 同用合法；但 [Path] 仍要求模板里有对应占位符
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new MixedKindsRequest()));

        Assert.Contains("[Path(\"Id\")]", ex.Message);
    }

    [Fact]
    public void DuplicateQueryName_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new DuplicateQueryRequest()));

        Assert.Contains("同名", ex.Message);
    }

    [Fact]
    public void NullPathValue_ThrowsWithPropertyName()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient_ApiClientModelValidation().GetUri(new NullablePathRequest()));

        Assert.Contains("PathValue", ex.Message);
        Assert.Contains("为 null", ex.Message);
    }

    [Fact]
    public void BuildError_IsCached_SameMessageOnEveryCall()
    {
        var client = CreateClient_ApiClientModelValidation();

        var first = Assert.Throws<InvalidOperationException>(() => client.GetUri(new NoRouteRequest()));
        var second = Assert.Throws<InvalidOperationException>(() => client.GetUri(new NoRouteRequest()));

        Assert.Equal(first.Message, second.Message);
    }

    // ------------------------------------------------------------------
    // 模型启动期校验
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateModels_ValidModel_ReturnsEmpty()
    {
        Assert.Empty(ApiClient.ValidateModels(typeof(GetUserRequest)));
    }

    [Fact]
    public void ValidateModels_CollectsAllErrors_WithoutThrowing()
    {
        var errors = ApiClient.ValidateModels(typeof(GetUserRequest), typeof(NoRouteModel), typeof(HeaderCollectionRequest), typeof(object));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, message => message.Contains("NoRouteModel") && message.Contains("路由特性"));
        Assert.Contains(errors, message => message.Contains("System.Object"));
    }

    [Fact]
    public void ValidateModels_AssemblyScan_IncludesNestedModels()
    {
        var errors = ApiClient.ValidateModels(typeof(ModelValidationTests).Assembly);

        Assert.Contains(errors, message => message.Contains(nameof(NoRouteModel)));
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }


    /// <summary>抽象请求基类（开放泛型）：程序集扫描必须跳过它。</summary>
    public abstract class PagedRequest<TResponse> : IApiRequest<TResponse>
    {
        public int Page { get; set; }
    }


    private static ApiClient CreateClient_ApiClientModelValidation() => new("http://127.0.0.1:1/");

    // ---- 非法模型样例（仅被上面用例经反射消费） ----

    public sealed class NoRouteRequest : IApiRequest<object>
    {
        public string? Name { get; set; }
    }

    [Get("users/{uid}")]
    public sealed class UnboundPlaceholderRequest : IApiRequest<object>
    {
        [Path]
        public int Id { get; set; }
    }

    [Get("users")]
    public sealed class UnusedPathRequest : IApiRequest<object>
    {
        [Path("id")]
        public int Id { get; set; }
    }

    [Get("users/{id")]
    public sealed class MalformedTemplateRequest : IApiRequest<object>
    {
        [Path]
        public int Id { get; set; }
    }

    [Get("items/{id}/{id}")]
    public sealed class DuplicatePlaceholderRequest : IApiRequest<object>
    {
        [Path]
        public int Id { get; set; }
    }

    [Post("items")]
    public sealed class TwoBodiesRequest : IApiRequest<object>
    {
        [Body]
        public object? First { get; set; }

        [Body]
        public object? Second { get; set; }
    }

    [Get("items")]
    public sealed class MixedKindsRequest : IApiRequest<object>
    {
        /// <summary>多特性并存合法（Path + Query）；但 Path 绑定仍受模板占位符校验约束。</summary>
        [Path]
        [Query]
        public int Id { get; set; }
    }

    [Get("items")]
    public sealed class DuplicateQueryRequest : IApiRequest<object>
    {
        [Query("page")]
        public int First { get; set; }

        [Query("page")]
        public int Second { get; set; }
    }

    [Get("users/{name}")]
    public sealed class NullablePathRequest : IApiRequest<object>
    {
        [Path("name")]
        public string? PathValue { get; set; }
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------


    [Get("headers")]
    private sealed class HeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }


    private sealed class NoRouteModel : IApiRequest<object>
    {
        public string? Name { get; set; }
    }


}