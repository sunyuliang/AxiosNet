
namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 请求装配（路径 / 查询 / 请求头 / 正文）的统一错误映射：取值或格式化失败一律归 <see cref="AxiosException.ErrBadRequest"/>；<see cref="AxiosException"/> 与取消异常原样透传。
/// </summary>
internal static class BindingErrors
{
    /// <summary>执行返回值的绑定步骤；失败时包装成 <see cref="AxiosException.ErrBadRequest"/>。</summary>
    /// <typeparam name="TState">绑定步骤的输入状态。</typeparam>
    /// <typeparam name="TResult">绑定结果类型。</typeparam>
    public static TResult Run<TState, TResult>(AxiosConfig config, string targetName, TState state, Func<TState, TResult> bind)
    {
        try
        {
            return bind(state);
        }
        catch (AxiosException ex) when (ex.IsRequestConstructionFailure)
        {
            // 内核的「请求构造阶段失败」分类：WebAPI 口径改判为 ERR_BAD_REQUEST，原因保留原始异常。
            throw RequestBindingErrors.Create(config, request: null, targetName, ex.Cause ?? ex);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw RequestBindingErrors.Create(config, request: null, targetName, ex);
        }
    }

    /// <summary>执行无返回值的绑定步骤（请求头写入等）；失败时包装成 <see cref="AxiosException.ErrBadRequest"/>。</summary>
    /// <typeparam name="TState">绑定步骤的输入状态。</typeparam>
    public static void Run<TState>(AxiosConfig config, string targetName, TState state, Action<TState> bind)
    {
        try
        {
            bind(state);
        }
        catch (AxiosException ex) when (ex.IsRequestConstructionFailure)
        {
            throw RequestBindingErrors.Create(config, request: null, targetName, ex.Cause ?? ex);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw RequestBindingErrors.Create(config, request: null, targetName, ex);
        }
    }
}
