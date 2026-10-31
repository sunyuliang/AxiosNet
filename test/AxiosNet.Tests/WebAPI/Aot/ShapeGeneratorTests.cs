using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using AxiosNet;
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
/// 对象形状源生成器：从 [AxiosShape] / 请求模型 / 调用点 / 匿名对象出发递归生成形状，
/// 生成结果必须与 JIT 反射展平一致；AOT（强制无动态代码）下 form / params / multipart 全通路可用，
/// 未覆盖的运行时类型显式抛 <see cref="AxiosAotMetadataException"/>（带类型与字段路径）。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class ShapeGeneratorTests
{
    [Fact]
    public void GeneratedShapes_DescribeMembersLikeReflectionFlattening()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(ShapeModels.Replace("__NS__", ns));

        Assert.Empty(diagnostics);

        var generatedText = string.Join(
            Environment.NewLine,
            result.Results.Single().GeneratedSources.Select(static source => source.SourceText.ToString()));
        Assert.Contains("Filters", generatedText);
        Assert.Contains("HeaderBag", generatedText);
        Assert.Contains("city_name", generatedText);

        var assembly = LoadEmitted(output);

        var filters = assembly.GetType(ns + ".Filters", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetShape(filters, out var shape));
        Assert.Equal(
            new[] { "Query", "Secret", "Optional", "Page", "Address" },
            shape!.Members.Select(static member => member.Name));
        Assert.Equal("q", shape.Members[0].FieldName);
        Assert.False(shape.Members[0].Ignored);
        Assert.True(shape.Members[1].Ignored);
        Assert.False(shape.Members[2].Ignored);
        Assert.Equal("Address", shape.Members[4].FieldName);

        var address = assembly.GetType(ns + ".Address", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetShape(address, out var addressShape));
        Assert.Equal("city_name", addressShape!.Members[0].FieldName);

        // 自定义只读字典：只生成枚举访问器（params 平铺），不注册 POCO 形状。
        var headerBag = assembly.GetType(ns + ".HeaderBag", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetMap(headerBag, out var map));
        var bag = CreateInstance(assembly, ns + ".HeaderBag");
        Assert.Equal(
            new[] { new KeyValuePair<string, object?>("a", "1"), new KeyValuePair<string, object?>("b", "two") },
            map!.Enumerate(bag));
        Assert.False(GeneratedMetadataRegistry.TryGetShape(headerBag, out _));

        // 只经 object 传递的类型不应被数据流分析误收集。
        Assert.False(GeneratedMetadataRegistry.TryGetShape(assembly.GetType(ns + ".Unseeded", throwOnError: true)!, out _));
    }

    [Fact]
    public async Task GeneratedFlattening_MatchesReflectionFlattening_ForParamsFormAndMultipart()
    {
        var ns = UniqueNamespace();
        var source = ShapeModels.Replace("__NS__", ns);
        var disabled = new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" };

        var generated = RunGenerator(source);
        var reflected = RunGenerator(source, disabled);

        Assert.Empty(generated.Diagnostics);
        Assert.Empty(reflected.Diagnostics);
        Assert.NotEmpty(generated.Result.Results.Single().GeneratedSources);
        Assert.Empty(reflected.Result.Results.Single().GeneratedSources);

        var generatedAssembly = LoadEmitted(generated.Output);
        var reflectedAssembly = LoadEmitted(reflected.Output);

        // params：两套路径产出的 URL 必须逐字符相同（含不跳过 [JsonIgnore] 的既有语义）。
        var generatedUrl = InvokeString(generatedAssembly, "Query", CreateFilters(generatedAssembly));
        var reflectedUrl = InvokeString(reflectedAssembly, "Query", CreateFilters(reflectedAssembly));
        Assert.Equal(reflectedUrl, generatedUrl);
        Assert.Contains("Query=hello%20world", generatedUrl);
        Assert.Contains("Secret=s", generatedUrl);
        Assert.Contains("Optional=keep", generatedUrl);
        Assert.Contains("Address[City]=Paris", generatedUrl);

        // 匿名对象：结构相同的样本推断类型参数，展平结果必须与反射一致。
        Assert.Equal(
            InvokeString(reflectedAssembly, "AnonymousQuery"),
            InvokeString(generatedAssembly, "AnonymousQuery"));

        // 表单 / multipart：字段名取 [JsonPropertyName]、跳过 [JsonIgnore(Always)]、条件忽略仍参与。
        Assert.Equal(
            await InvokeFormAsync(reflectedAssembly),
            await InvokeFormAsync(generatedAssembly));

        var formBody = await InvokeFormAsync(generatedAssembly);
        Assert.Contains("q=hello", formBody);
        Assert.Contains("city_name", formBody);
        Assert.Contains("Optional=keep", formBody);
        Assert.DoesNotContain("Secret", formBody);

        Assert.Equal(
            NormalizeMultipart(await InvokeMultipartAsync(reflectedAssembly)),
            NormalizeMultipart(await InvokeMultipartAsync(generatedAssembly)));

        Assert.Equal(
            await InvokeAnonymousFormAsync(reflectedAssembly),
            await InvokeAnonymousFormAsync(generatedAssembly));

        // 继承链上的 override：基类 [JsonPropertyName] / [JsonIgnore] 与反射同样生效。
        Assert.Equal(
            await InvokeDerivedFormAsync(reflectedAssembly),
            await InvokeDerivedFormAsync(generatedAssembly));
    }

    [Fact]
    public async Task GeneratedShapes_WorkUnderAot_ForFormParamsMultipartAndCustomMaps()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(ShapeModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);
        Assert.NotEmpty(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);
        var generatedUrl = InvokeString(assembly, "Query", CreateFilters(assembly));
        var generatedAnonymousUrl = InvokeString(assembly, "AnonymousQuery");

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Equal(generatedUrl, InvokeString(assembly, "Query", CreateFilters(assembly)));
            Assert.Equal(generatedAnonymousUrl, InvokeString(assembly, "AnonymousQuery"));

            var formBody = await InvokeFormAsync(assembly);
            Assert.Contains("q=hello", formBody);
            Assert.Contains("Address[city_name]", formBody);
            Assert.DoesNotContain("Secret", formBody);

            var multipartBody = await InvokeMultipartAsync(assembly);
            Assert.Contains("name=q", multipartBody);
            Assert.Contains("city_name", multipartBody);
            Assert.DoesNotContain("\"Secret\"", multipartBody);

            var anonymousFormBody = await InvokeAnonymousFormAsync(assembly);
            Assert.Contains("name=alice", anonymousFormBody);
            Assert.Contains("address[city_name]=Paris", anonymousFormBody);

            // 自定义只读字典：声明式 [Query] 平铺走 QueryMap，AOT 下命中生成的枚举访问器。
            Assert.Equal("a=1&b=two", await InvokeMapAsync(assembly));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public async Task MissingShapesUnderAot_FailExplicitlyWithTypeAndFieldPath()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, output) = RunGenerator(ShapeModels.Replace("__NS__", ns));
        Assert.Empty(diagnostics);
        Assert.NotEmpty(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" });

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            // 发送管线把「正文构建失败」统一包成 AxiosException（带配置），形状缺失在 InnerException 里。
            var unseeded = await Assert.ThrowsAsync<AxiosException>(
                () => InvokeTaskAsync(assembly, "SendUnseeded", client));
            var unseededAot = Assert.IsType<AxiosAotMetadataException>(unseeded.GetBaseException());
            Assert.Contains(ns + ".Unseeded", unseededAot.Message);
            Assert.Contains("根对象", unseededAot.Message);
            Assert.Contains("RegisterShapeFromSample", unseededAot.Message);
            Assert.Contains("Native AOT", unseededAot.Message);

            var nested = await Assert.ThrowsAsync<AxiosException>(
                () => InvokeTaskAsync(assembly, "SendHolder", client));
            var nestedAot = Assert.IsType<AxiosAotMetadataException>(nested.GetBaseException());
            Assert.Contains(ns + ".Unseeded", nestedAot.Message);
            Assert.Contains("位置：Payload", nestedAot.Message);

            var map = Assert.Throws<AxiosAotMetadataException>(() => QueryMap.Enumerate(new UnregisteredMap()).ToArray());
            Assert.Contains(nameof(UnregisteredMap), map.Message);
            Assert.Contains("字典映射访问器", map.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void GeneratorDisabled_LeavesNoShapes_SoAotFailsInsteadOfGuessing()
    {
        var ns = UniqueNamespace();
        var disabled = new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" };
        var (result, diagnostics, output) = RunGenerator(ShapeModels.Replace("__NS__", ns), disabled);

        Assert.Empty(diagnostics);
        Assert.Empty(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var invocation = Assert.Throws<TargetInvocationException>(
                () => InvokeString(assembly, "Query", CreateFilters(assembly)));
            var error = Assert.IsType<AxiosAotMetadataException>(invocation.GetBaseException());
            Assert.Contains(ns + ".Filters", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void AnonymousArrayMembers_EmitValidGeneratedCodeAndMatchReflection()
    {
        var ns = UniqueNamespace();
        var source = """
            using AxiosNet;

            namespace __NS__;

            public static class Boot
            {
                public static string Query()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new { items = new[] { new { id = 1, name = "a" }, new { id = 2, name = "b" } } };
                    });
                }
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.Empty(diagnostics);

        var generatedText = string.Join(
            Environment.NewLine,
            result.Results.Single().GeneratedSources.Select(static source => source.SourceText.ToString()));

        // 匿名元素无法在源码里命名：样本必须写成 new[] { ... } 让编译器推断，不能出现 <anonymous type: ...>。
        Assert.DoesNotContain("anonymous type", generatedText);
        Assert.Contains("new[] {", generatedText);

        var assembly = LoadEmitted(output);
        var jit = InvokeString(assembly, "Query");
        Assert.Contains("items[][id]=1", jit);
        Assert.Contains("items[][name]=a", jit);

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Equal(jit, InvokeString(assembly, "Query"));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void NonPublicGetters_AreExcludedFromGeneratedAndReflectedShapes()
    {
        var ns = UniqueNamespace();
        var source = """
            using AxiosNet;

            namespace __NS__;

            [AxiosShape]
            public sealed class Model
            {
                public string Visible { get; set; } = "v";

                public string PrivateGetter { private get; set; } = "p";

                public string InternalGetter { internal get; set; } = "i";

                // ref struct 无法装箱成 object：生成与反射两条路径都要跳过。
                public System.Span<int> RefLike => default;
            }

            public static class Boot
            {
                public static string Query()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new Model();
                    });
                }
            }
            """.Replace("__NS__", ns);

        var generated = RunGenerator(source);
        var reflected = RunGenerator(source, new Dictionary<string, string> { ["build_property.AxiosNetSourceGen"] = "false" });
        Assert.Empty(generated.Diagnostics);
        Assert.Empty(reflected.Diagnostics);

        var generatedAssembly = LoadEmitted(generated.Output);
        Assert.True(GeneratedMetadataRegistry.TryGetShape(generatedAssembly.GetType(ns + ".Model", throwOnError: true)!, out var shape));
        Assert.Equal("Visible", Assert.Single(shape!.Members).Name);
        var generatedUrl = InvokeString(generatedAssembly, "Query");

        var reflectedAssembly = LoadEmitted(reflected.Output);
        Assert.Equal(InvokeString(reflectedAssembly, "Query"), generatedUrl);
        Assert.Contains("Visible=v", generatedUrl);
        Assert.DoesNotContain("PrivateGetter", generatedUrl);
        Assert.DoesNotContain("InternalGetter", generatedUrl);
        Assert.DoesNotContain("RefLike", generatedUrl);
    }

    [Fact]
    public void MultiDimAnonymousArrays_AreSkippedInsteadOfRegisteringAMismatchedSample()
    {
        var ns = UniqueNamespace();
        var source = """
            using AxiosNet;

            namespace __NS__;

            public static class Boot
            {
                public static string Query()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new { grid = new[,] { { new { id = 1 } } } };
                    });
                }
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.Empty(diagnostics);

        // 多维匿名数组写不出 new[] 样本：宁可不注册（AOT 下显式缺形状），也不能注册结构不同的匿名类型。
        var generatedText = string.Join(
            Environment.NewLine,
            result.Results.Single().GeneratedSources.Select(static source => source.SourceText.ToString()));
        Assert.DoesNotContain("anonymous type", generatedText);

        var assembly = LoadEmitted(output);
        Assert.Contains("grid[][id]=1", InvokeString(assembly, "Query"));

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var invocation = Assert.Throws<TargetInvocationException>(() => InvokeString(assembly, "Query"));
            var error = Assert.IsType<AxiosAotMetadataException>(invocation.GetBaseException());
            Assert.Contains("缺少对象形状元数据", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void DictionarySubclasses_StillCollectValueShapesForAot()
    {
        var ns = UniqueNamespace();
        var source = """
            using System.Collections.Generic;
            using AxiosNet;

            namespace __NS__;

            public sealed class Item
            {
                public int Id { get; set; }
            }

            public static class Boot
            {
                public static string Query()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new Dictionary<string, Item> { ["a"] = new Item { Id = 1 } };
                    });
                }

                public static string QueryIntKeys()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new Dictionary<int, Item> { [5] = new Item { Id = 2 } };
                    });
                }
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.Empty(diagnostics);

        var assembly = LoadEmitted(output);
        var item = assembly.GetType(ns + ".Item", throwOnError: true)!;

        // Dictionary<,> 同时实现非泛型 IDictionary（运行期直达），但值类型自己的形状仍要生成。
        Assert.True(GeneratedMetadataRegistry.TryGetShape(item, out _));

        var jit = InvokeString(assembly, "Query");
        Assert.Contains("a[Id]=1", jit);

        // 非字符串键的字典没有映射访问器（运行期按 IDictionary 展平），但值形状同样要烘焙。
        var intKeys = InvokeString(assembly, "QueryIntKeys");
        Assert.Contains("5[Id]=2", intKeys);

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Equal(jit, InvokeString(assembly, "Query"));
            Assert.Equal(intKeys, InvokeString(assembly, "QueryIntKeys"));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public async Task CustomMapsWithValueTypeValues_FlowThroughParamsFormAndMultipartUnderAot()
    {
        var ns = UniqueNamespace();
        var source = """
            using System.Collections;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using AxiosNet;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public sealed class IntBag : IReadOnlyDictionary<string, int>
            {
                private readonly Dictionary<string, int> _inner = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

                public int this[string key] => _inner[key];

                public IEnumerable<string> Keys => _inner.Keys;

                public IEnumerable<int> Values => _inner.Values;

                public int Count => _inner.Count;

                public bool ContainsKey(string key) => _inner.ContainsKey(key);

                public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

                public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public sealed class FormBag : IReadOnlyDictionary<string, int>
            {
                private readonly Dictionary<string, int> _inner = new Dictionary<string, int> { ["c"] = 3, ["d"] = 4 };

                public int this[string key] => _inner[key];

                public IEnumerable<string> Keys => _inner.Keys;

                public IEnumerable<int> Values => _inner.Values;

                public int Count => _inner.Count;

                public bool ContainsKey(string key) => _inner.ContainsKey(key);

                public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

                public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            [Get("intmap")]
            public sealed class IntMapRequest : IApiRequest<object>
            {
                [Query]
                public IReadOnlyDictionary<string, int> Bag { get; set; } = new IntBag();
            }

            public static class Boot
            {
                public static Task IntMap(ApiClient client) => client.RequestAsync(new IntMapRequest());

                public static string RawMapQuery()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new FormBag();
                    });
                }

                public static Task FormMap(AxiosClient client)
                    => client.PostFormAsync("form", new FormBag(), c => c.ContentType = AxiosContentType.FormUrlEncoded);

                public static Task MultipartMap(AxiosClient client)
                    => client.PostFormAsync("up", new FormBag());
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.Empty(diagnostics);
        Assert.NotEmpty(result.Results.Single().GeneratedSources);

        var assembly = LoadEmitted(output);

        // 声明为接口类型时生成器按接口注册访问器：具体类型查不到，只能沿接口命中（AOT 的关键路径）。
        var intBag = assembly.GetType(ns + ".IntBag", throwOnError: true)!;
        Assert.False(GeneratedMetadataRegistry.TryGetMap(intBag, out _));
        Assert.True(GeneratedMetadataRegistry.TryGetMap(typeof(IReadOnlyDictionary<string, int>), out _));

        var formBag = assembly.GetType(ns + ".FormBag", throwOnError: true)!;
        Assert.True(GeneratedMetadataRegistry.TryGetMap(formBag, out _));
        Assert.EndsWith("?c=3&d=4", InvokeString(assembly, "RawMapQuery"));

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);
        using var axios = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            // 声明为接口时只有接口键的访问器：AOT 下必须沿接口命中。
            await InvokeTaskAsync(assembly, "IntMap", client);
            Assert.Equal("a=1&b=2", server.Received[^1].Query ?? string.Empty);

            // 旧 params 通道：值类型值字典走 QueryMap 的映射通道，而不是按 KeyValuePair 下标展开。
            Assert.EndsWith("?c=3&d=4", InvokeString(assembly, "RawMapQuery"));

            await InvokeTaskAsync(assembly, "FormMap", axios);
            Assert.Equal("c=3&d=4", Encoding.UTF8.GetString(server.Received[^1].Body));

            await InvokeTaskAsync(assembly, "MultipartMap", axios);
            var multipartBody = Encoding.UTF8.GetString(server.Received[^1].Body);
            Assert.Contains("name=c", multipartBody);
            Assert.Contains("name=d", multipartBody);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    /// <summary>
    /// 匿名对象先落到局部变量、再作为 <c>object</c> 实参传给 <c>PostFormAsync</c> 时，
    /// 静态类型在调用点已经退化成 <c>object</c>；只有「匿名对象创建」这条发现规则能兜住它，
    /// 所以这里锁定该规则不做上下文收窄（收窄会让这类写法在 AOT 下缺形状）。
    /// </summary>
    [Fact]
    public async Task AnonymousObjectsPassedThroughLocals_AreDiscoveredSoAotFlatteningWorks()
    {
        var ns = UniqueNamespace();
        var source = """
            using System.Threading.Tasks;
            using AxiosNet;

            namespace __NS__;

            public sealed class Address
            {
                public string City { get; set; } = string.Empty;
            }

            public static class Boot
            {
                public static Task Form(AxiosClient client)
                {
                    var data = new { name = "alice", address = new Address { City = "Paris" } };
                    return client.PostFormAsync("anon", data, c => c.ContentType = AxiosContentType.FormUrlEncoded);
                }
            }
            """.Replace("__NS__", ns);

        var (result, diagnostics, output) = RunGenerator(source);
        Assert.Empty(diagnostics);

        var shapeSources = result.Results.Single().GeneratedSources
            .Where(static source => source.HintName.Contains("Shape", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, shapeSources.Length);

        var assembly = LoadEmitted(output);
        var formBody = await InvokeFormForAsync(assembly, "Form");
        Assert.Contains("name=alice", formBody);
        Assert.Contains("address[City]=Paris", formBody);

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Equal(formBody, await InvokeFormForAsync(assembly, "Form"));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void AnonymousObjectsOutsideAxiosCalls_DoNotReportInaccessibleShape()
    {
        var ns = UniqueNamespace();
        var (result, diagnostics, _) = RunGenerator("""
            using System.Linq;
            using AxiosNet;

            namespace __NS__;

            public static class Boot
            {
                private sealed class Hidden
                {
                    public string Value { get; set; } = string.Empty;
                }

                private sealed class Holder
                {
                    public object? Params { get; set; }
                }

                public static string Query()
                {
                    // 与 Axios 无关的 LINQ 投影：成员类型不可见时不应产生 AXA013。
                    var projected = new[] { 1, 2 }.Select(_ => new { secret = new Hidden() }).ToList();

                    // 非 AxiosConfig 上的同名 Params 属性同样不是 Axios 上下文。
                    new Holder().Params = new { secret = new Hidden() };
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "x";
                        c.Params = new { ok = projected.Count };
                    });
                }
            }
            """.Replace("__NS__", ns));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void InaccessibleShapeType_ReportsWarningAndSkipsEmission()
    {
        var (result, diagnostics, _) = RunGenerator("""
            using AxiosNet;
            using AxiosNet.WebAPI;

            namespace __NS__;

            public static class Container
            {
                [AxiosShape]
                private sealed class Hidden
                {
                    public string Value { get; set; } = string.Empty;
                }

                public static string Query()
                {
                    using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                    return client.GetUri(c =>
                    {
                        c.Url = "p";
                        c.Params = new Hidden { Value = "v" };
                    });
                }
            }
            """.Replace("__NS__", UniqueNamespace()));

        DiagnosticCatalogTests.AssertNoUnrenderedPlaceholders(diagnostics);

        var warning = Assert.Single(diagnostics);
        Assert.Equal("AXA013", warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Hidden", warning.GetMessage());
        Assert.Empty(result.Results.Single().GeneratedSources);
    }

    // ------------------------------------------------------------------
    // 调用辅助：生成程序集里的 Boot 方法负责构造模型与发起调用
    // ------------------------------------------------------------------

    private static object CreateFilters(Assembly assembly)
    {
        var filters = CreateInstance(assembly, FindType(assembly, "Filters").FullName!);
        Set(filters, "Query", "hello world");
        Set(filters, "Secret", "s");
        Set(filters, "Optional", "keep");
        Set(filters, "Page", 3);

        var address = CreateInstance(assembly, FindType(assembly, "Address").FullName!);
        Set(address, "City", "Paris");
        Set(address, "Tags", new List<string> { "x", "y" });
        Set(filters, "Address", address);
        return filters;
    }

    private static object CreateDerived(Assembly assembly)
    {
        var derived = CreateInstance(assembly, FindType(assembly, "ShapeDerived").FullName!);
        Set(derived, "Name", "derived");
        Set(derived, "Hidden", "secret");
        return derived;
    }

    private static void Set(object instance, string property, object? value)
        => instance.GetType().GetProperty(property)!.SetValue(instance, value);

    /// <summary>multipart 边界每次随机，比较两套路径的正文前先归一化。</summary>
    private static string NormalizeMultipart(string body)
        => Regex.Replace(body, "--[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "--BOUNDARY");

    private static string InvokeString(Assembly assembly, string method, params object?[] arguments)
        => (string)FindBoot(assembly).GetMethod(method)!.Invoke(null, arguments)!;

    private static Type FindType(Assembly assembly, string name)
        => assembly.GetExportedTypes().Single(type => type.Name == name);

    private static Type FindBoot(Assembly assembly)
    {
        var boot = assembly.GetExportedTypes().SingleOrDefault(static type => type.Name == "Boot")
            ?? throw new InvalidOperationException("生成程序集里没有 Boot 类型。");
        return boot;
    }

    private static async Task InvokeTaskAsync(Assembly assembly, string method, params object?[] arguments)
        => await ((Task)FindBoot(assembly).GetMethod(method)!.Invoke(null, arguments)!).ConfigureAwait(false);

    private static async Task<string> InvokeFormAsync(Assembly assembly)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        await InvokeTaskAsync(assembly, "FormUrlEncoded", client, CreateFilters(assembly)).ConfigureAwait(false);
        return Encoding.UTF8.GetString(server.Received.Single().Body);
    }

    private static async Task<string> InvokeMultipartAsync(Assembly assembly)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        await InvokeTaskAsync(assembly, "Multipart", client, CreateFilters(assembly)).ConfigureAwait(false);
        return Encoding.UTF8.GetString(server.Received.Single().Body);
    }

    private static async Task<string> InvokeAnonymousFormAsync(Assembly assembly)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        await InvokeTaskAsync(assembly, "AnonymousForm", client).ConfigureAwait(false);
        return Encoding.UTF8.GetString(server.Received.Single().Body);
    }

    private static async Task<string> InvokeMapAsync(Assembly assembly)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);
        await InvokeTaskAsync(assembly, "MapModel", client).ConfigureAwait(false);
        return server.Received.Single().Query ?? string.Empty;
    }

    private static async Task<string> InvokeDerivedFormAsync(Assembly assembly)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        await InvokeTaskAsync(assembly, "FormDerived", client, CreateDerived(assembly)).ConfigureAwait(false);
        return Encoding.UTF8.GetString(server.Received.Single().Body);
    }

    /// <summary>调用生成程序集里「接收 <see cref="AxiosClient"/> 并提交表单」的方法，返回请求正文。</summary>
    private static async Task<string> InvokeFormForAsync(Assembly assembly, string method)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        await InvokeTaskAsync(assembly, method, client).ConfigureAwait(false);
        return Encoding.UTF8.GetString(server.Received.Single().Body);
    }

    // ------------------------------------------------------------------
    // 生成器驱动（与接口生成器测试同一套内存编译 / 模块初始化器加载方式）
    // ------------------------------------------------------------------

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
        IReadOnlyDictionary<string, string>? options = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            UniqueNamespace(),
            [tree],
            CreateReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RequestModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: new TestOptionsProvider(options ?? NoOptions));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (driver.GetRunResult(), diagnostics, output);
    }

    private static string UniqueNamespace() => "AxiosNet.ShapeGeneratorTests.G" + Guid.NewGuid().ToString("N");

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

    /// <summary>测试项目不跑生成器：这个自定义只读字典没有映射访问器，用于 AOT 缺失路径。</summary>
    private sealed class UnregisteredMap : IReadOnlyDictionary<string, string>
    {
        private readonly Dictionary<string, string> _inner = new() { ["only"] = "value" };

        public string this[string key] => _inner[key];

        public IEnumerable<string> Keys => _inner.Keys;

        public IEnumerable<string> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public bool TryGetValue(string key, out string value)
        {
            if (_inner.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public TestOptions(IReadOnlyDictionary<string, string>? values)
            => _values = values ?? new Dictionary<string, string>();

        public override bool TryGetValue(string key, out string value)
            => _values.TryGetValue(key, out value!);
    }

    private const string ShapeModels = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using AxiosNet;
        using AxiosNet.WebAPI;

        namespace __NS__;

        [AxiosShape]
        public sealed class Filters
        {
            [JsonPropertyName("q")]
            public string Query { get; set; } = string.Empty;

            [JsonIgnore]
            public string Secret { get; set; } = "hidden";

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string Optional { get; set; } = "kept";

            public int Page { get; set; }

            public Address Address { get; set; } = new Address();
        }

        public sealed class Address
        {
            [JsonPropertyName("city_name")]
            public string City { get; set; } = string.Empty;

            public List<string> Tags { get; set; } = new List<string>();
        }

        public class ShapeBase
        {
            [JsonPropertyName("base_name")]
            public virtual string Name { get; set; } = "base";

            [JsonIgnore]
            public virtual string Hidden { get; set; } = "hidden";
        }

        [AxiosShape]
        public sealed class ShapeDerived : ShapeBase
        {
            public override string Name { get; set; } = "derived";

            public override string Hidden { get; set; } = "hidden";
        }

        public sealed class Unseeded
        {
            public string Value { get; set; } = "leak";
        }

        public sealed class PayloadHolder
        {
            public string Name { get; set; } = "holder";

            public object Payload { get; set; } = new object();
        }

        public sealed class HeaderBag : IReadOnlyDictionary<string, string>
        {
            private readonly Dictionary<string, string> _inner = new Dictionary<string, string>
            {
                ["a"] = "1",
                ["b"] = "two"
            };

            public string this[string key] => _inner[key];

            public IEnumerable<string> Keys => _inner.Keys;

            public IEnumerable<string> Values => _inner.Values;

            public int Count => _inner.Count;

            public bool ContainsKey(string key) => _inner.ContainsKey(key);

            public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value);

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Get("map")]
        public sealed class MapRequest : IApiRequest<object>
        {
            [Query]
            public HeaderBag Bag { get; set; } = new HeaderBag();
        }

        public static class Boot
        {
            public static string Query(Filters filters)
            {
                using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                return client.GetUri(c =>
                {
                    c.Url = "search";
                    c.Params = filters;
                });
            }

            public static string AnonymousQuery()
            {
                using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
                return client.GetUri(c =>
                {
                    c.Url = "x";
                    c.Params = new { q = "a b", n = 2, nested = new { flag = true, items = new[] { "u", "v" } } };
                });
            }

            public static Task MapModel(ApiClient client)
                => client.RequestAsync(new MapRequest());

            public static Task FormUrlEncoded(AxiosClient client, object data)
                => client.PostFormAsync("form", data, c => c.ContentType = AxiosContentType.FormUrlEncoded);

            public static Task Multipart(AxiosClient client, object data)
                => client.PostFormAsync("upload", data);

            public static Task AnonymousForm(AxiosClient client)
                => client.PostFormAsync(
                    "anon",
                    new { name = "alice", address = new Address { City = "Paris", Tags = new List<string> { "x" } } },
                    c => c.ContentType = AxiosContentType.FormUrlEncoded);

            public static Task FormDerived(AxiosClient client, ShapeDerived data)
                => client.PostFormAsync("derived", data, c => c.ContentType = AxiosContentType.FormUrlEncoded);

            public static Task SendUnseeded(AxiosClient client)
                => client.PostFormAsync("bad", (object)new Unseeded(), c => c.ContentType = AxiosContentType.FormUrlEncoded);

            public static Task SendHolder(AxiosClient client)
                => client.PostFormAsync(
                    "bad",
                    new PayloadHolder { Payload = new Unseeded() },
                    c => c.ContentType = AxiosContentType.FormUrlEncoded);
        }
        """;
}
