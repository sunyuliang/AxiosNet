namespace AxiosNet;

/// <summary>
/// 对应 axios config.auth，发送 HTTP Basic 认证头。
/// </summary>
public sealed class AxiosAuth
{
    public AxiosAuth()
    {
    }

    public AxiosAuth(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    internal string ToBasicHeaderValue()
    {
        var raw = $"{Username}:{Password}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }
}
