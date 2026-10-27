using AxiosNet.Interceptors;
using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// 静态门面，对应 JS 全局 <c>axios</c>：<c>Axios.GetAsync(...)</c> 等同 <c>axios.get(...)</c>，
/// <c>Axios.Create(...)</c> 等同 <c>axios.create(...)</c>。
/// </summary>
public static class Axios
{
    private static readonly Lazy<AxiosClient> DefaultClientHolder = new(() => new AxiosClient());

    /// <summary>全局默认实例（对应 axios.defaults / axios.interceptors 所在实例）。</summary>
    public static AxiosClient DefaultClient => DefaultClientHolder.Value;

    /// <summary>对应 axios.defaults。</summary>
    public static AxiosConfig Defaults => DefaultClient.Defaults;

    /// <summary>对应 axios.interceptors。</summary>
    public static AxiosInterceptors Interceptors => DefaultClient.Interceptors;

    /// <summary>
    /// 对应 axios.create(config)。注入 <paramref name="handler"/> 时不使用连接池。
    /// <paramref name="pool"/> 与 <paramref name="poolOptions"/> / <paramref name="handler"/> 互斥（同时传入抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </summary>
    public static AxiosClient Create(
        AxiosConfig? defaults = null,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true)
        => new(defaults, pool: pool, poolOptions: poolOptions, handler: handler, disposeHandler: disposeHandler);

    /// <summary>对应 axios.create(config)，用 Action 配置默认项。</summary>
    public static AxiosClient Create(
        Action<AxiosConfig> configureDefaults,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(configureDefaults);
        var config = new AxiosConfig();
        configureDefaults(config);
        return new AxiosClient(config, pool: pool, poolOptions: poolOptions, handler: handler, disposeHandler: disposeHandler);
    }

    public static Task<AxiosResponse> RequestAsync(AxiosConfig config)
        => DefaultClient.RequestAsync(config);

    public static Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
        => DefaultClient.RequestAsync<T>(config);

    public static Task<AxiosResponse> RequestAsync(Action<AxiosConfig> configure)
        => DefaultClient.RequestAsync(configure);

    public static Task<AxiosResponse<T>> RequestAsync<T>(Action<AxiosConfig> configure)
        => DefaultClient.RequestAsync<T>(configure);

    /// <summary>对应 axios.getUri：只拼 URL，不发请求。</summary>
    public static string GetUri(AxiosConfig config) => DefaultClient.GetUri(config);

    /// <summary>对应 axios.getUri：只拼 URL，不发请求。</summary>
    public static string GetUri(Action<AxiosConfig> configure) => DefaultClient.GetUri(configure);

    /// <summary>对应 axios.isAxiosError。</summary>
    public static bool IsAxiosError(Exception? exception) => exception is AxiosException;

    /// <summary>对应 axios.isCancel：判断是否由取消导致。</summary>
    public static bool IsCancel(Exception? exception) =>
        exception is OperationCanceledException
        || exception is AxiosException { Code: AxiosException.ErrCanceled };

    public static Task<AxiosResponse> GetAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.GetAsync(url, configure, cancellationToken);

    public static Task<AxiosResponse<T>> GetAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.GetAsync<T>(url, configure, cancellationToken);

    public static Task<AxiosResponse> GetAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.GetAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> GetAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.GetAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> DeleteAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.DeleteAsync(url, configure, cancellationToken);

    public static Task<AxiosResponse<T>> DeleteAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.DeleteAsync<T>(url, configure, cancellationToken);

    public static Task<AxiosResponse> DeleteAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.DeleteAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> DeleteAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.DeleteAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> HeadAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.HeadAsync(url, configure, cancellationToken);

    public static Task<AxiosResponse<T>> HeadAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.HeadAsync<T>(url, configure, cancellationToken);

    public static Task<AxiosResponse> HeadAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.HeadAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> HeadAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.HeadAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> OptionsAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.OptionsAsync(url, configure, cancellationToken);

    public static Task<AxiosResponse<T>> OptionsAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.OptionsAsync<T>(url, configure, cancellationToken);

    public static Task<AxiosResponse> OptionsAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.OptionsAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> OptionsAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.OptionsAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> PostAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PostAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PostAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PostAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> PutAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PutAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PutAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PutAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> PatchAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PatchAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PatchAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PatchAsync<T>(url, data, configure, cancellationToken);

    // 对应 axios postForm / putForm / patchForm：默认 multipart/form-data
    public static Task<AxiosResponse> PostFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PostFormAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PostFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PostFormAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> PutFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PutFormAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PutFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PutFormAsync<T>(url, data, configure, cancellationToken);

    public static Task<AxiosResponse> PatchFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PatchFormAsync(url, data, configure, cancellationToken);

    public static Task<AxiosResponse<T>> PatchFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => DefaultClient.PatchFormAsync<T>(url, data, configure, cancellationToken);

    // 令牌直接作第二个参数的重载：与实例方法一致，避免 CancellationToken 被装箱成请求体。
    public static Task<AxiosResponse> GetAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.GetAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> GetAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.GetAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> DeleteAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.DeleteAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> DeleteAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.DeleteAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> HeadAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.HeadAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> HeadAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.HeadAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> OptionsAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.OptionsAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> OptionsAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.OptionsAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PostAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PostAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PostAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PostAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PutAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PutAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PutAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PutAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PatchAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PatchAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PatchAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PatchAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PostFormAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PostFormAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PostFormAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PostFormAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PutFormAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PutFormAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PutFormAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PutFormAsync<T>(url, cancellationToken);

    public static Task<AxiosResponse> PatchFormAsync(string url, CancellationToken cancellationToken)
        => DefaultClient.PatchFormAsync(url, cancellationToken);

    public static Task<AxiosResponse<T>> PatchFormAsync<T>(string url, CancellationToken cancellationToken)
        => DefaultClient.PatchFormAsync<T>(url, cancellationToken);

    // 需要 JS 式动态响应时用 <dynamic>：dynamic data = (await Axios.GetAsync<dynamic>(url)).Data!;
}
