using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 请求装配（路径 / 查询 / 请求头 / 正文）的统一错误映射：取值或格式化失败一律归
/// <see cref="AxiosException.ErrBadRequest"/>，与正文 / multipart 装配口径一致；
/// <see cref="AxiosException"/> 与取消异常原样透传（不吞、不二次包装）。
/// </summary>
internal static class BindingErrors
{
    /// <summary>执行返回值的绑定步骤；失败时包装成 <see cref="AxiosException.ErrBadRequest"/>。</summary>
    public static T Run<T>(AxiosConfig config, string targetName, Func<T> bind)
    {
        try
        {
            return bind();
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
    public static void Run(AxiosConfig config, string targetName, Action bind)
    {
        try
        {
            bind();
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
