using System.Dynamic;
using System.Linq.Expressions;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>AOT 守卫：依赖运行时代码生成的入口在 Native AOT 下给出可读的显式失败，而不是随机 NullReferenceException。</summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class AotGuardTests
{
    [Fact]
    public void XmlSerializer_WhenDynamicCodeIsDisabled_ThrowsPlatformNotSupported()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Throws<PlatformNotSupportedException>(
                () => XmlContentSerializer.Instance.Serialize(new object(), typeof(object), new AxiosConfig()));

            Assert.Contains("Native AOT", error.Message);
            Assert.Contains("IContentSerializer", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void DynamicMemberAccess_WhenDynamicCodeIsDisabled_ThrowsPlatformNotSupported()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var value = new AxiosDynamic { ["name"] = "n" };
            var provider = (IDynamicMetaObjectProvider)value;
            var parameter = Expression.Parameter(typeof(AxiosDynamic), "self");

            var error = Assert.Throws<PlatformNotSupportedException>(() => provider.GetMetaObject(parameter));

            Assert.Contains("Native AOT", error.Message);

            // 索引访问与字典接口不依赖 DLR：守卫生效时仍然可用。
            Assert.Equal("n", value["name"]);
            Assert.True(value.TryGetValue("name", out var read));
            Assert.Equal("n", read);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void MissingRequestModel_ReportsTypeAndRepairOptions()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            using var client = new AxiosNet.WebAPI.ApiClient("http://127.0.0.1:1/");

            var error = Assert.Throws<AxiosAotMetadataException>(
                () => client.GetUri(new GuardUnregisteredRequest()));

            Assert.Contains(nameof(GuardUnregisteredRequest), error.Message);
            Assert.Contains("源生成器", error.Message);
            Assert.Contains("RegisterRequestModel", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }
}

[Get("aot/guard")]
public sealed class GuardUnregisteredRequest : AxiosNet.WebAPI.IApiRequest<object>;
