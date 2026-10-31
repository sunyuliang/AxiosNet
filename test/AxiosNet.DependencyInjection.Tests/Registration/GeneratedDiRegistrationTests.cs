using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AxiosNet.SourceGen;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests.Registration;

/// <summary>
/// 源生成器产出的 <c>AddAxiosGeneratedApiInterfaces()</c>：直接驱动生成器，验证
/// 「有 DI 引用才生成」「只登记声明过的接口」「生成代码可编译、可注册、能解析出代理」。
/// </summary>
public sealed class GeneratedDiRegistrationTests
{
    private const string HintName = "AxiosNet.GeneratedDependencyInjection.g.cs";

    private const string ProbeSource = """
        using System.Threading.Tasks;
        using AxiosNet.WebAPI;

        namespace __NS__
        {
            public sealed class ProbeItem
            {
                public string Name { get; set; }
            }

            public interface IProbeAlpha
            {
                [Get("alpha")]
                Task<AxiosResponse<ProbeItem>> PingAsync();
            }

            public interface IProbeBeta
            {
                [Get("beta/{id}")]
                Task<AxiosResponse<ProbeItem>> PingAsync([Path] int id);
            }
        }
        """;

    [Fact]
    public void Schema_DeclaredInterfacesAreRegisteredAndResolvable()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = Run(ProbeSource.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var text = GeneratedDependencyInjection(result);
        Assert.Contains("AddApiInterface<global::" + ns + ".IProbeAlpha>(services);", text, StringComparison.Ordinal);
        Assert.Contains("AddApiInterface<global::" + ns + ".IProbeBeta>(services, siteName);", text, StringComparison.Ordinal);

        var assembly = LoadEmitted(output);
        var extensionType = assembly.GetType("AxiosNet.Generated.AxiosNetGeneratedDependencyInjection", true)!;
        var register = extensionType.GetMethod("AddAxiosGeneratedApiInterfaces", [typeof(IServiceCollection)])!;

        var services = new ServiceCollection();
        services.AddApiClient("https://api.example.com");
        register.Invoke(null, [services]);

        using var provider = services.BuildServiceProvider();
        var alpha = provider.GetRequiredService(assembly.GetType(ns + ".IProbeAlpha", true)!);
        var beta = provider.GetRequiredService(assembly.GetType(ns + ".IProbeBeta", true)!);

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.Same(alpha, provider.GetRequiredService(assembly.GetType(ns + ".IProbeAlpha", true)!));
    }

    [Fact]
    public void Schema_NoDependencyInjectionReference_EmitsNothing()
    {
        var (result, diagnostics, _) = Run(
            ProbeSource.Replace("__NS__", UniqueNamespace()),
            includeDependencyInjection: false);

        Assert.Empty(diagnostics);
        Assert.DoesNotContain(result.Results.Single().GeneratedSources, static source => source.HintName == HintName);
    }

    [Fact]
    public void Schema_ClosedGenericCallSiteIsNotRegisteredAsService()
    {
        var ns = UniqueNamespace();
        const string source = """
            using System.Threading.Tasks;
            using AxiosNet.WebAPI;

            namespace __NS__
            {
                public sealed class ProbeItem
                {
                    public string Name { get; set; }
                }

                public interface IProbeGamma<T>
                {
                    [Get("gamma/{id}")]
                    Task<AxiosResponse<ProbeItem>> PingAsync([Path] int id);
                }

                public interface IProbeDelta
                {
                    [Get("delta")]
                    Task<AxiosResponse<ProbeItem>> PingAsync();
                }

                public static class ProbeUsage
                {
                    public static object? Wire(ApiClient client) => client.Create<IProbeGamma<int>>();
                }
            }
            """;

        var (result, _, _) = Run(source.Replace("__NS__", ns));
        var text = GeneratedDependencyInjection(result);

        // 闭合泛型调用点会为 IProbeGamma<int> 生成代理，但它不该被顺手注册成容器服务。
        Assert.Contains("AddApiInterface<global::" + ns + ".IProbeDelta>(services);", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IProbeGamma", text, StringComparison.Ordinal);
    }

    private static string GeneratedDependencyInjection(GeneratorDriverRunResult result)
    {
        var source = result.Results.Single().GeneratedSources.Single(static s => s.HintName == HintName);
        return source.SourceText.ToString();
    }

    private static string UniqueNamespace() => "AxiosNet.GeneratedDiProbe.G" + Guid.NewGuid().ToString("N");

    private static (GeneratorDriverRunResult Result, ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Run(
        string source,
        bool includeDependencyInjection = true)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "AxiosNet.GeneratedDiTests." + Guid.NewGuid().ToString("N"),
            [tree],
            CreateReferences(includeDependencyInjection),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (driver.GetRunResult(), diagnostics, output);
    }

    private static MetadataReference[] CreateReferences(bool includeDependencyInjection)
    {
        var paths = new List<string>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            paths.AddRange(trustedPlatformAssemblies.Split(Path.PathSeparator));
        }

        paths.Add(typeof(ApiClient).Assembly.Location);
        paths.Add(typeof(IServiceCollection).Assembly.Location);
        if (includeDependencyInjection)
        {
            paths.Add(typeof(WebAPI.DependencyInjection.ApiInterfaceServiceCollectionExtensions).Assembly.Location);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !seen.Add(Path.GetFullPath(path)))
            {
                continue;
            }

            // TRUSTED_PLATFORM_ASSEMBLIES 里除了框架还列了测试程序自己的依赖：
            // 要模拟「没引用 DI 包」的消费方，必须把 DI 程序集从这里也剔掉。
            if (!includeDependencyInjection
                && string.Equals(Path.GetFileNameWithoutExtension(path), "AxiosNet.DependencyInjection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return [.. references];
    }

    private static Assembly LoadEmitted(Compilation output)
    {
        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static diagnostic => diagnostic.ToString())));

        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        return assembly;
    }
}
