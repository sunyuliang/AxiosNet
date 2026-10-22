namespace AxiosNet.Simple.DependencyInjection.Scenarios;

/// <summary>
/// 单个 DI 示例场景的统一抽象（由启动菜单反射发现并执行）。
/// </summary>
public interface IScenario
{
    /// <summary>场景序号（同时用于菜单编号）。</summary>
    int Number { get; }

    /// <summary>场景标题。</summary>
    string Title { get; }

    /// <summary>执行场景（baseUrl 为本地演示服务器地址）。</summary>
    Task RunAsync(string baseUrl);
}
