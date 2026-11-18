using System.Reflection;
using AxiosNet.Telemetry;

namespace AxiosNet.Tests.Diagnostics;

/// <summary>
/// 观测护栏的完整性：<see cref="GuardedTelemetrySink"/> 必须兜住<b>每一个</b>
/// <see cref="IAxiosTelemetrySink"/> 成员上抛出的异常。
/// </summary>
/// <remarks>
/// 护栏逐成员内联 try/catch（不共用辅助方法，避免热路径分配闭包），因此「新加一个成员忘了包」不会有编译或运行期提示。
/// 本类用反射驱动全部成员，把这个遗漏变成测试期失败。
/// </remarks>
public sealed class TelemetryGuardTests
{
    [Fact]
    public void EverySinkMember_SwallowsImplementationFailures()
    {
        var throwing = DispatchProxy.Create<IAxiosTelemetrySink, ThrowingSinkProxy>();
        var guarded = new GuardedTelemetrySink(throwing);

        // 接口映射由运行期给出，显式实现 / 参数带 in 的成员都能正确配对。
        var map = typeof(GuardedTelemetrySink).GetInterfaceMap(typeof(IAxiosTelemetrySink));
        var escaped = new List<string>();

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var contract = map.InterfaceMethods[i];
            var target = map.TargetMethods[i];
            var arguments = contract.GetParameters().Select(DefaultArgument).ToArray();

            try
            {
                target.Invoke(guarded, arguments);
            }
            catch (TargetInvocationException ex)
            {
                escaped.Add($"{contract.Name}（{ex.InnerException?.GetType().Name}）");
            }
        }

        Assert.True(
            escaped.Count == 0,
            "以下成员没有兜住实现抛出的异常，会外溢到请求路径：" + string.Join("、", escaped));
    }

    /// <summary>反射调用需要一份实参：值类型取默认值（<c>in</c> 参数同样是值类型），引用类型传 <c>null</c>。</summary>
    private static object? DefaultArgument(ParameterInfo parameter)
        => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;

    /// <summary>成员被调用即抛异常的「违约实现」：只要有一个成员没被护栏包住，本类就会让测试变红。</summary>
    private class ThrowingSinkProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"观测实现违约：{targetMethod?.Name}");
    }
}
