namespace AxiosNet.Internal;

/// <summary>
/// 把非泛型响应的 Data 收敛到目标类型。
/// <para>
/// 不匹配时抛 <see cref="AxiosException"/>（<c>ERR_BAD_RESPONSE</c>）而不是裸
/// <see cref="InvalidCastException"/>——典型诱因是泛型参数与 responseType 不匹配
/// （例如 <c>GetAsync&lt;Stream&gt;</c> 却显式配了 <c>responseType: 'text'</c>，
/// 或 <c>GetAsync&lt;Stream&gt;</c> 配 <c>arraybuffer</c> 拿到 byte[]）。
/// 这属于调用方配置错误，同样要走统一错误模型。
/// </para>
/// </summary>
internal static class DataCast
{
    public static T? To<T>(object? data, AxiosConfig? config, HttpRequestMessage? request)
    {
        if (data is null)
        {
            return default;
        }

        // 模式匹配覆盖引用转换与拆箱；T 为 dynamic 时擦除为 object，恒过。
        if (data is T typed)
        {
            return typed;
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
