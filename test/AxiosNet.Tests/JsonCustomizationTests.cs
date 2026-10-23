using System.Text;
using System.Text.Json;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 序列化可插拔：自定义 <see cref="IContentSerializer"/>、实例级
/// <see cref="AxiosConfig.JsonSerializerOptions"/>，以及 transform 列表的并发安全。
/// </summary>
public sealed class JsonCustomizationTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    [Fact]
    public async Task CustomSerializer_DrivesRequestAndResponse()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/users/1")
            ? TestJson.Response(new { user_name = "bob", age = 30 })
            : TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        var sent = Body(server.Received[0]);
        Assert.Contains("\"user_name\":\"alice\"", sent);
        Assert.DoesNotContain("UserName", sent);

        var response = await client.GetAsync<User>(server.Url + "users/1");
        Assert.Equal("bob", response.Data!.UserName);
        Assert.Equal(30, response.Data.Age);
    }

    [Fact]
    public async Task FormFlattening_IgnoresCustomSerializer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        await client.PostAsync(
            server.Url + "form",
            new User { UserName = "alice", Age = 20 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        // 表单是独立编码通道：换 JSON 引擎（snake_case）不影响字段名，命名只看属性名 /
        // [JsonPropertyName] / FormFieldNameSelector。这样换 XML、YAML 序列化器也不会把表单带坏。
        var body = Body(server.Received[0]);
        Assert.Contains("UserName=alice", body);
        Assert.Contains("Age=20", body);
    }

    [Fact]
    public async Task CustomSerializer_DrivesDynamicResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { user_name = "bob", age = 30 }));
        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        dynamic data = (await client.GetAsync<dynamic>(server.Url + "users/1")).Data!;

        Assert.Equal("bob", (string)data.user_name);
        Assert.Equal(30, (int)data.age);
    }

    [Fact]
    public async Task InstanceJsonOptions_OverrideGlobalNaming()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = SnakeCase
            },
            },
        });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        Assert.Contains("\"user_name\":\"alice\"", Body(server.Received[0]));
    }

    [Fact]
    public void GlobalJsonDefaults_AreSystemTextJsonWeb()
    {
        // 未注入任何序列化器：全局槽是 null（= 自动模式，查表未命中才回落内置 STJ）
        Assert.Null(AxiosContent.Serializer);
        Assert.Equal(JsonNamingPolicy.CamelCase, AxiosJson.Options.PropertyNamingPolicy);
        Assert.True(AxiosJson.Options.PropertyNameCaseInsensitive);
    }

    [Fact]
    public async Task TransformLists_AreSafeUnderConcurrentMutation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var stop = new CancellationTokenSource();

        Func<object?, AxiosConfig, object?> identity = static (data, _) => data;

        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                client.Defaults.TransformRequest.Add(identity);
                client.Defaults.TransformResponse.Add(static (data, _) => data);
                client.Defaults.TransformRequest.Remove(identity);
                client.Defaults.TransformResponse.RemoveAt(0);
            }
        });

        var failures = new List<Exception>();
        for (var i = 0; i < 300; i++)
        {
            try
            {
                await client.GetAsync(server.Url + "ping");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        stop.Cancel();
        await writer;

        Assert.Empty(failures);
    }

    private sealed class User
    {
        public string UserName { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    /// <summary>
    /// net8.0 内置 <c>JsonNamingPolicy.SnakeCaseLower</c>，net6.0 的 System.Text.Json 6 没有，
    /// 这里给出等价实现，保证两个 TFM 的用例语义一致。
    /// </summary>
    private static JsonNamingPolicy SnakeCase =>
#if NET8_0_OR_GREATER
        JsonNamingPolicy.SnakeCaseLower;
#else
        SnakeCaseNamingPolicy.Instance;
#endif

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public static SnakeCaseNamingPolicy Instance { get; } = new();

        public override string ConvertName(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>用 snake_case 固定策略的 STJ 适配器，证明自定义序列化器会绕开 config 里的 options。</summary>
    private sealed class SnakeCaseSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = SnakeCase
        };

        public static SnakeCaseSerializer Instance { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string json, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(json, type, Options);
    }
}
