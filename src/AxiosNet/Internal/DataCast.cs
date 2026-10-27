namespace AxiosNet.Internal;

/// <summary>把非泛型响应的 Data 收敛到目标类型；不匹配时抛 <see cref="AxiosException"/>（<c>ERR_BAD_RESPONSE</c>）。</summary>
internal static class DataCast
{
    public static T? To<T>(object? data, AxiosConfig? config, HttpRequestMessage? request)
    {
        EnsureCompatible<T>(data, config, request);
        return data is null ? default : (T)data;
    }

    /// <summary>校验 <paramref name="data"/> 已经是 <typeparamref name="T"/>（或 <c>null</c>），不通过就抛。</summary>
    public static void EnsureCompatible<T>(object? data, AxiosConfig? config, HttpRequestMessage? request)
    {
        // 模式匹配覆盖引用转换与拆箱；T 为 dynamic 时擦除为 object，恒过。
        if (data is null || data is T)
        {
            return;
        }

        throw AxiosException.BadResponse(
            config,
            request,
            typeof(T),
            new InvalidCastException(
                $"响应 Data 的类型为 '{data.GetType()}'，无法转换为目标类型 '{typeof(T)}'。"
                + "请检查 responseType 与泛型参数是否匹配（例如 GetAsync<Stream> 需要 ResponseType.Stream）。"));
    }
}
