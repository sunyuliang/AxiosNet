namespace AxiosNet.Cli.Tests;

/// <summary>参数解析：未知选项不被吞掉，互斥组合在启动前就报用法错误。</summary>
public sealed class CliOptionsTests
{
    /// <summary>位置参数 + 短选项 + --name=value 三种写法都能落到正确属性上。</summary>
    [Fact]
    public void ParsesSourceAndOptions()
    {
        var options = CliOptions.Parse(new[] { "spec.json", "-o", "out", "--namespace=Demo.Api", "--integer-type", "long" });

        Assert.Equal("spec.json", options.Source);
        Assert.Equal("out", options.OutputDirectory);
        Assert.Equal("Demo.Api", options.Namespace);
        Assert.True(options.IntegerIsLong);
        Assert.False(options.Check);
    }

    /// <summary>未知选项必须报用法错误，而不是被静默忽略或当成源文件。</summary>
    [Fact]
    public void UnknownOptionIsUsageError()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--nope" }));

    /// <summary>缺少文档来源时报用法错误。</summary>
    [Fact]
    public void MissingSourceIsUsageError()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(Array.Empty<string>()));

    /// <summary>一次只能处理一份文档，多余的位置参数被拒绝。</summary>
    [Fact]
    public void MultipleSourcesAreRejected()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "a.json", "b.json" }));

    /// <summary>--check（只校验）与 --dry-run（只打印）语义冲突，不允许同时出现。</summary>
    [Fact]
    public void CheckAndDryRunAreExclusive()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--check", "--dry-run" }));

    /// <summary>--interface-name 只有单接口模式才有意义，单独使用即用法错误。</summary>
    [Fact]
    public void InterfaceNameRequiresSingleInterface()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--interface-name", "IFoo" }));

    /// <summary>--style 缺省为 model，interface 可识别，非法值被拒绝。</summary>
    [Fact]
    public void StyleDefaultsToModelAndAcceptsInterface()
    {
        Assert.Equal(ApiStyle.Model, CliOptions.Parse(new[] { "spec.json" }).Style);
        Assert.Equal(ApiStyle.Interface, CliOptions.Parse(new[] { "spec.json", "--style", "interface" }).Style);
        Assert.Equal(ApiStyle.Model, CliOptions.Parse(new[] { "spec.json", "--style=model" }).Style);
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--style", "class" }));
    }

    /// <summary>只对接口式生效的选项，在 model 形态下必须直接报错而不是被忽略。</summary>
    [Fact]
    public void InterfaceOnlyOptionsAreRejectedForModelStyle()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--single-interface" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--interface-name", "IFoo" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--response-wrapper", "none" }));

        var options = CliOptions.Parse(new[]
        {
            "spec.json", "--style", "interface", "--single-interface", "--interface-name", "IFoo", "-w", "none"
        });
        Assert.True(options.SingleInterface);
        Assert.False(options.UseResponseWrapper);
    }

    /// <summary>--help / --version 只打印信息，不要求也不读取文档来源。</summary>
    [Fact]
    public void HelpAndVersionShortCircuitSourceRequirement()
    {
        Assert.True(CliOptions.Parse(new[] { "--help" }).ShowHelp);
        Assert.True(CliOptions.Parse(new[] { "--version" }).ShowVersion);
    }

    /// <summary>枚举类选项的非法值统一报用法错误。</summary>
    [Fact]
    public void InvalidEnumValuesAreRejected()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--response-wrapper", "raw" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--integer-type", "short" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--fail-on", "errors" }));
    }

    /// <summary>
    /// --interface-name 同时是单文件模式的输出文件名：含路径分隔符 / 非标识符的取值必须直接报用法错误，
    /// 否则会生成 ../../evil.g.cs 这类写出输出目录之外的文件。
    /// </summary>
    [Fact]
    public void InterfaceNameMustBeValidIdentifier()
    {
        foreach (var name in new[] { "../../evil", "I Foo", "IFoo/Bar", "class", "" })
        {
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[]
            {
                "spec.json", "--style", "interface", "--single-interface", "--interface-name", name
            }));
        }

        var options = CliOptions.Parse(new[]
        {
            "spec.json", "--style", "interface", "--single-interface", "--interface-name", "IAllApi"
        });
        Assert.Equal("IAllApi", options.InterfaceName);
    }

    /// <summary>--timeout 必须是有限的正数秒：(0, 86400] 之外的取值（含 NaN / Infinity）统一报用法错误，不能崩进程。</summary>
    [Fact]
    public void TimeoutMustBeFinitePositiveSeconds()
    {
        foreach (var value in new[] { "0", "-1", "NaN", "Infinity", "-Infinity", "1e300", "abc" })
        {
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--timeout", value }));
        }

        Assert.Equal(TimeSpan.FromSeconds(45), CliOptions.Parse(new[] { "spec.json", "--timeout", "45" }).Timeout);
        Assert.Equal(
            TimeSpan.FromSeconds(86400),
            CliOptions.Parse(new[] { "spec.json", "--timeout", "86400" }).Timeout);
    }

    /// <summary>--output / --report 为空会让 Path.GetFullPath 抛原始异常，必须在解析阶段就报用法错误。</summary>
    [Fact]
    public void EmptyOutputAndReportAreUsageErrors()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--output", "" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--output", "   " }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--report", "" }));
    }

    /// <summary>--header 的名字必须是非空 token：非法名字会被 HttpClient 静默丢弃，下载就会少一个头。</summary>
    [Fact]
    public void HeaderNameMustBeToken()
    {
        foreach (var header in new[] { ":value", "X Api:value", "X(Api:value", "X\tApi:value" })
        {
            Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--header", header }));
        }

        // 只按第一个冒号切分：值里允许再出现冒号。
        var parsed = CliOptions.Parse(new[]
        {
            "spec.json", "--header", "Authorization: Bearer token", "--header", "X-Api:v:w"
        });
        Assert.Equal("Authorization", parsed.Headers[0].Key);
        Assert.Equal("Bearer token", parsed.Headers[0].Value);
        Assert.Equal("X-Api", parsed.Headers[1].Key);
        Assert.Equal("v:w", parsed.Headers[1].Value);
    }

    /// <summary>
    /// 选项缺值时不能把下一个选项吞成取值：<c>--output --check</c> 会把 <c>--check</c> 当目录名，
    /// 校验被悄悄取消。内联写法（<c>--output=-out</c>）仍然允许取值以 <c>-</c> 开头。
    /// </summary>
    [Fact]
    public void OptionValueCannotSwallowNextOption()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--output", "--check" }));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "-n", "--style", "interface" }));
        Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(new[] { "spec.json", "--style", "interface", "--single-interface", "--interface-name", "--" }));

        Assert.Equal("-out", CliOptions.Parse(new[] { "spec.json", "--output=-out" }).OutputDirectory);
    }

    /// <summary>小于一个 TimeSpan tick 的秒数会被舍入成 0，必须在解析阶段报用法错误而不是让 HttpClient 抛原始异常。</summary>
    [Fact]
    public void TimeoutSmallerThanOneTickIsUsageError()
        => Assert.Throws<CliUsageException>(() => CliOptions.Parse(new[] { "spec.json", "--timeout", "1e-9" }));
}
