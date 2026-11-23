using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Comparison.Models;

/// <summary>模型式入口的请求模型：路由写在类上，<c>[RootBody]</c> 成员把同一个 <see cref="User"/> 实例原样作为正文。</summary>
[Post("/" + Payloads.Route)]
internal sealed class SaveUserRequest : IApiRequest<User>
{
    /// <summary>请求正文：与强类型入口发送的是同一个实例、同一份序列化结果。</summary>
    [RootBody]
    public User Body { get; set; } = default!;
}
