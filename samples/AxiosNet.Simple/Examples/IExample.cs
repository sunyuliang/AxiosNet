namespace AxiosNet.Simple.Examples;

/// <summary>单个集成示例的统一抽象。</summary>
public interface IExample
{
    /// <summary>示例序号（同时用于运行顺序与标题编号）。</summary>
    int Number { get; }

    /// <summary>示例标题，用于菜单显示。</summary>
    string Title { get; }

    /// <summary>跑一遍示例；<c>context</c> 带演示服务器地址与实例。</summary>
    Task RunAsync(ExampleContext context);
}
