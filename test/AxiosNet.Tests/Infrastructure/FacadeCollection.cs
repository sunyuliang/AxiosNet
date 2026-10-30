namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 会重置 / 释放进程级静态门面（<see cref="AxiosNet.Axios.DefaultClient"/>）的用例的独占集合：
/// 这类用例必须与其它正在使用门面的用例串行，否则后者的默认配置会被顺手清掉。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FacadeCollection
{
    public const string Name = "Facade";
}
