using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 声明式装配（路径 / 查询 / 请求头 / 正文）的统一错误映射：取值或格式化失败一律归
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
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.RequestBindingFailed(config, request: null, targetName, ex);
        }
    }

    /// <summary>执行无返回值的绑定步骤（请求头写入等）；失败时包装成 <see cref="AxiosException.ErrBadRequest"/>。</summary>
    public static void Run(AxiosConfig config, string targetName, Action bind)
    {
        try
        {
            bind();
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.RequestBindingFailed(config, request: null, targetName, ex);
        }
    }
}
