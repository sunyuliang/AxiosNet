using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using AxiosNet.SourceGen;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 接口代理源生成器：真实代理类可编译、经模块初始化器注册，并按声明路径发包
/// （继承接口 / 方法级授权覆盖 / 静态头 / multipart / SSE 流式 / 闭合泛型调用点）；
/// 无法静态表达的声明在编译期出诊断，开放泛型只在没有闭合调用点时提示。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class InterfaceGeneratorTests
{
    [Fact]
    public async Task GeneratedProxy_SendsGetPostMultipartAndStreamRequests()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(RichInterface.Replace("__NS__", ns));

        Assert.Empty(diagnostics);
        Assert.Equal(2, ProxySources(result).Count());

        var assembly = LoadEmitted(output);
        var richType = assembly.GetType(ns + ".IRichApi", throwOnError: true)!;
        var baseType = assembly.GetType(ns + ".IBaseApi", throwOnError: true)!;

        Assert.True(GeneratedMetadataRegistry.TryGetInterface(baseType, out _));
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(richType, out var entry));
        entry!.Validate();
        Assert.Equal(5, entry.GetMethods().Length);

        await using var server = TestHttpServer.Start(request => request.Path == "/stream"
            ? new TestResponse(
                StatusCode: 200,
                Body: Encoding.UTF8.GetBytes("data: {\"name\":\"alice\"}\n\ndata: {\"name\":\"bob\"}\n\n"),
                ContentType: "text/event-stream")
            : new TestResponse(StatusCode: 200, Body: Encoding.UTF8.GetBytes("""{"name":"alice"}""")));

        using var client = new ApiClient(server.Url) { AccessTokenProvider = new FixedTokenProvider() };
        var proxy = CreateProxy(client, richType);

        var fetched = await InvokeAsync(richType, proxy, "GetAsync", 7, "x1", new Dictionary<string, string> { ["X-Extra"] = "1" }, default(CancellationToken));
        var getRequest = Assert.Single(server.Received);
        Assert.Equal("GET", getRequest.Method);
        Assert.Equal("/items/7", getRequest.Path);
        Assert.Equal("q=x1", getRequest.Query);
        Assert.Equal("v1", getRequest.Headers["X-Api"]);
        Assert.Equal("2", getRequest.Headers["X-Derived"]);
        Assert.Equal("yes", getRequest.Headers["X-Method"]);
        Assert.Equal("1", getRequest.Headers["X-Extra"]);
        Assert.Equal("Bearer tok", getRequest.Headers["Authorization"]);
        Assert.Equal("alice", ReadString(fetched!, "Name"));

        await InvokeAsync(richType, proxy, "CreateAsync", CreateInstance(assembly, ns + ".Patient", "bob"), true, default(CancellationToken));
        var postRequest = server.Received.Last();
        Assert.Equal("POST", postRequest.Method);
        Assert.Equal("/items", postRequest.Path);
        Assert.Equal("notify=true", postRequest.Query);
        Assert.False(postRequest.Headers.ContainsKey("Authorization"));
        Assert.Contains("\"bob\"", Encoding.UTF8.GetString(postRequest.Body));

        await InvokeAsync(richType, proxy, "UploadAsync", 3, new MemoryStream([1, 2, 3]), "note-1", default(CancellationToken));
        var uploadRequest = server.Received.Last();
        Assert.Equal("POST", uploadRequest.Method);
        Assert.Equal("/upload/3", uploadRequest.Path);
        Assert.StartsWith("multipart/form-data", uploadRequest.Headers["Content-Type"]);
        var uploadBody = Encoding.UTF8.GetString(uploadRequest.Body);
        Assert.Contains("name=file; filename=a.bin", uploadBody);
        Assert.Contains("name=note", uploadBody);

        var streamResponse = await InvokeAsync(richType, proxy, "StreamAsync", default(CancellationToken));
        var sseRequest = server.Received.Last();
        Assert.Equal("GET", sseRequest.Method);
        Assert.Equal("/stream", sseRequest.Path);
        var items = await CollectAsync(((AxiosResponse)streamResponse!).Data!);
        Assert.Equal(new[] { "alice", "bob" }, items.Select(item => ReadString(item!, "Name")));

        // 继承来的方法：接口级静态头与「最近的接口级授权」同样生效。
        var inherited = await InvokeAsync(richType, proxy, "BaseAsync", 5, default(CancellationToken));
        var baseRequest = server.Received.Last();
        Assert.Equal("/base/5", baseRequest.Path);
        Assert.Equal("Basic tok", baseRequest.Headers["Authorization"]);
        Assert.Equal("alice", ReadString(((AxiosResponse)inherited!).Data!, "Name"));
    }

    [Fact]
    public async Task ClosedGenericInterface_GeneratesProxyFromCallSite()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(ClosedGenericInterface.Replace("__NS__", ns));

        Assert.Empty(diagnostics);
        Assert.Single(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);
        var repoType = assembly.GetType(ns + ".IRepo`1", throwOnError: true)!
            .MakeGenericType(assembly.GetType(ns + ".User", throwOnError: true)!);

        Assert.True(GeneratedMetadataRegistry.TryGetInterface(repoType, out var entry));
        entry!.Validate();

        await using var server = TestHttpServer.Start(_ => new TestResponse(StatusCode: 200, Body: Encoding.UTF8.GetBytes("""{"name":"carol"}""")));
        using var client = new ApiClient(server.Url);
        var boot = assembly.GetType(ns + ".Boot", throwOnError: true)!;
        var proxy = boot.GetMethod("Make")!.Invoke(null, [client])!;

        Assert.True(repoType.IsInstanceOfType(proxy));

        var user = await InvokeAsync(repoType, proxy, "GetAsync", 11, default(CancellationToken));
        Assert.Equal("/repos/11", server.Received.Single().Path);
        Assert.Equal("carol", ReadString(user!, "Name"));
    }

    [Fact]
    public void BrokenInterfaces_ReportDiagnosticsAndSkipEmission()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(BrokenInterfaces.Replace("__NS__", ns));

        var messagesById = diagnostics
            .GroupBy(static diagnostic => diagnostic.Id)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static diagnostic => diagnostic.GetMessage()).ToArray());

        Assert.Contains("IPropertyApi", messagesById["AXA001"].Single());
        Assert.Contains(messagesById["AXA002"], static message => message.Contains("泛型方法"));
        Assert.Contains(messagesById["AXA002"], static message => message.Contains("ref"));
        Assert.Contains(messagesById["AXA002"], static message => message.Contains("CancellationToken"));
        Assert.Contains(messagesById["AXA002"], static message => message.Contains("非 public 的抽象成员"));
        Assert.Contains(messagesById["AXA003"], static message => message.Contains("OtherAsync"));
        Assert.Contains(messagesById["AXA003"], static message => message.Contains("多个路由特性"));
        Assert.Contains(messagesById["AXA003"], static message => message.Contains("空白"));
        Assert.Contains("void", messagesById["AXA004"].Single());
        Assert.Contains(messagesById["AXA005"], static message => message.Contains("只用于"));
        Assert.Contains(messagesById["AXA005"], static message => message.Contains("帧格式值 99"));
        Assert.Contains("IAuthConflictApi", messagesById["AXA008"].Single());
        Assert.Contains("IHiddenApi", messagesById["AXA009"].Single());
        Assert.Contains("CustomRouteAttribute", messagesById["AXA010"].Single());
        Assert.Contains("IOpenApi", messagesById["AXA011"].Single());

        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Single(static d => d.Id == "AXA010").Severity);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Single(static d => d.Id == "AXA011").Severity);

        // 合法接口生成代理；接口级授权冲突可静态表达，注册后由共享构建器在首次使用时抛出。
        var sources = result.Results.Single().GeneratedSources;
        Assert.Equal(2, sources.Length);
        Assert.Contains(sources, static source => source.SourceText.ToString().Contains("IGoodApi"));

        // 合法接口能编译并注册。
        var assembly = LoadEmitted(output);
        var goodType = assembly.GetType(ns + ".IGoodApi", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(goodType, out var entry));
        entry!.Validate();
    }

    [Fact]
    public void Generator_DisabledByProperty_EmitsNoInterfaceProxies()
    {
        var source = RichInterface.Replace("__NS__", UniqueNamespace());
        var disabled = new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" };

        var (result, diagnostics, _) = RunGenerator(source, disabled);

        Assert.Empty(diagnostics);
        Assert.Empty(result.Results.Single().GeneratedSources);
    }

    [Fact]
    public async Task ReferencedAssemblyGeneratedInterface_IsReusedWithoutDuplicateRegistration()
    {
        var libNs = UniqueNamespace();
        var libBytes = EmitLibraryWithGenerator(libNs, out var libSource);
        var libAssembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(libBytes));
        RuntimeHelpers.RunModuleConstructor(libAssembly.ManifestModule.ModuleHandle);
        var libType = libAssembly.GetType(libNs + ".ILibApi", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(libType, out _));

        // 被引用程序集已生成并注册：使用方不再生成（否则模块初始化器重复注册会抛错），直接复用。
        var consumer = CompileConsumer(libNs, libBytes);
        Assert.Empty(consumer.Diagnostics);
        Assert.Empty(consumer.Result.Results.Single().GeneratedSources);

        var consumerAssembly = LoadEmitted(consumer.Compilation);
        await AssertProxySendsRequest(consumerAssembly, libType);

        // 对照：被引用程序集没跑生成器（例如库关闭了 AxiosNetSourceGen）时，使用方按调用点生成并注册。
        var plainLib = RunGenerator(libSource, new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" });
        Assert.Empty(plainLib.Diagnostics);
        Assert.Empty(plainLib.Result.Results.Single().GeneratedSources);
        var plainLibBytes = EmitToBytes(plainLib.Output);
        var plainLibAssembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(plainLibBytes));
        RuntimeHelpers.RunModuleConstructor(plainLibAssembly.ManifestModule.ModuleHandle);
        var plainLibType = plainLibAssembly.GetType(libNs + ".ILibApi", throwOnError: true)!;
        Assert.False(GeneratedMetadataRegistry.TryGetInterface(plainLibType, out _));

        var fallback = CompileConsumer(libNs, plainLibBytes);
        Assert.Empty(fallback.Diagnostics);
        Assert.Single(fallback.Result.Results.Single().GeneratedSources);
        var fallbackAssembly = LoadEmitted(fallback.Compilation);
        await AssertProxySendsRequest(fallbackAssembly, plainLibType);
    }

    [Fact]
    public void ReferencedAssemblyClosedGenericProxy_IsReusedWithoutDuplicateRegistration()
    {
        var libNs = UniqueNamespace();
        var libSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public sealed record LibUser(string Name);

            public interface IRepo<T>
            {
                [Get("repos/{id}")]
                Task<T> GetAsync([Path] int id, CancellationToken cancellationToken);
            }

            public static class LibBoot
            {
                public static object Make(ApiClient client) => client.Create<IRepo<LibUser>>();
            }
            """.Replace("__NS__", libNs);

        var (libResult, libDiagnostics, libCompilation) = RunGenerator(libSource);
        Assert.Empty(libDiagnostics);
        Assert.Single(libResult.Results.Single().GeneratedSources);

        var libBytes = EmitToBytes(libCompilation);
        var libAssembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(libBytes));
        RuntimeHelpers.RunModuleConstructor(libAssembly.ManifestModule.ModuleHandle);
        var repoType = libAssembly.GetType(libNs + ".IRepo`1", throwOnError: true)!
            .MakeGenericType(libAssembly.GetType(libNs + ".LibUser", throwOnError: true)!);
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(repoType, out _));

        var consumer = RunGenerator(
            """
            using AxiosNet.WebAPI;

            namespace __NS__;

            public static class Boot
            {
                public static object Make(ApiClient client) => client.Create<__REPO__>();
            }
            """.Replace("__NS__", UniqueNamespace()).Replace("__REPO__", libNs + ".IRepo<" + libNs + ".LibUser>"),
            references: [MetadataReference.CreateFromImage(libBytes)]);

        Assert.Empty(consumer.Diagnostics);
        Assert.Empty(consumer.Result.Results.Single().GeneratedSources);

        // 加载使用方程序集：若重复注册，模块初始化器会在这里抛错。
        Assert.NotNull(LoadEmitted(consumer.Output));
    }

    [Fact]
    public async Task SiblingAssemblies_RegisteringSameInterface_KeepFirstAndDoNotThrow()
    {
        var contractsNs = UniqueNamespace();
        var contractsSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public interface ISharedApi
            {
                [Get("shared")]
                Task<string> GetAsync(CancellationToken cancellationToken);
            }
            """.Replace("__NS__", contractsNs);

        // 契约程序集不跑生成器：两个兄弟程序集各自在调用点生成同名代理（不同程序集里的同名类型），
        // 模块初始化器都会注册同一个接口；后注册的必须被忽略，而不是抛「重复注册」让启动失败。
        var contracts = RunGenerator(
            contractsSource,
            new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" });
        Assert.Empty(contracts.Diagnostics);
        Assert.Empty(contracts.Result.Results.Single().GeneratedSources);
        var contractsBytes = EmitToBytes(contracts.Output);

        var first = CompileSibling("A", contractsNs, contractsBytes);
        var second = CompileSibling("B", contractsNs, contractsBytes);
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Single(first.Result.Results.Single().GeneratedSources);
        Assert.Single(second.Result.Results.Single().GeneratedSources);

        var contractsAssembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(contractsBytes));
        RuntimeHelpers.RunModuleConstructor(contractsAssembly.ManifestModule.ModuleHandle);
        var sharedType = contractsAssembly.GetType(contractsNs + ".ISharedApi", throwOnError: true)!;

        LoadEmitted(first.Compilation);
        LoadEmitted(second.Compilation);

        Assert.True(GeneratedMetadataRegistry.TryGetInterface(sharedType, out _));

        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 200,
            Body: Encoding.UTF8.GetBytes("shared")));
        using var client = new ApiClient(server.Url);
        var proxy = CreateProxy(client, sharedType);
        var result = await InvokeAsync(sharedType, proxy, "GetAsync", default(CancellationToken));

        Assert.Equal("shared", result);
        Assert.Equal("/shared", Assert.Single(server.Received).Path);

        static (GeneratorDriverRunResult Result, ImmutableArray<Diagnostic> Diagnostics, Compilation Compilation) CompileSibling(
            string suffix,
            string contractsNamespace,
            byte[] contractsImage)
            => RunGenerator(
                """
                using AxiosNet.WebAPI;

                namespace __NS__;

                public static class Boot
                {
                    public static object Make(ApiClient client) => client.Create<__SHARED__>();
                }
                """.Replace("__NS__", UniqueNamespace() + suffix).Replace("__SHARED__", contractsNamespace + ".ISharedApi"),
                references: [MetadataReference.CreateFromImage(contractsImage)]);
    }

    [Fact]
    public async Task GeneratedAndReflectionProxies_ProduceIdenticalRequests()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator("""
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet.WebAPI;

            namespace __NS__;

            [Headers("X-Shared: base", "X-Derived: base")]
            [Authorize("Base")]
            public interface IBase
            {
                [Get("base")]
                Task<string> BaseAsync();
            }

            [Headers("X-Shared: left", "X-Derived: left")]
            [Authorize("Left")]
            public interface ILeft : IBase
            {
                [Get("left")]
                Task<string> LeftAsync();
            }

            [Headers("X-Sibling: 1")]
            public interface ISibling : IBase
            {
                [Get("sibling")]
                Task<string> SiblingAsync();
            }

            [Headers("X-Shared: root")]
            public interface IRoot : ILeft, ISibling
            {
                [Get("root/{id}")]
                Task<string> GetAsync([Path] int id, [Query] int q, CancellationToken cancellationToken);
            }
            """.Replace("__NS__", ns));

        Assert.Empty(diagnostics);
        var assembly = LoadEmitted(output);
        var rootType = assembly.GetType(ns + ".IRoot", throwOnError: true)!;

        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 200,
            Body: Encoding.UTF8.GetBytes("ok")));
        using var client = new ApiClient(server.Url) { AccessTokenProvider = new FixedTokenProvider() };

        var generated = CreateProxy(client, rootType);
        var reflected = ApiInterfaceProxy.Create(rootType, client);

        await InvokeAsync(rootType, generated, "GetAsync", 5, 6, default(CancellationToken));
        await InvokeAsync(rootType, reflected, "GetAsync", 5, 6, default(CancellationToken));

        var generatedRequest = server.Received[0];
        var reflectedRequest = server.Received[1];

        // 派生接口的同名静态头必须压过基接口，两条路径都如此。
        Assert.Equal("root", generatedRequest.Headers["X-Shared"]);
        Assert.Equal("root", reflectedRequest.Headers["X-Shared"]);
        Assert.Equal("left", generatedRequest.Headers["X-Derived"]);
        Assert.Equal("left", reflectedRequest.Headers["X-Derived"]);

        // 最近的接口级授权：ILeft 比 IBase 更近，两条路径都取 ILeft 的 scheme。
        Assert.Equal("Left tok", generatedRequest.Headers["Authorization"]);
        Assert.Equal("Left tok", reflectedRequest.Headers["Authorization"]);
        Assert.Equal("1", generatedRequest.Headers["X-Sibling"]);
        Assert.Equal("1", reflectedRequest.Headers["X-Sibling"]);

        Assert.Equal(generatedRequest.Method, reflectedRequest.Method);
        Assert.Equal(generatedRequest.Path, reflectedRequest.Path);
        Assert.Equal(generatedRequest.Query, reflectedRequest.Query);
    }

    [Fact]
    public async Task InterfaceNestedInGenericOuter_GeneratesFromClosedCallSite()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator("""
            using System.Threading;
            using System.Threading.Tasks;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public sealed class Container<T>
            {
                public interface IInnerApi
                {
                    [Get("inner")]
                    Task<string> GetAsync(CancellationToken cancellationToken);
                }
            }

            public static class Boot
            {
                public static object Make(ApiClient client) => client.Create<Container<int>.IInnerApi>();
            }
            """.Replace("__NS__", ns));

        // 开放声明无法静态命名（Outer<T> 未闭合），AXA011 由同一编译里的闭合调用点抵消。
        Assert.Empty(diagnostics);
        Assert.Single(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);
        var innerType = assembly.GetType(ns + ".Container`1+IInnerApi", throwOnError: true)!.MakeGenericType(typeof(int));
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(innerType, out var entry));
        entry!.Validate();
        await AssertProxySendsRequest(assembly, innerType, "/inner");
    }

    [Fact]
    public void UnregisteredInterface_MissingMetadataThrowsAotException()
    {
        var error = Assert.Throws<AxiosAotMetadataException>(() => AxiosMetadata.GetInterfaceMethod(typeof(IUnregisteredApi), 0));

        Assert.Contains("Native AOT", error.Message);
        Assert.Contains("RegisterInterface", error.Message);
    }

    private static byte[] EmitLibraryWithGenerator(string ns, out string source)
    {
        source = LibraryInterface.Replace("__NS__", ns);
        var (result, diagnostics, compilation) = RunGenerator(source);
        Assert.Empty(diagnostics);
        Assert.Single(result.Results.Single().GeneratedSources);
        return EmitToBytes(compilation);
    }

    private static (GeneratorDriverRunResult Result, ImmutableArray<Diagnostic> Diagnostics, Compilation Compilation) CompileConsumer(
        string libNs,
        byte[] libBytes)
    {
        var source = """
            using AxiosNet.WebAPI;

            namespace __NS__;

            public static class Boot
            {
                public static object Make(ApiClient client) => client.Create<__LIB__>();
            }
            """.Replace("__NS__", UniqueNamespace()).Replace("__LIB__", libNs + ".ILibApi");

        return RunGenerator(source, references: [MetadataReference.CreateFromImage(libBytes)]);
    }

    private static async Task AssertProxySendsRequest(Assembly assembly, Type interfaceType, string expectedPath = "/lib")
    {
        _ = assembly;
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 200,
            Body: Encoding.UTF8.GetBytes("lib")));
        using var client = new ApiClient(server.Url);

        var proxy = CreateProxy(client, interfaceType);
        var result = await InvokeAsync(interfaceType, proxy, "GetAsync", default(CancellationToken)).ConfigureAwait(false);

        Assert.Equal("lib", result);
        var request = Assert.Single(server.Received);
        Assert.Equal("GET", request.Method);
        Assert.Equal(expectedPath, request.Path);
    }

    private static object CreateProxy(ApiClient client, Type interfaceType)
    {
        var create = typeof(ApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(static method => method.Name == "Create" && method.IsGenericMethodDefinition);

        return create.MakeGenericMethod(interfaceType).Invoke(client, null)!;
    }

    private static async Task<object?> InvokeAsync(Type interfaceType, object proxy, string name, params object?[] arguments)
    {
        // Type.GetMethod 不含继承接口上的方法：按接口图逐层找声明。
        var method = interfaceType.GetInterfaces().Prepend(interfaceType)
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .FirstOrDefault(declared => declared.Name == name)
            ?? throw new InvalidOperationException($"{interfaceType.Name} 没有 {name} 方法。");
        var task = (Task)method.Invoke(proxy, arguments)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static async Task<List<object?>> CollectAsync(object asyncEnumerable)
    {
        var streamType = asyncEnumerable.GetType().GetInterfaces()
            .First(static type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        var enumeratorType = typeof(IAsyncEnumerator<>).MakeGenericType(streamType.GetGenericArguments()[0]);
        var enumerator = streamType.GetMethod("GetAsyncEnumerator")!.Invoke(asyncEnumerable, [default(CancellationToken)])!;

        var items = new List<object?>();
        var moveNext = enumeratorType.GetMethod("MoveNextAsync")!;
        var current = enumeratorType.GetProperty("Current")!;
        try
        {
            while ((bool)(await AwaitValueTaskAsync(moveNext.Invoke(enumerator, null)!).ConfigureAwait(false))!)
            {
                items.Add(current.GetValue(enumerator));
            }
        }
        finally
        {
            await AwaitValueTaskAsync(typeof(IAsyncDisposable).GetMethod("DisposeAsync")!.Invoke(enumerator, null)!).ConfigureAwait(false);
        }

        return items;
    }

    private static async Task<object?> AwaitValueTaskAsync(object valueTask)
    {
        var task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static string? ReadString(object instance, string property)
        => (string?)instance.GetType().GetProperty(property)!.GetValue(instance);

    private static object CreateInstance(Assembly assembly, string typeName, params object?[] constructorArguments)
        => Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!, constructorArguments)!;

    private static Assembly LoadEmitted(Compilation output)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(EmitToBytes(output)));
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        return assembly;
    }

    private static byte[] EmitToBytes(Compilation output)
    {
        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static diagnostic => diagnostic.ToString())));

        return stream.ToArray();
    }

    private static (GeneratorDriverRunResult Result, ImmutableArray<Diagnostic> Diagnostics, Compilation Output) RunGenerator(
        string source,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<MetadataReference>? references = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "AxiosNet.InterfaceGeneratorTests." + Guid.NewGuid().ToString("N"),
            [tree],
            references is null ? CreateReferences() : [.. CreateReferences(), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: new TestOptionsProvider(options ?? NoOptions));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (driver.GetRunResult(), diagnostics, output);
    }

    private static string UniqueNamespace() => "AxiosNet.InterfaceGenerated.G" + Guid.NewGuid().ToString("N");

    /// <summary>只取接口代理源：同一驱动还会输出对象形状源（<c>AxiosNet.Shape.*</c>），计数断言不应受其影响。</summary>
    private static IEnumerable<GeneratedSourceResult> ProxySources(GeneratorDriverRunResult result)
        => result.Results.Single().GeneratedSources.Where(
            static source => source.HintName.StartsWith("AxiosNet.InterfaceProxy.", StringComparison.Ordinal));

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

    private const string LibraryInterface = """
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet.WebAPI;

        namespace __NS__;

        public interface ILibApi
        {
            [Get("lib")]
            Task<string> GetAsync(CancellationToken cancellationToken);
        }
        """;

    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

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

    private sealed class FixedTokenProvider : IAccessTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => new ValueTask<string?>("tok");
    }

    private const string RichInterface = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet;
        using AxiosNet.WebAPI;

        namespace __NS__;

        public sealed record Patient(string Name);

        [Headers("X-Api: v1")]
        [AllowAnonymous]
        public interface IBaseApi
        {
            [Get("base/{id}")]
            Task<AxiosResponse<Patient>> BaseAsync([Path] int id, CancellationToken cancellationToken);
        }

        [Headers("X-Derived: 2")]
        [Authorize("Basic")]
        public interface IRichApi : IBaseApi
        {
            [Get("items/{id}")]
            [Headers("X-Method: yes")]
            [Authorize("Bearer")]
            Task<Patient> GetAsync(
                [Path] int id,
                [Query("q")] string? q,
                [HeaderCollection] IDictionary<string, string>? extra,
                CancellationToken cancellationToken);

            [Post("items")]
            [AllowAnonymous]
            Task CreateAsync([Body] Patient patient, [Query] bool notify, CancellationToken cancellationToken);

            [Post("upload/{id}")]
            [Multipart]
            Task<Patient> UploadAsync(
                [Path] int id,
                [Part("file", FileName = "a.bin", ContentType = "application/octet-stream")] Stream file,
                string note,
                CancellationToken cancellationToken);

            [Get("stream")]
            [Stream(AxiosStreamFormat.ServerSentEvents)]
            Task<AxiosResponse<IAsyncEnumerable<Patient>>> StreamAsync(CancellationToken cancellationToken);
        }
        """;

    private const string ClosedGenericInterface = """
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet.WebAPI;

        namespace __NS__;

        public sealed record User(string Name);

        public interface IRepo<T>
        {
            [Get("repos/{id}")]
            Task<T> GetAsync([Path] int id, CancellationToken cancellationToken);
        }

        public static class Boot
        {
            public static object Make(ApiClient client) => client.Create<IRepo<User>>();
        }
        """;

    private const string BrokenInterfaces = """
        using System;
        using System.Collections.Generic;
        using System.Net.Http;
        using System.Threading.Tasks;
        using AxiosNet;
        using AxiosNet.WebAPI;

        namespace __NS__;

        public sealed class CustomRouteAttribute : HttpApiAttribute
        {
            public CustomRouteAttribute(string path) : base(HttpMethod.Get, path)
            {
            }
        }

        public interface IGoodApi
        {
            [Get("good")]
            Task GetAsync();
        }

        public interface IPropertyApi
        {
            string Name { get; }

            [Get("property")]
            Task GetAsync();
        }

        public interface IMixedApi
        {
            [Get("mixed")]
            Task GetAsync();

            Task OtherAsync();
        }

        public interface IMultiRouteApi
        {
            [Get("multi-a")]
            [Post("multi-b")]
            Task GetAsync();
        }

        public interface IBlankRouteApi
        {
            [Get(" ")]
            Task GetAsync();
        }

        public interface ICustomRouteApi
        {
            [CustomRoute("custom")]
            Task GetAsync();
        }

        public interface IReturnApi
        {
            [Get("return")]
            void GetAsync();
        }

        public interface IStreamShapeApi
        {
            [Get("stream-shape")]
            [Stream]
            Task<string> GetAsync();
        }

        public interface IStreamFormatApi
        {
            [Get("stream-format")]
            [Stream((AxiosStreamFormat)99)]
            Task<AxiosResponse<IAsyncEnumerable<string>>> GetAsync();
        }

        public interface IGenericMethodApi
        {
            [Get("generic")]
            Task<T> GetAsync<T>();
        }

        public interface IRefParameterApi
        {
            [Get("ref")]
            Task GetAsync(ref int id);
        }

        public interface ICancellationTokenApi
        {
            [Get("ct")]
            Task GetAsync([Query] System.Threading.CancellationToken cancellationToken);
        }

        public interface INonPublicMemberApi
        {
            internal Task HiddenAsync();

            [Get("non-public-member")]
            Task GetAsync();
        }

        [Authorize]
        [AllowAnonymous]
        public interface IAuthConflictApi
        {
            [Get("auth")]
            Task GetAsync();
        }

        public static class Container
        {
            private interface IHiddenApi
            {
                [Get("hidden")]
                Task GetAsync();
            }
        }

        public interface IOpenApi<T>
        {
            [Get("open")]
            Task<T> GetAsync();
        }
        """;

    public interface IUnregisteredApi
    {
        Task GetAsync();
    }
}
