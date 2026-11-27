using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using AxiosNet.Metadata;
using AxiosNet.SourceGen;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI.Internal.Metadata;
using AxiosNet.WebAPI.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 双实现对拍：同一个接口声明，源生成器（Roslyn 符号）烘焙的声明与运行期反射读出的声明必须逐字段相等。
/// </summary>
/// <remarks>
/// 两条路径此后共用 <see cref="InterfaceMethodMetadata.BuildCore"/>，所以「声明相等」是行为一致的充分前提；
/// 反过来，声明一旦漂移，AOT 与 JIT 会静默地按不同路由 / 绑定发包，而这种分叉在各自的单元测试里看不出来。
/// 比对走全字段转储而非逐字段断言：声明加了新字段会自动纳入，不会漏。
/// </remarks>
[Collection(GlobalSerializationCollection.Name)]
public sealed class JitAotParityTests
{
    /// <summary>同一声明经两条路径读出的 <see cref="InterfaceDeclaration"/> 逐字段相等。</summary>
    [Fact]
    public void Declarations_FromGeneratorAndReflection_AreFieldEqual()
    {
        var ns = UniqueNamespace();
        var (diagnostics, output) = RunGenerator(ParityInterface.Replace("__NS__", ns));

        Assert.DoesNotContain(diagnostics, static d => d.Severity == DiagnosticSeverity.Error);

        var assembly = LoadEmitted(output);
        var interfaceType = assembly.GetType(ns + ".IParityApi", throwOnError: true)!;

        Assert.True(
            GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry),
            "生成代码没有登记 IParityApi：模块初始化器没跑，或生成器没有产出这个接口的代理。");

        var baked = entry!.Declaration;
        Assert.NotEmpty(baked.Methods);

        // 方法集合先守恒再谈字段：只比「同一方法上的字段」发现不了「生成器把某个方法整条漏发」。
        Assert.Equal(ChainMethodNames(interfaceType), BakedMethodNames(baked));

        var mirrored = MirrorThroughReflection(interfaceType, baked);

        // 逐方法比对而不是整份字符串：失败时能直接指出是哪个方法漂了。
        Assert.Equal(baked.StaticHeaders, mirrored.StaticHeaders);
        Assert.Equal(baked.AuthorizeScheme, mirrored.AuthorizeScheme);
        Assert.Equal(baked.AllowAnonymous, mirrored.AllowAnonymous);
        Assert.Equal(baked.Error, mirrored.Error);
        Assert.Equal(baked.Methods.Count, mirrored.Methods.Count);

        for (var i = 0; i < baked.Methods.Count; i++)
        {
            // ReturnTypeText 只喂两条「返回类型不受支持」的错误文案，不参与绑定：反射侧能直接格式化 Type，
            // 生成期没有同形的符号格式化器。这里钉住「生成期不烘焙它」，再把它抹平后比其余全部字段。
            Assert.Null(baked.Methods[i].ReturnTypeText);
            mirrored.Methods[i].ReturnTypeText = null;

            Assert.Equal(Dump(baked.Methods[i]), Dump(mirrored.Methods[i]));
        }
    }

    /// <summary>转储必须真的覆盖到声明内容：全字段被跳过时上面那条断言会变成永远成立。</summary>
    [Fact]
    public void Dump_CoversDeclarationFields()
    {
        var ns = UniqueNamespace();
        var (_, output) = RunGenerator(ParityInterface.Replace("__NS__", ns));
        var assembly = LoadEmitted(output);
        var interfaceType = assembly.GetType(ns + ".IParityApi", throwOnError: true)!;

        Assert.True(GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry));

        var dump = Dump(entry!.Declaration.Methods);

        // 每个方法的六项事实都要出现；少一项说明转储或声明形状变了。
        foreach (var method in entry.Declaration.Methods)
        {
            Assert.Contains("Name=\"" + method.Name + "\"", dump, StringComparison.Ordinal);
        }

        Assert.Contains("MethodName=\"GET\"", dump, StringComparison.Ordinal);
        Assert.Contains("Path=\"items/{id}\"", dump, StringComparison.Ordinal);
        Assert.Contains("Multipart=True", dump, StringComparison.Ordinal);
        Assert.Contains("ContentType=\"application/json\"", dump, StringComparison.Ordinal);
        Assert.Contains("QueryCollectionFormat=AxiosCollectionFormat.Multi", dump, StringComparison.Ordinal);
        Assert.Contains("StreamFormat=AxiosStreamFormat.ServerSentEvents", dump, StringComparison.Ordinal);
        // 参数声明必须真的进了转储：空列表对空列表也能「相等」，这是最容易漏的一层。
        Assert.Contains("Parameters=[InterfaceParameterDeclaration{", dump, StringComparison.Ordinal);
        Assert.Contains("PartName=\"file\"", dump, StringComparison.Ordinal);
        Assert.Contains("HeaderCollection=True", dump, StringComparison.Ordinal);
        Assert.Contains("Ignore=True", dump, StringComparison.Ordinal);
        Assert.Contains("Body=True", dump, StringComparison.Ordinal);
    }

    /// <summary>请求模型声明同样双写：生成器烘焙 vs <see cref="RequestModelDeclarationReader"/> 读出，逐字段相等。</summary>
    [Fact]
    public void RequestModelDeclarations_FromGeneratorAndReflection_AreFieldEqual()
    {
        var ns = UniqueNamespace();
        var (diagnostics, output) = RunGenerator(ParityModels.Replace("__NS__", ns));

        Assert.DoesNotContain(diagnostics, static d => d.Severity == DiagnosticSeverity.Error);

        var assembly = LoadEmitted(output);

        foreach (var name in RequestModelNames)
        {
            var modelType = assembly.GetType(ns + "." + name, throwOnError: true)!;

            Assert.True(
                GeneratedMetadataRegistry.TryGetRequestModel(modelType, out var baked),
                $"生成代码没有登记请求模型 {name}。");

            var mirrored = RequestModelDeclarationReader.Read(modelType);

            // 类级字段整份比（成员列表折成计数），成员再逐项比：两层都要相等才算同形。
            Assert.Equal(
                Dump(baked, collapseMembers: true, blankAsNull: OptionalNameDeclarations),
                Dump(mirrored, collapseMembers: true, blankAsNull: OptionalNameDeclarations));

            for (var i = 0; i < baked!.Members.Count; i++)
            {
                Assert.Equal(
                    Dump(baked.Members[i], blankAsNull: OptionalNameDeclarations),
                    Dump(mirrored.Members[i], blankAsNull: OptionalNameDeclarations));
            }
        }
    }

    /// <summary>请求模型侧的转储同样要真的钻到成员层。</summary>
    [Fact]
    public void Dump_CoversRequestModelFields()
    {
        var ns = UniqueNamespace();
        var (_, output) = RunGenerator(ParityModels.Replace("__NS__", ns));
        var assembly = LoadEmitted(output);

        var orderType = assembly.GetType(ns + ".OrderRequest", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(orderType, out var order));

        var uploadType = assembly.GetType(ns + ".UploadRequest", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetRequestModel(uploadType, out var upload));

        var orderDump = Dump(order!, collapseMembers: false);
        Assert.Contains("MethodName=\"POST\"", orderDump, StringComparison.Ordinal);
        Assert.Contains("Path=\"orders/{id}\"", orderDump, StringComparison.Ordinal);
        Assert.Contains("ImplementsApiRequest=True", orderDump, StringComparison.Ordinal);
        Assert.Contains("ContentType=\"application/json\"", orderDump, StringComparison.Ordinal);
        Assert.Contains("StaticHeaders=[\"X-Model: 1\"]", orderDump, StringComparison.Ordinal);
        Assert.Contains("PathName=null", orderDump, StringComparison.Ordinal);
        Assert.Contains("NameFromAttribute=True", orderDump, StringComparison.Ordinal);
        Assert.Contains("Ignore=True", orderDump, StringComparison.Ordinal);
        Assert.Contains("GetValue=<delegate>", orderDump, StringComparison.Ordinal);

        var uploadDump = Dump(upload!, collapseMembers: false);
        Assert.Contains("Multipart=True", uploadDump, StringComparison.Ordinal);
        Assert.Contains("PartName=\"file\"", uploadDump, StringComparison.Ordinal);
        Assert.Contains("PartFileName=\"a.bin\"", uploadDump, StringComparison.Ordinal);
        Assert.Contains("PartContentType=\"application/octet-stream\"", uploadDump, StringComparison.Ordinal);
    }

    /// <summary>归一是真的在起作用：反射侧原文是空串，抹平后才是 <c>null</c>。</summary>
    [Fact]
    public void OptionalNames_NormalizationTurnsBlankIntoNull()
    {
        var ns = UniqueNamespace();
        var (_, output) = RunGenerator(ParityModels.Replace("__NS__", ns));
        var assembly = LoadEmitted(output);

        var orderType = assembly.GetType(ns + ".OrderRequest", throwOnError: true)!;
        var mirrored = RequestModelDeclarationReader.Read(orderType);

        var raw = Dump(mirrored);
        var normalized = Dump(mirrored, blankAsNull: OptionalNameDeclarations);

        // 反射侧三处无参命名都是空串：不加归一就会与烘焙声明的 null 一一对不上。
        Assert.Contains("PathName=\"\"", raw, StringComparison.Ordinal);
        Assert.Contains("QueryName=\"\"", raw, StringComparison.Ordinal);
        Assert.Contains("HeaderName=\"\"", raw, StringComparison.Ordinal);

        Assert.Contains("PathName=null", normalized, StringComparison.Ordinal);
        Assert.Contains("QueryName=null", normalized, StringComparison.Ordinal);
        Assert.Contains("HeaderName=null", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("Name=\"\"", normalized, StringComparison.Ordinal);
    }

    /// <summary>归一的范围只能是「空串等价于缺省」的那三处：放宽到别的字段会掩盖真实分叉。</summary>
    [Fact]
    public void OptionalNames_NormalizationCoversOnlyBlankTolerantNames()
    {
        Assert.Equal(
            ["HeaderName", "PathName", "QueryName"],
            OptionalNameDeclarations.OrderBy(static name => name, StringComparer.Ordinal));

        foreach (var name in OptionalNameDeclarations)
        {
            var property = typeof(RequestModelMemberDeclaration)
                .GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property!.PropertyType);
        }

        // 空白在 Part 上是构建错误、在 Body 上另有含义：都不许进集。
        Assert.DoesNotContain(nameof(RequestModelMemberDeclaration.PartName), OptionalNameDeclarations);
        Assert.DoesNotContain(nameof(RequestModelMemberDeclaration.PartFileName), OptionalNameDeclarations);
        Assert.DoesNotContain(nameof(RequestModelMemberDeclaration.PartContentType), OptionalNameDeclarations);
        Assert.DoesNotContain(nameof(RequestModelMemberDeclaration.BodyName), OptionalNameDeclarations);
    }

    /// <summary>把声明按「公开属性 + 集合元素」递归转成文本；委托成员只留占位，类型只留全名。</summary>
    /// <param name="value">任意声明对象。</param>
    /// <param name="collapseMembers">为 <c>true</c> 时把成员声明列表折成「几个成员」，让整份声明比对专注类级字段。</param>
    /// <param name="blankAsNull">需要抹平空串的命名属性集合；<c>null</c> 表示逐字比对。</param>
    private static string Dump(object? value, bool collapseMembers = false, HashSet<string>? blankAsNull = null)
    {
        var builder = new StringBuilder();
        Write(builder, value, collapseMembers, blankAsNull);
        return builder.ToString();
    }

    /// <summary>转储的递归实现。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="value">当前节点。</param>
    /// <param name="collapseMembers">为 <c>true</c> 时折叠成员声明列表。</param>
    /// <param name="blankAsNull">需要抹平空串的属性名集合；只对 <c>string</c> 值生效。</param>
    private static void Write(StringBuilder builder, object? value, bool collapseMembers, HashSet<string>? blankAsNull)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;

            case string text:
                builder.Append('"').Append(text).Append('"');
                return;

            case Type type:
                // 两条路径的 Type 实例来自同一个已加载程序集，但按全名比对才不绑定加载上下文。
                builder.Append(type.FullName ?? type.Name);
                return;

            case Delegate:
                builder.Append("<delegate>");
                return;

            case Enum:
                builder.Append(value.GetType().Name).Append('.').Append(value);
                return;

            case bool or int or long or double or float or decimal or char:
                builder.Append(value);
                return;

            case IEnumerable sequence:
                // 成员声明列表折叠成计数：它由调用方逐项比对，整份转储只保留「有几个」。
                if (collapseMembers && IsMemberList(value))
                {
                    builder.Append('[').Append(sequence.Cast<object>().Count()).Append(" members]");
                    return;
                }

                builder.Append('[');
                var first = true;
                foreach (var item in sequence)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    Write(builder, item, collapseMembers, blankAsNull);
                }

                builder.Append(']');
                return;
        }

        var objectType = value.GetType();
        builder.Append(objectType.Name).Append('{');

        // 属性名排序：两条路径的对象内部布局与成员顺序不必相同。
        var properties = objectType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < properties.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var propertyValue = properties[i].GetValue(value);

            // 只有入选的命名属性、且值确实是空串才抹成 null：别的空值留在原处照常比。
            if (blankAsNull is not null
                && propertyValue is string blank
                && blank.Length == 0
                && blankAsNull.Contains(properties[i].Name))
            {
                propertyValue = null;
            }

            builder.Append(properties[i].Name).Append('=');
            Write(builder, propertyValue, collapseMembers, blankAsNull);
        }

        builder.Append('}');
    }

    /// <summary>是否「请求模型成员声明列表」：只有它按计数折叠，静态头一类的字符串列表照常逐项比。</summary>
    /// <param name="value">集合节点。</param>
    private static bool IsMemberList(object value)
        => value.GetType().IsGenericType
           && value.GetType().GetGenericArguments()[0] == typeof(RequestModelMemberDeclaration);

    /// <summary>请求模型上「空串等价于缺省」的三处命名：只有它们在比对时把空串抹成 <c>null</c>。</summary>
    /// <remarks>
    /// 无参 <c>[Path]</c> / <c>[Query]</c> / <c>[Header]</c> 的特性构造参数是空串，而生成器发射不带实参的
    /// <c>.Path()</c>，烘焙声明里为 <c>null</c>；消费端 <c>BindingAccumulator.ResolveName</c> 用
    /// <c>IsNullOrWhiteSpace</c> 判定「沿用成员名」，两者同义。
    /// 不入列的：<c>PartName</c> / <c>PartFileName</c> / <c>PartContentType</c>（空白是构建错误）、
    /// <c>BodyName</c>（<c>null</c> 表示整份正文，空串表示按键聚合，语义不同）。
    /// </remarks>
    private static readonly HashSet<string> OptionalNameDeclarations = new(StringComparer.Ordinal)
    {
        nameof(RequestModelMemberDeclaration.PathName),
        nameof(RequestModelMemberDeclaration.QueryName),
        nameof(RequestModelMemberDeclaration.HeaderName),
    };

    /// <summary>用反射路径重建同形声明：方法顺序跟烘焙声明走，接口级字段取每次读取都相同的那份。</summary>
    /// <param name="interfaceType">已加载的接口类型。</param>
    /// <param name="shape">烘焙声明，只用来定方法顺序与名字。</param>
    private static InterfaceDeclaration MirrorThroughReflection(Type interfaceType, InterfaceDeclaration shape)
    {
        var mirror = new InterfaceDeclaration { InterfaceType = interfaceType };
        var ownerCaptured = false;

        foreach (var method in shape.Methods)
        {
            var info = FindMethod(interfaceType, method);

            var (owner, declaration) = InterfaceDeclarationReader.Read(interfaceType, info);
            mirror.Methods.Add(declaration);

            if (!ownerCaptured)
            {
                // 接口级字段由接口链算出，与方法无关，读一次即可。
                mirror.StaticHeaders.AddRange(owner.StaticHeaders);
                mirror.AuthorizeScheme = owner.AuthorizeScheme;
                mirror.AllowAnonymous = owner.AllowAnonymous;
                mirror.Error = owner.Error;
                ownerCaptured = true;
            }
        }

        return mirror;
    }

    /// <summary>在接口及其继承链上找方法：烘焙声明是链级的（含基接口方法），反射要跟着走一遍链。</summary>
    /// <param name="interfaceType">已加载的接口类型。</param>
    /// <param name="method">烘焙声明里的方法，按名字 + 参数名序列配对重载。</param>
    private static MethodInfo FindMethod(Type interfaceType, InterfaceMethodDeclaration method)
    {
        var candidates = ChainMethods(interfaceType)
            .Where(candidate => candidate.Name == method.Name)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 及其继承链上没有方法 {method.Name}：烘焙声明与类型对不上。");
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        // 重载：按参数名序列配对。参数名与声明同源，顺带校验了参数顺序与个数。
        var expected = method.Parameters.Select(static parameter => parameter.Name).ToArray();
        var matched = candidates
            .Where(candidate => candidate.GetParameters()
                .Select(static parameter => parameter.Name)
                .SequenceEqual(expected))
            .ToArray();

        if (matched.Length == 1)
        {
            return matched[0];
        }

        throw new InvalidOperationException(
            $"接口 {interfaceType.Name} 上的 {method.Name} 有 {candidates.Length} 个重载，"
            + $"按参数名（{string.Join(", ", expected)}）配不出唯一一个：烘焙声明与类型对不上。");
    }

    /// <summary>接口链上真实声明的方法：自身 + 每一层基接口，按签名去重。</summary>
    /// <param name="interfaceType">已加载的接口类型。</param>
    private static MethodInfo[] ChainMethods(Type interfaceType)
    {
        var methods = new List<MethodInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var current in new[] { interfaceType }.Concat(interfaceType.GetInterfaces()))
        {
            foreach (var method in current.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (seen.Add(Signature(method)))
                {
                    methods.Add(method);
                }
            }
        }

        return methods.ToArray();
    }

    /// <summary>方法签名键：声明接口 + 名字 + 参数类型，用于在接口链上去重同一个方法。</summary>
    /// <param name="method">接口方法。</param>
    private static string Signature(MethodInfo method)
        => (method.DeclaringType?.FullName ?? "?")
           + "|"
           + method.Name
           + "("
           + string.Join(",", method.GetParameters().Select(static parameter => parameter.ParameterType.FullName))
           + ")";

    /// <summary>反射侧的方法名（排序后），与烘焙声明的名字序列比对集合是否守恒。</summary>
    /// <param name="interfaceType">已加载的接口类型。</param>
    private static string[] ChainMethodNames(Type interfaceType)
        => ChainMethods(interfaceType)
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>烘焙声明里的方法名（排序后）。</summary>
    /// <param name="declaration">生成器烘焙的接口声明。</param>
    private static string[] BakedMethodNames(InterfaceDeclaration declaration)
        => declaration.Methods
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

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

    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output) RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "AxiosNet.JitAotParityTests." + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            CreateReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (diagnostics, output);
    }

    private static string UniqueNamespace() => "AxiosNet.Parity.G" + Guid.NewGuid().ToString("N");

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

    private const string ParityModels = """
        using System.Collections.Generic;
        using System.IO;
        using System.Text.Json.Serialization;
        using AxiosNet;
        using AxiosNet.WebAPI;
        using AxiosNet.WebAPI.Attributes;

        namespace __NS__;

        public sealed record Item(string Name, int Size);

        [Post("orders/{id}")]
        [Headers("X-Model: 1")]
        [BodyContentType("application/json")]
        [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
        public sealed class OrderRequest : IApiRequest<Item>
        {
            [Path]
            public int Id { get; init; }

            [Query("q")]
            public string? Keyword { get; init; }

            [Query]
            public IReadOnlyList<string>? Tags { get; init; }

            [Header("X-Trace")]
            public string? Trace { get; init; }

            [HeaderCollection]
            public IDictionary<string, string>? Extra { get; init; }

            [Body]
            public Item Payload { get; init; } = new("n", 1);

            [JsonPropertyName("note")]
            public string? Note { get; init; }

            [ApiIgnore]
            public string? Ignored { get; init; }

            // 无参 [Header]：反射侧读出空串、生成期烘焙 null —— 三处可选命名里的 HeaderName 这条。
            [Header]
            public string? Untitled { get; init; }
        }

        [Post("upload")]
        [Multipart]
        public sealed class UploadRequest
        {
            [Part("file", FileName = "a.bin", ContentType = "application/octet-stream")]
            public Stream? File { get; init; }

            public string Note { get; init; } = string.Empty;
        }
        """;

    private static readonly string[] RequestModelNames = ["OrderRequest", "UploadRequest"];

    private const string ParityInterface = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;
        using AxiosNet;
        using AxiosNet.WebAPI;
        using AxiosNet.WebAPI.Attributes;

        namespace __NS__;

        public sealed record Item(string Name, int Size);

        [Headers("X-Base: 1", "X-Shared: base")]
        public interface IParityBase
        {
            [Get("base/{id}")]
            [Headers("X-Base-Method: 1")]
            Task<Item> BaseAsync([Path] int id, CancellationToken cancellationToken);
        }

        [Headers("X-Derived: 2", "X-Shared: derived")]
        [Authorize("Basic")]
        public interface IParityApi : IParityBase
        {
            [Get("items/{id}")]
            [Headers("X-Method: yes")]
            [Authorize("Bearer")]
            [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
            Task<Item> GetAsync(
                [Path("id")] int id,
                [Query("q")] string? q,
                [Header("X-Trace")] string? trace,
                [HeaderCollection] IDictionary<string, string>? extra,
                [ApiIgnore] string? ignored,
                CancellationToken cancellationToken);

            // 同名不同参的重载：对拍要按参数名配出唯一一个，不能「按名字取第一个」。
            [Get("items/{id}")]
            Task<Item> GetAsync([Path] int id, CancellationToken cancellationToken);

            [Post("items")]
            [BodyContentType("application/json")]
            [AllowAnonymous]
            Task CreateAsync([Body] Item item, [Query] bool notify, CancellationToken cancellationToken);

            [Post("upload/{id}")]
            [Multipart]
            Task<Item> UploadAsync(
                [Path] int id,
                [Part("file", FileName = "a.bin", ContentType = "application/octet-stream")] Stream file,
                string note,
                CancellationToken cancellationToken);

            [Get("stream")]
            [Stream(AxiosStreamFormat.ServerSentEvents)]
            Task<AxiosResponse<IAsyncEnumerable<Item>>> StreamAsync(CancellationToken cancellationToken);

            [Delete("items/{id}")]
            Task RemoveAsync([Path] int id, CancellationToken cancellationToken);
        }
        """;
}
