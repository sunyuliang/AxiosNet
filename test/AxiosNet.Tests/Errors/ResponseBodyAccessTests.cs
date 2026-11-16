using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Errors;

/// <summary>
/// <c>AxiosResponse&lt;T&gt;.DataOrThrow()</c>：把「这里一定有正文」写成一次显式断言，空正文走统一错误模型，
/// 而不是在业务代码深处爆一个不带请求上下文的 <see cref="NullReferenceException"/>。
/// </summary>
public sealed class ResponseBodyAccessTests
{
    [Fact]
    public void DataOrThrow_ReturnsBody_WhenPresent()
    {
        var person = new Person("alice", 30);
        var response = new AxiosResponse<Person> { Data = person, Status = 200 };

        Assert.Same(person, response.DataOrThrow());
    }

    [Fact]
    public void DataOrThrow_ThrowsBadResponse_WhenBodyIsNull()
    {
        var response = new AxiosResponse<Person> { Data = null, Status = 204, StatusText = "No Content" };

        var error = Assert.Throws<AxiosException>(() => response.DataOrThrow());

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        // 消息要能定位到「期望的类型」和最常见的成因，否则等于换个地方猜。
        Assert.Contains(nameof(Person), error.Message);
        Assert.Contains("204", error.Message);
    }

    [Fact]
    public void DataOrThrow_CarriesConfigAndResponse()
    {
        var config = new AxiosConfig { Url = "https://api.example.com/orders/1" };
        using var request = new HttpRequestMessage(HttpMethod.Get, config.Url);
        var response = new AxiosResponse<Person>
        {
            Data = null,
            Status = 204,
            Config = config,
            Request = request
        };

        var error = Assert.Throws<AxiosException>(() => response.DataOrThrow());

        Assert.Same(config, error.Config);
        Assert.Same(response, error.Response);
    }

    [Fact]
    public void DataOrThrow_ThrowsForValueType_InsteadOfReturningDefault()
    {
        // 值类型的 Data 为空时 Data 属性给 default(T)，DataOrThrow 必须仍然抛——
        // 否则「0 / false / 空结构」会被静默当成本次调用的结果。
        var response = new AxiosResponse<int> { Status = 204 };

        Assert.Equal(0, response.Data);
        var error = Assert.Throws<AxiosException>(() => response.DataOrThrow());
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
    }

    [Fact]
    public void DataOrThrow_ReturnsValueTypeBody_WhenPresent()
    {
        var response = new AxiosResponse<int> { Data = 0, Status = 200 };

        // 0 是合法正文，不能因为「看起来像默认值」就抛。
        Assert.Equal(0, response.DataOrThrow());
    }

    [Fact]
    public void DataOrThrow_ThrowsForNonNullableScalar_WhenBodyIsNull()
    {
        var response = new AxiosResponse<string> { Data = null, Status = 200 };

        var error = Assert.Throws<AxiosException>(() => response.DataOrThrow());

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("String", error.Message);
    }
}
