namespace AxiosNet.Cache;

/// <summary>一次在途发送的登记：领导者发送完成后把结果交给跟随者。</summary>
internal sealed class AxiosCacheFlight
{
    private readonly TaskCompletionSource<AxiosShortCircuit?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<AxiosShortCircuit?>? _shared;

    /// <summary>领导者：结果由 <see cref="Succeed"/> / <see cref="Fail"/> 填写。</summary>
    internal AxiosCacheFlight()
    {
    }

    /// <summary>跟随者：等领导者那一份结果。</summary>
    internal AxiosCacheFlight(Task<AxiosShortCircuit?> shared) => _shared = shared;

    /// <summary>本次调用是否由自己发请求（跟随者只等领导者的结果）。</summary>
    internal bool IsLeader => _shared is null;

    /// <summary>领导者等自己填结果，跟随者等共享的那一份。</summary>
    internal Task<AxiosShortCircuit?> Completion => _shared ?? _completion.Task;

    /// <summary>放行跟随者；<paramref name="reply"/> 为 <c>null</c> 表示这次没有可共享的字节，跟随者自己发。</summary>
    internal void Succeed(AxiosShortCircuit? reply) => _completion.TrySetResult(reply);

    /// <summary>把失败转给跟随者；无人跟随时先观察一次。</summary>
    internal void Fail(Exception error)
    {
        if (!_completion.TrySetException(error))
        {
            return;
        }

        // 没有跟随者时这个异常没人接：先观察一次。
        _ = _completion.Task.Exception;
    }
}
