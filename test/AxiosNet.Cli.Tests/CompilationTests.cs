using AxiosNet.SourceGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.Cli.Tests;

/// <summary>
/// 生成结果的最终验收：两种形态都能单独编译，并被 AxiosNet 的源生成器完整接住
/// （请求模型数量 = 操作数量 / 代理数量 = 接口数量，且不产生任何错误诊断）。
/// </summary>
public sealed class CompilationTests
{
    /// <summary>语法树解析选项：跟随最新语言版本，与生成代码使用的语义一致。</summary>
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>model 形态的每个请求类都能单独编译，源生成器为每个操作产出一个请求模型。</summary>
    [Theory]
    [InlineData("petstore.json")]
    [InlineData("swagger2.json")]
    [InlineData("adversarial.json")]
    [InlineData("mini-3.1.yaml")]
    public void ModelRequestsCompileAndSatisfySourceGenerator(string fixture)
    {
        using var run = CliHarness.Run(fixture);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("model", run.Style);

        var (driver, outputErrors) = Compile(run);
        Assert.True(outputErrors.Length == 0, Describe(outputErrors));

        var models = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .Where(source => source.HintName.StartsWith("AxiosNet.RequestModel.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(run.Operations, models.Count);
    }

    /// <summary>interface 形态的代理接口同样能编译，源生成器为每个分组产出一个代理实现。</summary>
    [Theory]
    [InlineData("petstore.json")]
    [InlineData("swagger2.json")]
    [InlineData("adversarial.json")]
    [InlineData("mini-3.1.yaml")]
    public void InterfaceProxiesCompileAndSatisfySourceGenerator(string fixture)
    {
        using var run = CliHarness.Run(fixture, "--style", "interface");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("interface", run.Style);

        var (driver, outputErrors) = Compile(run);
        Assert.True(outputErrors.Length == 0, Describe(outputErrors));

        var proxies = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .Where(source => source.HintName.StartsWith("AxiosNet.InterfaceProxy.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(run.GroupCount, proxies.Count);
    }

    /// <summary>
    /// 对抗性命名（枚举常量成员与常量类同名、请求类撞运行库 <c>ApiRequest&lt;T&gt;</c>）生成的代码也必须能编译：
    /// 这两类错误只会表现为 CS0542 / 遮蔽泛型基类，文本断言看不出问题，必须真编译一次。
    /// </summary>
    [Fact]
    public void AdversarialNamesCompile()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/api": {
                  "get": {
                    "operationId": "api",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Status" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "Status": { "type": "string", "enum": ["status-values", "other"] } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        Assert.Equal(0, run.ExitCode);

        var (_, outputErrors) = Compile(run);
        Assert.True(outputErrors.Length == 0, Describe(outputErrors));
    }

    /// <summary>
    /// 关键字转义前缀 <c>@</c> 不能掩盖同名冲突：<c>@class2</c> 与 <c>class2</c> 在 C# 里就是同一个名字（CS0100）；
    /// 枚举值恰好与枚举类型同名则是 CS0542。这两类问题只有真编译才会暴露。
    /// </summary>
    [Fact]
    public void EscapedKeywordAndEnumMemberNamesCompile()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "parameters": [
                      { "name": "class", "in": "query", "schema": { "type": "string" } },
                      { "name": "class2", "in": "query", "schema": { "type": "string" } },
                      { "name": "class", "in": "header", "schema": { "type": "string" } }
                    ],
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Status" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "Status": { "type": "string", "enum": ["Status", "Other"] } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        Assert.Equal(0, run.ExitCode);

        var (_, outputErrors) = Compile(run);
        Assert.True(outputErrors.Length == 0, Describe(outputErrors));
    }

    /// <summary>把生成文件编译成程序集并跑一遍源生成器，返回驱动与增量编译后的错误诊断。</summary>
    private static (GeneratorDriver Driver, Diagnostic[] OutputErrors) Compile(CliRun run)
    {
        var trees = run.FileNames
            .Where(name => name.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(name => CSharpSyntaxTree.ParseText(run.Read(name), ParseOptions))
            .ToArray();
        Assert.NotEmpty(trees);

        var compilation = CSharpCompilation.Create(
            "AxiosNet.Cli.Tests.Generated." + Guid.NewGuid().ToString("N"),
            trees,
            CreateReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(compileErrors.Length == 0, Describe(compileErrors));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var generatorErrors = diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(generatorErrors.Length == 0, Describe(generatorErrors));

        var outputErrors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        return (driver, outputErrors);
    }

    /// <summary>把诊断拼成多行文本，失败时作为断言消息定位问题。</summary>
    private static string Describe(Diagnostic[] diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    /// <summary>收集运行时可信平台程序集与 AxiosNet.WebAPI 本体，作为编译引用的最小集合。</summary>
    private static MetadataReference[] CreateReferences()
    {
        var paths = new List<string>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            paths.AddRange(trustedPlatformAssemblies.Split(Path.PathSeparator));
        }

        paths.Add(typeof(AxiosNet.WebAPI.ApiClient).Assembly.Location);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                references.Add(MetadataReference.CreateFromFile(fullPath));
            }
        }

        return references.ToArray();
    }
}
