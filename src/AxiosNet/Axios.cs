using AxiosNet.Interceptors;
using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// 静态门面，对应 JS 里直接使用的全局 <c>axios</c>：
/// <c>await Axios.GetAsync(...)</c> 等同于 <c>axios.get(...)</c>，
/// <c>Axios.Create(...)</c> 等同于 <c>axios.create(...)</c>。
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

    /// <summary>对应 axios.create(config)。</summary>
    public static AxiosClient Create(AxiosConfig? defaults = null, ConnectionPoolOptions? poolOptions = null)
        => new(defaults, poolOptions: poolOptions);

    /// <summary>对应 axios.create(config)，用 Action 配置默认项。</summary>
    public static AxiosClient Create(Action<AxiosConfig> configureDefaults, ConnectionPoolOptions? poolOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configureDefaults);
        var config = new AxiosConfig();
        configureDefaults(config);
        return new AxiosClient(config, poolOptions: poolOptions);
    }

    public static Task<AxiosResponse> RequestAsync(AxiosConfig config)
        => DefaultClient.RequestAsync(config);

    public static Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
        => DefaultClient.RequestAsync<T>(config);

    public static Task<AxiosResponse> RequestAsync(Action<AxiosConfig> configure)
        => DefaultClient.RequestAsync(configure);

    public static Task<AxiosResponse<T>> RequestAsync<T>(Action<AxiosConfig> configure)
        => DefaultClient.RequestAsync<T>(configure);

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

    // 提示：需要 JS 式动态响应时直接使用泛型 <dynamic>，例如：
    //   dynamic data = (await Axios.GetAsync<dynamic>(url)).Data!;
    //   data.address.city、data.items[0].id 可直接访问；Post/Put/Patch/Delete 同理。
}
