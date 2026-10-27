using System.Text.Json;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 默认 JSON 序列化器的全局选项（<see cref="AxiosJson.Options"/>）：可替换性与空值守卫。
/// 默认值本身（命名策略 / 大小写不敏感 / NumberHandling / DefaultIgnoreCondition）由
/// <c>SerializerTests.GlobalJsonDefaults_AreSystemTextJsonWeb</c> 一并钉住。
/// 这里只替换成行为等价的实例，避免影响并行执行中的其它用例。
/// </summary>
public sealed class JsonDefaultsTests
{
    [Fact]
    public void Options_CanBeReplacedAndReadBack()
    {
        var original = AxiosJson.Options;
        var replacement = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            AxiosJson.Options = replacement;

            Assert.Same(replacement, AxiosJson.Options);
        }
        finally
        {
            AxiosJson.Options = original;
        }

        Assert.Same(original, AxiosJson.Options);
    }

    [Fact]
    public void Options_NullAssignment_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AxiosJson.Options = null!);
    }
}
