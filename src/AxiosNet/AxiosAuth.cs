namespace AxiosNet;

/// <summary>对应 axios config.auth，发送 HTTP Basic 认证头。</summary>
public sealed class AxiosAuth
{
    /// <summary>创建空认证（用户名与密码均为空串）。</summary>
    public AxiosAuth()
    {
    }

    /// <summary>按用户名与密码创建 Basic 认证。</summary>
    public AxiosAuth(string username, string password)
    {
        Username = username;
        Password = password;
    }

    private string _username = string.Empty;

    /// <summary>Basic 认证的用户名，不允许含冒号（RFC 7617 用第一个冒号分隔 user-id 与 password）。</summary>
    /// <exception cref="ArgumentException">用户名含 <c>:</c>。</exception>
    public string Username
    {
        get => _username;
        set
        {
            if (value is not null && value.Contains(':'))
            {
                throw new ArgumentException(
                    "Basic 认证的用户名不能包含冒号：RFC 7617 用第一个冒号分隔 user-id 与 password，"
                    + "含冒号的用户名会让服务端解析出错误的密码。",
                    nameof(Username));
            }

            _username = value ?? string.Empty;
        }
    }

    /// <summary>Basic 认证的密码，默认空串。</summary>
    public string Password { get; set; } = string.Empty;

    internal string ToBasicHeaderValue()
    {
        var raw = $"{Username}:{Password}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }
}
