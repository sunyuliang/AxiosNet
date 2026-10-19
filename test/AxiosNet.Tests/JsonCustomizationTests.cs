using System.Dynamic;
using System.Text;
using System.Text.Json;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// JSON 序列化可插拔：自定义 <see cref="IAxiosJsonSerializer"/>、实例级
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

        using var client = new AxiosClient(new AxiosConfig { JsonSerializer = SnakeCaseSerializer.Instance });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        var sent = Body(server.Received[0]);
        Assert.Contains("\"user_name\":\"alice\"", sent);
        Assert.DoesNotContain("UserName", sent);

        var response = await client.GetAsync<User>(server.Url + "users/1");
        Assert.Equal("bob", response.Data!.UserName);
        Assert.Equal(30, response.Data.Age);
    }

    [Fact]
    public async Task CustomSerializer_AlsoDrivesFormFlattening()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { JsonSerializer = SnakeCaseSerializer.Instance });

        await client.PostAsync(
            server.Url + "form",
            new User { UserName = "alice", Age = 20 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        var body = Body(server.Received[0]);
        Assert.Contains("user_name=alice", body);
        Assert.Contains("age=20", body);
    }

    [Fact]
    public async Task CustomSerializer_DrivesDynamicResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { user_name = "bob", age = 30 }));
        using var client = new AxiosClient(new AxiosConfig { JsonSerializer = SnakeCaseSerializer.Instance });

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
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }
        });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        Assert.Contains("\"user_name\":\"alice\"", Body(server.Received[0]));
    }

    [Fact]
    public void GlobalJsonDefaults_AreSystemTextJsonWeb()
    {
        Assert.IsType<SystemTextJsonSerializer>(AxiosJson.Serializer);
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

    /// <summary>用 snake_case 固定策略的 STJ 适配器，证明自定义序列化器会绕开 config 里的 options。</summary>
    private sealed class SnakeCaseSerializer : IAxiosJsonSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public static SnakeCaseSerializer Instance { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string json, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(json, type, Options);

        public object? ParseDynamic(string json, AxiosConfig config)
        {
            using var document = JsonDocument.Parse(json);
            return FromElement(document.RootElement);
        }

        public object ParseDocument(string json, AxiosConfig config)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static object? FromElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => ToExpando(element),
            JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

        private static ExpandoObject ToExpando(JsonElement element)
        {
            var expando = new ExpandoObject();
            var dictionary = (IDictionary<string, object?>)expando;
            foreach (var property in element.EnumerateObject())
            {
                dictionary[property.Name] = FromElement(property.Value);
            }

            return expando;
        }
    }
}
