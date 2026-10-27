namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 遥测用例的独占集合。
/// <para>
/// 观测点是进程级的：用例里注册的 <see cref="System.Diagnostics.ActivityListener"/> /
/// <c>MeterListener</c> / <c>DiagnosticListener</c> 订阅会让<b>同一进程里所有</b> AxiosNet 请求
/// 都开始产出 span / 指标 / 事件，而并行执行的分配护栏用例正是按「每请求分配字节数」断言的。
/// 让这组用例独占运行，既不影响别人，也不被别人的请求干扰。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "telemetry";
}
