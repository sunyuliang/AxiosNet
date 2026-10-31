using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using AxiosNet.SourceGen;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Internal.Reflection;
using AxiosNet.WebAPI.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 请求模型源生成器：直接驱动 <see cref="RequestModelGenerator"/>，验证生成代码可编译、可经模块初始化器注册，
/// 并与 JIT 反射读取器产出的声明逐字段一致（继承 / override / new 隐藏 / [ApiIgnore] / [JsonPropertyName] / multipart / 授权 / 静态头）。
/// 每个用例用唯一命名空间编译并加载，避免注册表里同一类型重复注册。
/// </summary>
public sealed class RequestModelGeneratorTests
{
    private static readonly MetadataReference[] References = CreateReferences();

    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    [Fact]
    public void GeneratedDeclaration_MatchesReflection()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(RichModels.Replace("__NS__", ns));

        Assert.Empty(diagnostics);
        Assert.Equal(3, ModelSources(result).Count());

        var assembly = LoadEmitted(output);
        AssertSameDeclaration(assembly, ns + ".ParityDerived");
        AssertSameDeclaration(assembly, ns + ".HiddenDerived");
        AssertSameDeclaration(assembly, ns + ".HiddenTypeDerived");
    }

    [Fact]
    public void GeneratedModel_ProducesSameBindingsAsReflection()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(RichModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        var modelType = assembly.GetType(ns + ".ParityDerived", throwOnError: true)!;
        var metadata = RequestModelMetadata.Get(modelType);

        Assert.Equal(HttpMethod.Get, metadata.Method);
        Assert.True(metadata.IsMultipart);
        Assert.Equal(["n", "file", "Note"], metadata.PartBindings.Select(static part => part.Name));

        var file = metadata.PartBindings[1];
        Assert.True(file.HasExplicitName);
        Assert.Equal("a.txt", file.FileName);
        Assert.Equal("text/plain", file.ContentType);

        Assert.Equal(ApiAuthorizationMode.Required, metadata.Authorization.Mode);
        Assert.Equal("Basic", metadata.Authorization.Scheme);

        var request = Activator.CreateInstance(modelType)!;
        SetProperty(request, "Id", 42);
        SetProperty(request, "BaseQuery", "b q");
        SetProperty(request, "Overridden", 5);
        SetProperty(request, "BaseHeader", "base-header");

        Assert.Equal("parity/42", metadata.ResolveUrl(request, formatter: null));

        var query = metadata.ResolveQuery(request, formatter: null);
        Assert.NotNull(query);
        Assert.Equal("b q", query["base-query"]);
        Assert.Equal(5, query["overridden"]);

        var config = new AxiosConfig();
        metadata.ApplyStaticHeaders(config);
        metadata.ApplyHeaders(request, config);
        Assert.Equal("base", config.Headers["X-Base"]);
        Assert.Equal("derived", config.Headers["X-Shared"]);
        Assert.Equal("1", config.Headers["X-Derived"]);
        Assert.Equal("base-header", config.Headers["X-Base-Member"]);
    }

    [Fact]
    public void Generator_ReportsDiagnosticsForUnexpressibleDeclarations()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(DiagnosticsModels.Replace("__NS__", ns));
        DiagnosticCatalogTests.AssertNoUnrenderedPlaceholders(diagnostics);

        var messagesById = diagnostics
            .GroupBy(static diagnostic => diagnostic.Id)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static diagnostic => diagnostic.GetMessage()).ToArray());

        Assert.Contains("MultiRouteModel", messagesById["AXA003"].Single());
        Assert.Contains("IgnoreConflictModel", messagesById["AXA006"].Single());
        Assert.Contains("AuthConflictModel", messagesById["AXA008"].Single());
        Assert.Contains(messagesById["AXA009"], static message => message.Contains("PrivateModel"));
        Assert.Contains(messagesById["AXA009"], static message => message.Contains("GenericModel"));
        Assert.Contains("CustomRouteAttribute", messagesById["AXA010"].Single());

        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Single(static d => d.Id == "AXA010").Severity);
        foreach (var error in diagnostics.Where(static d => d.Id != "AXA010"))
        {
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        }

        // 可静态表达的冲突仍会生成注册代码：错误由共享构建器在首次使用时抛出，文本与反射路径一致。
        var sources = ModelSources(result).ToArray();
        Assert.Equal(2, sources.Length);
        Assert.DoesNotContain(sources, static source => source.SourceText.ToString().Contains("MultiRouteModel"));

        var assembly = LoadEmitted(output);
        var authConflict = assembly.GetType(ns + ".AuthConflictModel", throwOnError: true)!;
        var authError = Assert.Throws<InvalidOperationException>(() => RequestModelMetadata.Get(authConflict));
        Assert.Contains("授权与匿名互斥", authError.Message);

        var ignoreConflict = assembly.GetType(ns + ".IgnoreConflictModel", throwOnError: true)!;
        var ignoreError = Assert.Throws<InvalidOperationException>(() => RequestModelMetadata.Get(ignoreConflict));
        Assert.Contains("二义性", ignoreError.Message);
    }

    [Fact]
    public void Generator_DisabledByProperty_EmitsNothing()
    {
        var source = SimpleModel.Replace("__NS__", UniqueNamespace());
        var disabled = new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" };

        var (result, diagnostics, _) = RunGenerator(source, disabled);

        Assert.Empty(diagnostics);
        Assert.Empty(result.Results.Single().GeneratedSources);
    }

    [Fact]
    public void Generator_DisabledUnderPublishAot_ReportsAxa012()
    {
        var source = SimpleModel.Replace("__NS__", UniqueNamespace());
        var disabledWithAot = new Dictionary<string, string>
        {
            ["build_property.AxiosNetSourceGen"] = "false",
            ["build_property.PublishAot"] = "true"
        };

        var (result, diagnostics, _) = RunGenerator(source, disabledWithAot);

        var warning = Assert.Single(diagnostics);
        Assert.Equal("AXA012", warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Empty(result.Results.Single().GeneratedSources);
    }

    [Fact]
    public void GeneratedValidationSource_CoversModelsAndInterfaces()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(ValidationModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var source = result.Results.Single().GeneratedSources.Single(
            static candidate => candidate.HintName.StartsWith("AxiosNet.Validation.", StringComparison.Ordinal));
        var text = source.SourceText.ToString();

        Assert.StartsWith("// <auto-generated/>", text);
        Assert.Contains("namespace AxiosNet.Generated", text);
        Assert.Contains("public static class AxiosNetValidation_", text);
        Assert.Contains("ValidateModels(", text);
        Assert.Contains("typeof(global::" + ns + ".ValidationModel)", text);
        Assert.Contains("ValidateInterfaces(", text);
        Assert.Contains("typeof(global::" + ns + ".IValidationApi)", text);

        // 生成的校验入口与代理 / 模型注册源一起编译：语法必须成立。
        Assert.NotNull(LoadEmitted(output));
    }

    [Fact]
    public void GeneratedValidationSource_BatchesLargeModelSets()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(BuildManyModels(ns, 101));
        Assert.Empty(diagnostics);

        var text = result.Results.Single().GeneratedSources.Single(
            static candidate => candidate.HintName.StartsWith("AxiosNet.Validation.", StringComparison.Ordinal)).SourceText.ToString();

        // 每 100 个一批：101 个模型切成 2 次调用，单方法体不会随模型数无限膨胀。
        Assert.Equal(2, text.Split("ApiClient.ValidateModels(").Length - 1);

        // 分批拼接后仍是合法代码（每批最后一个参数用 "));" 闭合）。
        Assert.NotNull(LoadEmitted(output));
    }

    [Fact]
    public void Generator_UsesStringHttpApi_AndRuntimeNormalizesRoute()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(StringRouteModel.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        var modelType = assembly.GetType(ns + ".RawRouteModel", throwOnError: true)!;
        var metadata = RequestModelMetadata.Get(modelType);

        Assert.Equal(HttpMethod.Post, metadata.Method);

        var request = Activator.CreateInstance(modelType)!;
        SetProperty(request, "Page", 3);
        Assert.Equal("things/", metadata.ResolveUrl(request, formatter: null));
        Assert.Equal(3, metadata.ResolveQuery(request, formatter: null)!["Page"]);
    }

    [Fact]
    public void PartialModel_WithRouteOnLaterPart_GeneratesFromMergedDeclaration()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(PartialModel.Replace("__NS__", ns));
        Assert.Empty(diagnostics);
        Assert.Single(ModelSources(result));

        var assembly = LoadEmitted(output);
        var modelType = assembly.GetType(ns + ".SplitModel", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(modelType, out _));
        AssertSameDeclaration(assembly, ns + ".SplitModel");

        var metadata = RequestModelMetadata.Get(modelType);
        Assert.Equal(HttpMethod.Get, metadata.Method);

        var request = Activator.CreateInstance(modelType)!;
        SetProperty(request, "Id", 9);
        SetProperty(request, "Page", 2);
        Assert.Equal("split/9", metadata.ResolveUrl(request, formatter: null));
        Assert.Equal(2, metadata.ResolveQuery(request, formatter: null)!["Page"]);
    }

    [Fact]
    public void HiddenMembers_WithLoweredAccessibility_MatchReflection()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(HiddenAccessibilityModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        foreach (var name in new[] { "PrivateDerived", "InternalDerived", "DifferentDerived", "StaticDerived" })
        {
            AssertSameDeclaration(assembly, ns + "." + name);
        }

        // private / internal 的 new 同样会挡住基类的可见性；new 成不同类型或 static 时不挡。
        Assert.Empty(RequestModelDeclarationReader.Read(assembly.GetType(ns + ".PrivateDerived", throwOnError: true)!).Members);
        Assert.Empty(RequestModelDeclarationReader.Read(assembly.GetType(ns + ".InternalDerived", throwOnError: true)!).Members);
        Assert.Single(RequestModelDeclarationReader.Read(assembly.GetType(ns + ".DifferentDerived", throwOnError: true)!).Members);
        Assert.Single(RequestModelDeclarationReader.Read(assembly.GetType(ns + ".StaticDerived", throwOnError: true)!).Members);
    }

    [Fact]
    public void ModelWithGenericBase_MatchesReflection()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(GenericBaseModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        AssertSameDeclaration(assembly, ns + ".GenericDerived");
    }

    [Fact]
    public void NestedDynamicMemberType_MatchesReflection()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(NestedDynamicModel.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        var modelType = assembly.GetType(ns + ".DynamicModel", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(modelType, out var generated));
        var reflected = RequestModelDeclarationReader.Read(modelType);

        Assert.Equal(reflected.Members.Count, generated.Members.Count);
        for (var i = 0; i < reflected.Members.Count; i++)
        {
            Assert.Equal(reflected.Members[i].Name, generated.Members[i].Name);
            Assert.Same(reflected.Members[i].ValueType, generated.Members[i].ValueType);
        }

        // CLR 层面 dynamic == object：生成代码中的 typeof(List<dynamic>) 与反射拿到的 List<object> 是同一个类型。
        Assert.Equal(typeof(List<object>), generated.Members.Single(static member => member.Name == "Items").ValueType);
    }

    [Fact]
    public void TupleElementNames_DoNotAffectHiding()
    {
        var ns = UniqueNamespace();
        var (_, diagnostics, output) = RunGenerator(TupleHidingModel.Replace("__NS__", ns));
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        var modelType = assembly.GetType(ns + ".TupleDerived", throwOnError: true)!;

        // 元组元素名不参与 CLR 类型标识：(int X, int Y) 与 (int A, int B) 同为 ValueTuple<int,int>，
        // 派生类的 new 成员隐藏基类成员（与反射 GetProperties 一致，只保留最派生的一份）。
        Assert.Single(RequestModelDeclarationReader.Read(modelType).Members);
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(modelType, out var generated));
        Assert.Single(generated.Members);
        Assert.Equal("TupleDerived.Value", generated.Members[0].Description);

        var instance = Activator.CreateInstance(modelType)!;
        modelType.GetProperty("Value")!.SetValue(instance, (1, 2));
        Assert.Equal((1, 2), generated.Members[0].GetValue!(instance));

        // 元组语法与显式写出的 ValueTuple 是同一个 CLR 类型：同样要隐藏基类成员。
        var spellingType = assembly.GetType(ns + ".SpellingDerived", throwOnError: true)!;
        Assert.Single(RequestModelDeclarationReader.Read(spellingType).Members);
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(spellingType, out var spellingGenerated));
        Assert.Single(spellingGenerated.Members);
        Assert.Equal("SpellingDerived.Value", spellingGenerated.Members[0].Description);
    }

    private static void AssertSameDeclaration(Assembly assembly, string typeName)
    {
        var modelType = assembly.GetType(typeName, throwOnError: true)!;

        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(modelType, out var generated));
        var reflected = RequestModelDeclarationReader.Read(modelType);

        Assert.Same(modelType, generated.ModelType);
        Assert.Equal(reflected.MethodName, generated.MethodName);
        Assert.Equal(reflected.Path, generated.Path);
        Assert.Equal(reflected.Multipart, generated.Multipart);
        Assert.Equal(reflected.AllowAnonymous, generated.AllowAnonymous);
        Assert.Equal(reflected.AuthorizeScheme, generated.AuthorizeScheme);
        Assert.Equal(reflected.ImplementsApiRequest, generated.ImplementsApiRequest);
        Assert.Equal(reflected.Error, generated.Error);
        Assert.Equal(reflected.StaticHeaders, generated.StaticHeaders);
        Assert.Equal(reflected.Members.Count, generated.Members.Count);

        for (var i = 0; i < reflected.Members.Count; i++)
        {
            AssertSameMember(reflected.Members[i], generated.Members[i]);
        }

        var instance = Activator.CreateInstance(modelType)!;
        var expectedValues = new object?[reflected.Members.Count];
        for (var i = 0; i < reflected.Members.Count; i++)
        {
            var expected = reflected.Members[i];
            var actual = generated.Members[i];
            expectedValues[i] = SampleValue(expected.ValueType, expected.Description!);
            FindDeclaredProperty(modelType, expected.Description!).SetValue(instance, expectedValues[i]);
        }

        for (var i = 0; i < reflected.Members.Count; i++)
        {
            Assert.NotNull(reflected.Members[i].GetValue);
            Assert.NotNull(generated.Members[i].GetValue);
            Assert.Equal(expectedValues[i], reflected.Members[i].GetValue!(instance));
            Assert.Equal(expectedValues[i], generated.Members[i].GetValue!(instance));
        }
    }

    private static void AssertSameMember(RequestModelMemberDeclaration expected, RequestModelMemberDeclaration actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.FieldName, actual.FieldName);
        Assert.Same(expected.ValueType, actual.ValueType);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Ignore, actual.Ignore);
        Assert.Equal(expected.Path, actual.Path);

        // 反射读到的 [Path] / [Query] / [Header] 省略名字时是 ""（构造器默认值）；生成器读到的是 null。
        // 两者在构建器里都表示「用成员名」，语义一致。
        Assert.Equal(NormalizeOptionalName(expected.PathName), NormalizeOptionalName(actual.PathName));
        Assert.Equal(expected.Query, actual.Query);
        Assert.Equal(NormalizeOptionalName(expected.QueryName), NormalizeOptionalName(actual.QueryName));
        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(NormalizeOptionalName(expected.HeaderName), NormalizeOptionalName(actual.HeaderName));
        Assert.Equal(expected.HeaderCollection, actual.HeaderCollection);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Part, actual.Part);
        Assert.Equal(expected.PartName, actual.PartName);
        Assert.Equal(expected.PartFileName, actual.PartFileName);
        Assert.Equal(expected.PartContentType, actual.PartContentType);
    }

    private static string? NormalizeOptionalName(string? name) => string.IsNullOrEmpty(name) ? null : name;

    /// <summary>只取请求模型注册源：同一驱动还会输出对象形状源（<c>AxiosNet.Shape.*</c>），计数断言不应受其影响。</summary>
    private static IEnumerable<GeneratedSourceResult> ModelSources(GeneratorDriverRunResult result)
        => result.Results.Single().GeneratedSources.Where(
            static source => source.HintName.StartsWith("AxiosNet.RequestModel.", StringComparison.Ordinal));

    /// <summary>按声明的定位文本（<c>DeclaringType.Name.PropertyName</c>）找到对应层级的属性：new 隐藏时基类与派生类各有一个。</summary>
    private static PropertyInfo FindDeclaredProperty(Type modelType, string description)
    {
        var dot = description.LastIndexOf('.');
        var declaringName = description[..dot];
        var propertyName = description[(dot + 1)..];

        for (var type = modelType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.Name == declaringName
                && type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } property)
            {
                return property;
            }
        }

        throw new InvalidOperationException($"找不到 {description} 对应的属性。");
    }

    private static object SampleValue(Type type, string description)
    {
        if (type == typeof(string))
        {
            return "value:" + description;
        }

        if (type == typeof(object))
        {
            return new object();
        }

        if (type == typeof(int))
        {
            return 42;
        }

        if (type == typeof(Stream))
        {
            return new MemoryStream([1, 2, 3]);
        }

        if (typeof(IDictionary<string, string>).IsAssignableFrom(type))
        {
            return new Dictionary<string, string> { ["x"] = "y" };
        }

        throw new InvalidOperationException($"测试源没有为 {type} 准备样值。");
    }

    private static void SetProperty(object instance, string name, object value)
        => instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(instance, value);

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

    private static (GeneratorDriverRunResult Result, ImmutableArray<Diagnostic> Diagnostics, Compilation Output) RunGenerator(
        string source,
        IReadOnlyDictionary<string, string>? options = null,
        string? assemblyName = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            // 默认随机：用例之间互不干扰（注册表按程序集隔离）；快照用例必须给固定名——
            // 生成的 ValidateAll 类名里带程序集名，随机名会让基线每次都不一样。
            assemblyName ?? "AxiosNet.GeneratorTests." + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: new TestOptionsProvider(options ?? NoOptions));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (driver.GetRunResult(), diagnostics, output);
    }

    /// <summary>
    /// 增量管线的意义是「编辑别处时不重算」：模型 / 形状 / 接口全部用 record + <see cref="EquatableArray{T}"/> 就是为了这个。
    /// 这里在同一编译上追加一个与生成无关的类型，再跑一遍，断言没有任何源码被重新生成。
    /// 接口管线曾经返回 <c>ImmutableArray</c>（按底层数组引用比较），导致每次编辑都判成「已修改」、增量缓存整体失效。
    /// 回归时的表现是 Roslyn 4.1 的跟踪设施抛 <c>InvalidOperationException: Unexpected value '(Cached, Modified)'</c>。
    /// </summary>
    [Fact]
    public void IncrementalPipeline_ReusesEverySourceWhenUnrelatedCodeChanges()
    {
        var ns = UniqueNamespace();
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet;
            using AxiosNet.WebAPI;

            namespace __NS__;

            [AxiosShape]
            public sealed class Item
            {
                public string Name { get; set; } = string.Empty;
            }

            [Get("items")]
            public sealed class ListRequest : IApiRequest<Item>
            {
                [Query]
                public string Q { get; set; } = string.Empty;
            }

            public interface IApi
            {
                [Get("items/{id}")]
                Task<string> GetAsync(int id, CancellationToken ct);
            }
            """.Replace("__NS__", ns);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "AxiosNet.IncrementalTests." + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: new TestOptionsProvider(NoOptions),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var firstRun = driver.GetRunResult().Results.Single();
        Assert.True(
            firstRun.GeneratedSources.Length >= 4,
            "首次运行至少要产出模型 / 形状 / 接口 / 校验四份源码，否则下面的「全部命中缓存」没有意义："
            + string.Join("、", firstRun.GeneratedSources.Select(static source => source.HintName)));

        var unrelated = CSharpSyntaxTree.ParseText("public sealed class Unrelated { public int X { get; set; } }", parseOptions);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(unrelated));

        Assert.True(
            driver.GetRunResult().Results.Single().TrackedSteps.TryGetValue("SourceOutput", out var outputs),
            "增量步骤里没有 SourceOutput（Roslyn 版本变化？），这条护栏需要跟着更新。");

        var rerun = outputs
            .SelectMany(static step => step.Outputs)
            .Where(static output => output.Reason is not (IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged))
            .Select(static output => output.Reason.ToString())
            .Distinct()
            .ToList();

        Assert.True(rerun.Count == 0, "无关编辑后仍有源码被重新生成：" + string.Join("、", rerun));
    }

    private static string UniqueNamespace() => "AxiosNet.GeneratedTests.G" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// 声明里的字符串与成员名会原样写进生成代码：路由 / 头声明含引号反斜杠、属性名是关键字时，
    /// 必须仍生成可编译代码，且运行时读回的字符串与声明逐字符相同（字面量转义不能只做到「能编译」）。
    /// </summary>
    [Fact]
    public void PathologicalIdentifiersAndStrings_EmitCompilableCodeWithExactValues()
    {
        var ns = UniqueNamespace();
        var source = """
            using System.Text.Json.Serialization;
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public sealed class Weird
            {
                [JsonPropertyName("a\"b\\c\u00e9")]
                public string Quoted { get; set; } = string.Empty;

                public string @class { get; set; } = "c";

                public string @record { get; set; } = "r";

                public string @value { get; set; } = "v";
            }

            [Headers("X-Quote: a\"b")]
            [Get("items/\"quoted\"/{id}")]
            public sealed class WeirdRequest : IApiRequest<object>
            {
                [Path]
                public int Id { get; set; }

                [Query]
                public Weird Data { get; set; } = new Weird();
            }

            public interface IWeirdApi
            {
                [Get("w")]
                Task<string> FetchAsync(string @class, CancellationToken ct);
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.NotEmpty(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);
        var request = assembly.GetType(ns + ".WeirdRequest", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(request, out var model));
        Assert.Equal("items/\"quoted\"/{id}", model!.Path);
        Assert.Equal("X-Quote: a\"b", model.StaticHeaders.Single());

        // 关键字属性名必须以 @ 前缀写进取值委托：拿实例真读一遍才证明绑定到的是那个属性。
        var weirdType = assembly.GetType(ns + ".Weird", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetShape(weirdType, out var shape));
        var instance = Activator.CreateInstance(weirdType)!;
        var values = shape!.Members.ToDictionary(
            static member => member.Name,
            member => (string?)member.GetValue(instance),
            StringComparer.Ordinal);

        Assert.Equal("c", values["class"]);
        Assert.Equal("r", values["record"]);
        Assert.Equal("v", values["value"]);
        Assert.Equal("a\"b\\c\u00e9", shape.Members.Single(static member => member.Name == "Quoted").FieldName);
    }

    /// <summary>
    /// 生成器快照：把一次编译产出的全部源码逐字节钉住。生成代码是对外契约（用户会读它、发布说明会引用它），
    /// 改动就该出现在 diff 里，而不是悄悄变了没人发现。
    /// 更新基线：<c>AXIOS_UPDATE_SNAPSHOTS=1 dotnet test --filter GeneratedSources_MatchSnapshot</c>
    /// </summary>
    [Fact]
    public void GeneratedSources_MatchSnapshot()
    {
        var (result, diagnostics, _) = RunGenerator(
            SnapshotModels.Replace("__NS__", "AxiosNet.SnapshotProbe"),
            assemblyName: "AxiosNet.SnapshotProbe");
        Assert.Empty(diagnostics);

        var actual = SnapshotText(result);
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("AXIOS_UPDATE_SNAPSHOTS") == "1")
        {
            // CI 上禁止重写基线：否则快照用例永远通过，等于没装这道闸。
            Assert.True(
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                "CI 环境请勿用 AXIOS_UPDATE_SNAPSHOTS 重写快照基线。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"缺少快照基线 {path}：用 AXIOS_UPDATE_SNAPSHOTS=1 重新生成。");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }

    private static string SnapshotText(GeneratorDriverRunResult result)
    {
        var builder = new StringBuilder();
        foreach (var source in result.Results.Single().GeneratedSources.OrderBy(static s => s.HintName, StringComparer.Ordinal))
        {
            builder.Append("//// ").Append(source.HintName).Append('\n');
            builder.Append(source.SourceText.ToString().TrimEnd()).Append('\n').Append('\n');
        }

        // 统一成 LF：基线文件在 Windows 上会被 core.autocrlf 转成 CRLF，比对前两边都得归一，
        // 否则用例只在「谁的换行恰好一致」时通过。
        return builder.ToString().Replace("\r\n", "\n");
    }

    private static string SnapshotPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AxiosNet.sln")))
            {
                return Path.Combine(directory.FullName, "test", "AxiosNet.Tests", "WebAPI", "Aot", "Snapshots", "GeneratedSources.snap.txt");
            }
        }

        throw new InvalidOperationException("找不到仓库根（AxiosNet.sln），无法定位快照基线。");
    }

    /// <summary>快照输入：一份模型 + 一个接口，覆盖路径 / 查询 / 头 / 正文与两种返回形态。</summary>
    private const string SnapshotModels = """
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet.WebAPI;

        namespace __NS__
        {
            public sealed class SnapshotItem
            {
                public string Name { get; set; } = string.Empty;
            }

            [Get("items/{id}")]
            public sealed class SnapshotRequest : IApiRequest<SnapshotItem>
            {
                [Path]
                public int Id { get; set; }

                [Query("q")]
                public string? Search { get; set; }

                [Header("X-Token")]
                public string? Token { get; set; }
            }

            public interface ISnapshotApi
            {
                [Get("items/{id}")]
                Task<AxiosResponse<SnapshotItem>> GetAsync([Path] int id, CancellationToken cancellationToken);

                [Post("items")]
                Task<SnapshotItem?> CreateAsync([Body] SnapshotItem item, CancellationToken cancellationToken);
            }
        }
        """;

    /// <summary>造 N 个互不相同的请求模型，用于验证校验入口的分批拼接。</summary>
    private static string BuildManyModels(string ns, int count)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using AxiosNet.WebAPI;");
        builder.Append("namespace ").Append(ns).AppendLine(";");
        for (var i = 0; i < count; i++)
        {
            builder.Append("[Get(\"items\")] public sealed class M").Append(i).AppendLine(" : IApiRequest<object> { }");
        }

        return builder.ToString();
    }

    private static MetadataReference[] CreateReferences()
    {
        var paths = new List<string>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            paths.AddRange(trustedPlatformAssemblies.Split(Path.PathSeparator));
        }

        paths.Add(typeof(AxiosMetadata).Assembly.Location);

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

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new TestOptions(null);

        private readonly TestOptions _globalOptions;

        public TestOptionsProvider(IReadOnlyDictionary<string, string> globalOptions)
            => _globalOptions = new TestOptions(globalOptions);

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public TestOptions(IReadOnlyDictionary<string, string>? values)
            => _values = values ?? new Dictionary<string, string>();

        public override bool TryGetValue(string key, out string value)
            => _values.TryGetValue(key, out value!);
    }

    private const string RichModels = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Text.Json.Serialization;
        using AxiosNet.WebAPI;

        namespace __NS__;

        [Headers("X-Base: base", null!, "X-Shared: base")]
        public class ParityBase
        {
            [Query("base-query")]
            public string? BaseQuery { get; set; }

            [Query("overridden")]
            public virtual int Overridden { get; set; }

            [ApiIgnore]
            [Query("ignored")]
            public virtual string? Ignored { get; set; }

            [Header("X-Base-Member")]
            public string? BaseHeader { get; set; }
        }

        [Get("parity/{id}")]
        [Headers("X-Derived: 1", "X-Shared: derived")]
        [Authorize("Basic")]
        [Multipart]
        public class ParityDerived : ParityBase, IApiRequest<object>
        {
            [Path]
            public int Id { get; set; }

            [Query("overridden")]
            public override int Overridden { get; set; }

            [ApiIgnore]
            public override string? Ignored { get; set; }

            [JsonPropertyName("n")]
            public string? Name { get; set; }

            [Part("file", FileName = "a.txt", ContentType = "text/plain")]
            public Stream? File { get; set; }

            [HeaderCollection]
            public IDictionary<string, string>? ExtraHeaders { get; set; }

            public string? Note { get; set; }
        }

        [AllowAnonymous]
        public class HiddenBase
        {
            public string? Value { get; set; }
        }

        [Get("hidden")]
        public class HiddenDerived : HiddenBase
        {
            public new string? Value { get; set; }
        }

        public class HiddenTypeBase
        {
            public string? Value { get; set; }
        }

        [Get("hidden-type")]
        public class HiddenTypeDerived : HiddenTypeBase
        {
            public new object? Value { get; set; }
        }
        """;

    private const string DiagnosticsModels = """
        using System.Net.Http;
        using AxiosNet.WebAPI;

        namespace __NS__;

        public sealed class CustomRouteAttribute : HttpApiAttribute
        {
            public CustomRouteAttribute(string path) : base(HttpMethod.Get, path)
            {
            }
        }

        [Get("multi/a")]
        [Post("multi/b")]
        public class MultiRouteModel
        {
        }

        public static class Container
        {
            [Get("private")]
            private sealed class PrivateModel
            {
                [Query]
                public int Id { get; set; }
            }
        }

        [CustomRoute("custom")]
        public class CustomRouteModel
        {
        }

        [Get("ignore")]
        public class IgnoreConflictModel
        {
            [ApiIgnore]
            [Path]
            public int Id { get; set; }
        }

        [Get("auth")]
        [Authorize("Basic")]
        [AllowAnonymous]
        public class AuthConflictModel
        {
        }

        [Get("generic")]
        public class GenericModel<T>
        {
            [Query]
            public int Id { get; set; }
        }
        """;

    private const string SimpleModel = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        [Get("disabled")]
        public class DisabledModel
        {
            [Query]
            public int Id { get; set; }
        }
        """;

    private const string ValidationModels = """
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet.WebAPI;

        namespace __NS__;

        [Get("items/{id}")]
        public sealed class ValidationModel : IApiRequest<object>
        {
            [Path]
            public int Id { get; set; }
        }

        public interface IValidationApi
        {
            [Get("items/{id}")]
            Task<AxiosResponse<object>> GetAsync([Path] int id, CancellationToken cancellationToken);
        }
        """;

    private const string StringRouteModel = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        [HttpApi("  post ", "  things/  ")]
        public class RawRouteModel
        {
            [Query]
            public int Page { get; set; }
        }
        """;

    // 路由特性写在后一段声明上：生成器必须用合并后的符号，不能固定在 references[0] 上找特性。
    private const string PartialModel = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        public partial class SplitModel : IApiRequest<object>
        {
            [Query]
            public int Page { get; set; }
        }

        [Get("split/{id}")]
        public partial class SplitModel
        {
            [Path]
            public int Id { get; set; }
        }
        """;

    private const string HiddenAccessibilityModels = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        public class PrivateBase { public string? Value { get; set; } }

        [Get("private-new")]
        public class PrivateDerived : PrivateBase
        {
            private new string? Value { get; set; }
        }

        public class InternalBase { public string? Value { get; set; } }

        [Get("internal-new")]
        public class InternalDerived : InternalBase
        {
            internal new string? Value { get; set; }
        }

        public class DifferentBase { public string? Value { get; set; } }

        [Get("different")]
        public class DifferentDerived : DifferentBase
        {
            private new object? Value { get; set; }
        }

        public class StaticBase { public string? Value { get; set; } }

        [Get("static")]
        public class StaticDerived : StaticBase
        {
            public static new string? Value { get; set; }
        }
        """;

    private const string NestedDynamicModel = """
        using System.Collections.Generic;
        using AxiosNet.WebAPI;

        namespace __NS__;

        [Get("dynamic")]
        public class DynamicModel : IApiRequest<object>
        {
            public dynamic? Token { get; set; }

            public List<dynamic>? Items { get; set; }
        }
        """;

    private const string TupleHidingModel = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        public class TupleBase
        {
            public virtual (int A, int B) Value { get; set; }
        }

        [Get("tuple-hiding")]
        public class TupleDerived : TupleBase
        {
            public new (int X, int Y) Value { get; set; }
        }

        public class SpellingBase
        {
            public virtual (int A, int B) Value { get; set; }
        }

        [Get("tuple-spelling")]
        public class SpellingDerived : SpellingBase
        {
            public new System.ValueTuple<int, int> Value { get; set; }
        }
        """;

    private const string GenericBaseModels = """
        using AxiosNet.WebAPI;

        namespace __NS__;

        public class GenericBase<T>
        {
            [Query("value")]
            public T Value { get; set; } = default!;
        }

        [Get("generic-base")]
        public class GenericDerived : GenericBase<int>
        {
            [Query]
            public int Page { get; set; }
        }
        """;
}
