using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 请求装配失败的边界契约：<see cref="BindingErrors"/> 的四个分支
/// （成功 / 普通异常 / 构造期分类改判 / 原样透传），以及消息里必须带上的<b>通道与字段名</b>。
/// </summary>
/// <remarks>
/// 为什么单独锁这些：装配失败原本只给一句底层消息（<c>Request could not be built from 'Model': &lt;inner&gt;</c>），
/// 用户拿到的信息等于「这个模型有问题」，但不知道是哪个字段。通道 / 字段名是在绑定循环里补的，
/// 一旦某一层忘了包，错误码照样是 <c>ERR_BAD_REQUEST</c>、测试照样全绿，只有消息悄悄退化 —— 所以逐条钉死。
/// </remarks>
public sealed class BindingErrorsTests
{
    // ===============================================================
    // Run 的四个分支
    // ===============================================================

    /// <summary>成功路径原样返回，不额外分配、不改语义。</summary>
    [Fact]
    public void Run_Value_ReturnsTheResult_WhenBindingSucceeds()
    {
        Assert.Equal(42, BindingErrors.Run(new AxiosConfig(), "Probe", 0, static _ => 42));
    }

    /// <summary>普通异常（用户格式化函数抛的任意类型）→ <c>ERR_BAD_REQUEST</c>，原始异常进 <c>Cause</c>。</summary>
    [Fact]
    public void Run_Value_WrapsPlainException_AsBadRequest_KeepingTheCause()
    {
        var original = new FormatException("格式炸了");

        var ex = Assert.ThrowsAny<AxiosException>(
            () => BindingErrors.Run(new AxiosConfig(), "Probe", original, static ex => Throwing<int>(ex)));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Same(original, ex.Cause);
        Assert.Contains("Probe", ex.Message, StringComparison.Ordinal);
        Assert.Contains("格式炸了", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 内核的「构造期失败」分类（<c>IsRequestConstructionFailure</c>）在 WebAPI 口径下改判为
    /// <c>ERR_BAD_REQUEST</c>，但 <c>Cause</c> 要取更里面的原始异常，不要再包一层。
    /// </summary>
    [Fact]
    public void Run_Value_RemapsConstructionFailure_AndUnwrapsToTheOriginalCause()
    {
        var original = new InvalidOperationException("查询串展平失败");
        var constructionFailure = AxiosException.RequestConstructionFailed(new AxiosConfig(), original);

        var ex = Assert.ThrowsAny<AxiosException>(
            () => BindingErrors.Run(new AxiosConfig(), "Probe", constructionFailure, static ex => Throwing<int>(ex)));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Same(original, ex.Cause);
        Assert.NotSame(constructionFailure, ex);
    }

    /// <summary>已经是一个「非构造期」的 <see cref="AxiosException"/>：原样透传，不二次包装。</summary>
    [Fact]
    public void Run_Value_PassesThroughUnrelatedAxiosException()
    {
        var unrelated = AxiosException.Canceled(new AxiosConfig(), request: null);

        var ex = Assert.ThrowsAny<AxiosException>(
            () => BindingErrors.Run(new AxiosConfig(), "Probe", unrelated, static ex => Throwing<int>(ex)));

        Assert.Same(unrelated, ex);
    }

    /// <summary>取消异常原样透传：绑定阶段的取消不能被改判成「请求构造失败」。</summary>
    [Fact]
    public void Run_Value_PassesThroughOperationCanceledException()
    {
        var canceled = new OperationCanceledException();

        var ex = Assert.Throws<OperationCanceledException>(
            () => BindingErrors.Run(new AxiosConfig(), "Probe", canceled, static ex => Throwing<int>(ex)));

        Assert.Same(canceled, ex);
    }

    /// <summary>无返回值重载同口径。</summary>
    [Fact]
    public void Run_Action_WrapsPlainException_AsBadRequest()
    {
        var original = new FormatException("头写不进去");

        var ex = Assert.ThrowsAny<AxiosException>(
            () => BindingErrors.Run(new AxiosConfig(), "Probe", original, static ex => ThrowingVoid(ex)));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Same(original, ex.Cause);
    }

    /// <summary>
    /// 带成员定位的失败（<see cref="BindingMemberException"/>）：消息补上「通道 + 字段」，
    /// <c>Cause</c> 仍是字段上真正抛出的那个异常。
    /// </summary>
    [Fact]
    public void Run_Value_ReportsChannelAndMember_WhenTheFailureCarriesThem()
    {
        var original = new FormatException("值不是合法的年龄");
        var member = new BindingMemberException(BindingChannel.Query, "UserRequest.Age", original);

        var ex = Assert.ThrowsAny<AxiosException>(
            () => BindingErrors.Run(new AxiosConfig(), "UserRequest", member, static ex => Throwing<int>(ex)));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Same(original, ex.Cause);
        Assert.Contains("query parameter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UserRequest.Age", ex.Message, StringComparison.Ordinal);
        Assert.Contains("值不是合法的年龄", ex.Message, StringComparison.Ordinal);
    }

    // ===============================================================
    // 端到端：真实声明式模型，消息必须点名具体字段
    // ===============================================================

    /// <summary>查询参数格式化抛错：消息必须点出是哪个属性、哪条通道。</summary>
    [Fact]
    public async Task QueryFormatterThrows_NamesTheProperty()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowInQueryRequest()));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("query parameter", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowInQueryRequest.Age), ex.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(ex.Cause);
    }

    /// <summary>路径参数格式化抛错：同样要点名字段（这一条走 <c>ResolveUrl</c>，与查询不是同一段代码）。</summary>
    [Fact]
    public async Task PathFormatterThrows_NamesTheProperty()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowInPathRequest()));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("path parameter", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowInPathRequest.Id), ex.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(ex.Cause);
    }

    /// <summary>请求头格式化抛错：同样要点名字段（这一条走 <c>ApplyHeaders</c>）。</summary>
    [Fact]
    public async Task HeaderFormatterThrows_NamesTheProperty()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowInHeaderRequest()));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("header", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowInHeaderRequest.Trace), ex.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(ex.Cause);
    }

    /// <summary>查询字典映射里某个键抛错：消息要带上键名，否则一个集合里几十个键无从定位。</summary>
    [Fact]
    public async Task QueryMapFormatterThrows_NamesTheKey()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowInQueryMapRequest()));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    // ===============================================================
    // 夹具
    // ===============================================================

    /// <summary>
    /// 模型声明非法（路由占位符没人绑）：必须从 <c>ERR_BAD_OPTION_VALUE</c> 的统一出口抛出，
    /// 而不是裸 <see cref="InvalidOperationException"/> —— 这条守卫的是「元数据在 <c>BuildConfig</c> 之前取，
    /// 早年绕过了绑定错误那条统一出口」这个缺口。
    /// </summary>
    [Fact]
    public void InvalidModelDeclaration_SurfacesAsAxiosException()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.ThrowsAny<AxiosException>(() => client.GetUri(new MissingPathBindingRequest()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>固定抛同一个异常的委托（避免 lambda 里写 <c>throw</c> 时被编译器当成语句块）。</summary>
    /// <typeparam name="T">返回类型（本方法永不返回）。</typeparam>
    /// <param name="exception">要抛出的异常。</param>
    /// <returns>永不返回。</returns>
    private static T Throwing<T>(Exception exception) => throw exception;

    /// <summary>无返回值版本：让 lambda 绑定到 <c>Action</c> 重载（<c>throw</c> 表达式会同时匹配两个重载）。</summary>
    /// <param name="exception">要抛出的异常。</param>
    private static void ThrowingVoid(Exception exception) => throw exception;

    /// <summary>满足 <c>[Format]</c> 静态函数签名（<c>public static string? M(object? value, ValueTextChannel channel)</c>）的格式化器。</summary>
    public static class ThrowingFormatter
    {
        /// <summary>永远抛 <see cref="FormatException"/>：模拟用户自定义格式化在某个值上失手。</summary>
        /// <param name="value">成员值。</param>
        /// <param name="channel">值所在通道。</param>
        /// <returns>永不返回。</returns>
        public static string? Throw(object? value, ValueTextChannel channel)
            => throw new FormatException($"无法把 {value} 格式化到 {channel}。");
    }

    /// <summary>查询参数上的格式化失手。</summary>
    [Get("probe")]
    private sealed class ThrowInQueryRequest : IApiRequest<Person>
    {
        /// <summary>本属性转查询串时抛错。</summary>
        [Query]
        [Format(typeof(ThrowingFormatter), nameof(ThrowingFormatter.Throw))]
        public int Age { get; set; } = 7;
    }

    /// <summary>路径占位符上的格式化失手。</summary>
    [Get("probe/{id}")]
    private sealed class ThrowInPathRequest : IApiRequest<Person>
    {
        /// <summary>本属性转路径段时抛错。</summary>
        [Path]
        [Format(typeof(ThrowingFormatter), nameof(ThrowingFormatter.Throw))]
        public int Id { get; set; } = 7;
    }

    /// <summary>请求头上的格式化失手。</summary>
    [Get("probe")]
    private sealed class ThrowInHeaderRequest : IApiRequest<Person>
    {
        /// <summary>本属性转请求头文本时抛错。</summary>
        [Header("X-Trace")]
        [Format(typeof(ThrowingFormatter), nameof(ThrowingFormatter.Throw))]
        public int Trace { get; set; } = 7;
    }

    /// <summary>查询字典映射里某个键的格式化失手（键名必须出现在消息里）。</summary>
    [Get("probe")]
    private sealed class ThrowInQueryMapRequest : IApiRequest<Person>
    {
        /// <summary>字典值转查询串时抛错，键名为 <c>boom</c>。</summary>
        [Query]
        [Format(typeof(ThrowingFormatter), nameof(ThrowingFormatter.Throw))]
        public Dictionary<string, int> Extra { get; set; } = new() { ["boom"] = 1 };
    }

    /// <summary>夹具：路径模板有 <c>{id}</c>，但没有任何属性标 <c>[Path]</c> —— 元数据构建期就该拒绝。</summary>
    [Get("probe/{id}")]
    private sealed class MissingPathBindingRequest : IApiRequest<Person>
    {
        /// <summary>普通查询参数，故意不绑 <c>{id}</c>。</summary>
        [Query]
        public string? Name { get; set; }
    }
}
