using System.Text;
using System.Text.Json;

namespace AxiosNet.Tests.Infrastructure;

public record Person(string Name, int Age);

internal static class TestJson
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, WebOptions);

    public static TestResponse Response(object? value, int statusCode = 200, IReadOnlyDictionary<string, string>? headers = null)
        => new(statusCode, Body: value is null ? null : Bytes(value), Headers: headers);

    public static TestResponse Echo(TestRequest request, int statusCode = 200, string contentType = "application/json; charset=utf-8")
        => new(statusCode, Body: request.Body, ContentType: contentType);

    public static string BasicAuth(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
}
