namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 「短超时的流式响应」用例的前置动作：同源预热。
/// <para>
/// 这类用例要验证的是「timeout 覆盖正文读取阶段」：先让 <c>GetAsync&lt;Stream&gt;</c> 正常拿到响应头，
/// 再读正文时必须被超时打断。但 <c>Timeout</c> 是**整条请求**的预算，池里没有同源连接时，
/// 创建 Handler + TCP 建连 + 首包往返都要从这份预算里扣除。CI（2 核、用例并行、进程冷启动）上
/// 这一步偶发就吃掉 150ms，超时在收响应头阶段先触发，用例连正文都没读到——测的已经不是正文阶段。
/// </para>
/// <para>
/// 先用宽松超时打一次同源请求并把正文读完（缓冲模式下读完即归还连接），短超时请求就只剩
/// 一次已建连的本地往返，预算不会被连接建立阶段吃掉。
/// </para>
/// </summary>
internal static class ConnectionWarmup
{
    /// <summary>预热一条同源连接，供随后的短超时请求复用。</summary>
    public static async Task WarmUpAsync(this AxiosClient client, string url)
    {
        // 预热本身不参与断言：只要求请求成功走完（缓冲模式下读完正文即归还连接）。
        var response = await client.GetAsync<string>(url, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        Assert.Equal(200, response.Status);
    }
}
