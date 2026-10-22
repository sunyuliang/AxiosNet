namespace AxiosNet.WebAPI.Tests;

/// <summary>
/// 模型定义校验：非法请求模型在首次使用时（构建缓存处）抛 InvalidOperationException，消息指名问题。
/// </summary>
public sealed class ApiClientModelValidationTests
{
    [Fact]
    public void MissingRouteAttribute_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new NoRouteRequest()));

        Assert.Contains("路由特性", ex.Message);
    }

    [Fact]
    public void PlaceholderWithoutPathBinding_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new UnboundPlaceholderRequest()));

        Assert.Contains("{uid}", ex.Message);
    }

    [Fact]
    public void PathBindingWithoutPlaceholder_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new UnusedPathRequest()));

        Assert.Contains("[Path(\"id\")]", ex.Message);
    }

    [Fact]
    public void UnclosedBraceInTemplate_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new MalformedTemplateRequest()));

        Assert.Contains("无法识别的花括号", ex.Message);
    }

    [Fact]
    public void DuplicatePlaceholder_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new DuplicatePlaceholderRequest()));

        Assert.Contains("重复使用占位符", ex.Message);
    }

    [Fact]
    public void TwoBodyProperties_AreRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new TwoBodiesRequest()));

        Assert.Contains("[Body]", ex.Message);
        Assert.Contains("一个请求最多一个请求体", ex.Message);
    }

    [Fact]
    public void PathBindingWithoutPlaceholder_IsRejected_EvenWithOtherAttributes()
    {
        // 多特性并存后，[Path] + [Query] 同用合法；但 [Path] 仍要求模板里有对应占位符
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new MixedKindsRequest()));

        Assert.Contains("[Path(\"Id\")]", ex.Message);
    }

    [Fact]
    public void DuplicateQueryName_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new DuplicateQueryRequest()));

        Assert.Contains("同名", ex.Message);
    }

    [Fact]
    public void NullPathValue_ThrowsWithPropertyName()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateClient().GetUri(new NullablePathRequest()));

        Assert.Contains("PathValue", ex.Message);
        Assert.Contains("为 null", ex.Message);
    }

    [Fact]
    public void BuildError_IsCached_SameMessageOnEveryCall()
    {
        var client = CreateClient();

        var first = Assert.Throws<InvalidOperationException>(() => client.GetUri(new NoRouteRequest()));
        var second = Assert.Throws<InvalidOperationException>(() => client.GetUri(new NoRouteRequest()));

        Assert.Equal(first.Message, second.Message);
    }

    private static ApiClient CreateClient() => new("http://127.0.0.1:1/");

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
}
