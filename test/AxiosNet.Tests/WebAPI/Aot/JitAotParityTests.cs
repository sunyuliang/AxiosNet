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

    /// <summary>把声明按「公开属性 + 集合元素」递归转成文本；委托成员只留占位，类型只留全名。</summary>
    /// <param name="value">任意声明对象。</param>
    private static string Dump(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    /// <summary>转储的递归实现。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="value">当前节点。</param>
    private static void Write(StringBuilder builder, object? value)
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
                builder.Append('[');
                var first = true;
                foreach (var item in sequence)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    Write(builder, item);
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

            builder.Append(properties[i].Name).Append('=');
            Write(builder, properties[i].GetValue(value));
        }

        builder.Append('}');
    }

    /// <summary>用反射路径重建同形声明：方法顺序跟烘焙声明走，接口级字段取每次读取都相同的那份。</summary>
    /// <param name="interfaceType">已加载的接口类型。</param>
    /// <param name="shape">烘焙声明，只用来定方法顺序与名字。</param>
    private static InterfaceDeclaration MirrorThroughReflection(Type interfaceType, InterfaceDeclaration shape)
    {
        var mirror = new InterfaceDeclaration { InterfaceType = interfaceType };
        var ownerCaptured = false;

        foreach (var method in shape.Methods)
        {
            var info = FindMethod(interfaceType, method.Name)
                ?? throw new InvalidOperationException(
                    $"接口 {interfaceType.Name} 及其继承链上没有方法 {method.Name}：烘焙声明与类型对不上。");

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
    /// <param name="name">方法名。</param>
    private static MethodInfo? FindMethod(Type interfaceType, string name)
    {
        var found = interfaceType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        if (found is not null)
        {
            return found;
        }

        foreach (var inherited in interfaceType.GetInterfaces())
        {
            found = inherited.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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
