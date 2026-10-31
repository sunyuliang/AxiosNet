using AxiosNet;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using Xunit;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 两份资产（net6.0 / net8.0）在 System.Text.Json 上的行为一致性：同一份调用必须得到同一种结果或同一种异常。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class JsonSerializerTargetFrameworkParityTests
{
    [Fact]
    public void NullBody_SerializesAsJsonNull_OnEveryTargetFramework()
        => Assert.Equal("null", SystemTextJsonSerializer.Instance.Serialize(null, typeof(string), new AxiosConfig()));

    [Fact]
    public void NullBody_ForNonNullableValueType_ThrowsArgumentNullOnEveryTargetFramework()
    {
        // 判定在公开入口里做，net6.0 不会退化成 STJ 的 JsonException。
        var error = Assert.Throws<ArgumentNullException>(
            () => SystemTextJsonSerializer.Instance.Serialize(null, typeof(int), new AxiosConfig()));

        Assert.Contains("非可空值类型", error.Message);
    }

#if !NET8_0_OR_GREATER
    // net8.0 无法用改开关的方式验证：JsonSerializer.IsReflectionEnabledByDefault 在首次访问时取值并缓存，
    // 用例里再改 AppContext 开关不会生效（实测「首次读取=True 改开关后=True」）。
    // net6.0 的守卫按调用读取开关，因此只有这一份资产可以在进程内复现「反射被关掉」。
    private const string ReflectionSwitchName = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    [Fact]
    public void ReflectionDisabled_SerializeThrowsActionableMetadataError()
    {
        AppContext.SetSwitch(ReflectionSwitchName, false);
        try
        {
            var error = Assert.Throws<NotSupportedException>(
                () => SystemTextJsonSerializer.Instance.Serialize(new object(), typeof(object), new AxiosConfig()));

            // 必须指向「补元数据」这个方向，而不是漏出 STJ 的内部消息。
            Assert.Contains("没有可用的 System.Text.Json 元数据", error.Message);
            Assert.Contains("net6.0", error.Message);
        }
        finally
        {
            AppContext.SetSwitch(ReflectionSwitchName, true);
        }
    }

    [Fact]
    public void ReflectionDisabled_DeserializeThrowsActionableMetadataError()
    {
        AppContext.SetSwitch(ReflectionSwitchName, false);
        try
        {
            Assert.Contains(
                "没有可用的 System.Text.Json 元数据",
                Assert.Throws<NotSupportedException>(
                    () => SystemTextJsonSerializer.Instance.Deserialize("{}", typeof(object), new AxiosConfig())).Message);

            Assert.Contains(
                "没有可用的 System.Text.Json 元数据",
                Assert.Throws<NotSupportedException>(
                    () => SystemTextJsonSerializer.Instance.Deserialize(new byte[] { 0x7B, 0x7D }, typeof(object), new AxiosConfig())).Message);
        }
        finally
        {
            AppContext.SetSwitch(ReflectionSwitchName, true);
        }
    }
#endif
}
