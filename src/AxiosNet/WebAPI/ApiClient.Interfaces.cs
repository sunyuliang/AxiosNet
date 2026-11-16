using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;
using AxiosNet.WebAPI.Reflection;

namespace AxiosNet.WebAPI;

public sealed partial class ApiClient
{
    /// <summary>
    /// 为接口创建代理实现（如 <c>var api = client.Create&lt;IUserApi&gt;();</c>）。
    /// 接口方法的绑定语义与请求模型完全一致；代理持有当前 <see cref="ApiClient"/>，请按接口持有一份（DI 注册即单例）。
    /// </summary>
    /// <remarks>
    /// 优先使用源生成器烘焙的真实代理类（JIT / Native AOT 同一路径）；只有接口没有生成元数据时才回退
    /// DispatchProxy（仅 JIT，且要求接口为 public）。
    /// 每个方法返回 <c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c> / <c>Task&lt;AxiosResponse&gt;</c> /
    /// <c>Task&lt;T&gt;</c> / <c>Task</c> 之一；返回 <c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;U&gt;&gt;&gt;</c> 时按流式处理
    /// （<c>Task&lt;IAsyncEnumerable&lt;U&gt;&gt;</c> 这种形态不受支持，校验期直接拒绝）。
    /// 同步返回、<c>ValueTask</c>、泛型方法与 <c>ref</c> / <c>out</c> / <c>in</c> 参数在 Create 时直接拒绝（校验错误一次列全）。
    /// <para>接口声明非法时抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>，原始异常在 <c>Cause</c>），
    /// 与请求模型「声明期错误」同一出口；只有 AOT 元数据缺失仍抛 <see cref="AxiosAotMetadataException"/>。</para>
    /// </remarks>
    public T Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] T>()
        where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException($"类型 {typeof(T).FullName} 不是接口：Create<T>() 只能用于 interface 类型。", nameof(T));
        }

        if (GeneratedMetadataRegistry.TryGetInterface(typeof(T), out var entry))
        {
            // 声明错误在创建代理时就暴露（与 JIT 路径「Create 时校验」的体验一致）。
            ValidateInterfaceDeclaration(entry.Validate);
            return (T)entry.Factory(this);
        }

        // 守卫条件必须直接写 intrinsic：ILC 在 Native AOT 下把它折成常量 false，整段 DispatchProxy 回退
        // 随之不可达、被裁掉（否则包消费方发布时会收到 BCL 内部的 IL3050）。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            ValidateInterfaceDeclaration(() => InterfaceDeclaration.EnsureValid(typeof(T)));
            return ApiInterfaceProxy.Create<T>(this);
        }

        throw AxiosAotMetadataException.MissingInterface(typeof(T));
    }

    /// <summary>非泛型重载（供 DI 等运行时场景使用）；<paramref name="interfaceType"/> 必须是已声明路由的接口。</summary>
    /// <remarks>
    /// 已生成 / 手工注册的接口在 Native AOT 下同样可用；未注册时 AOT 显式失败，JIT 回退 DispatchProxy。
    /// 声明非法时抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>），与泛型重载同口径。
    /// </remarks>
    [RequiresUnreferencedCode("未生成元数据时的回退路径通过反射读取接口方法、参数特性与类型；裁剪时需 root 接口及其参数 / 返回类型。")]
    public object Create(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] Type interfaceType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!interfaceType.IsInterface)
        {
            throw new ArgumentException($"类型 {interfaceType.FullName} 不是接口：Create(Type) 只能用于 interface 类型。", nameof(interfaceType));
        }

        if (GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry))
        {
            ValidateInterfaceDeclaration(entry.Validate);
            return entry.Factory(this);
        }

        // 与泛型重载同一条守卫。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            ValidateInterfaceDeclaration(() => InterfaceDeclaration.EnsureValid(interfaceType));
            return ApiInterfaceProxy.Create(interfaceType, this);
        }

        throw AxiosAotMetadataException.MissingInterface(interfaceType);
    }

    /// <summary>
    /// 接口声明校验的唯一出口：非法时改写成与请求模型同一条「声明期错误」承诺 ——
    /// <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>），原始 <see cref="InvalidOperationException"/> 进 <c>Cause</c>。
    /// </summary>
    /// <remarks>
    /// 为什么在这一层改写：两个校验入口（生成的注册表 <c>entry.Validate()</c> 与反射侧的
    /// <c>InterfaceDeclaration.EnsureValid</c>）当下抛的是裸 <see cref="InvalidOperationException"/>，
    /// 而模型式路径（<c>AcquireMetadata</c>）已改成 <see cref="AxiosException"/>：同一个「接口 / 模型写错」，
    /// 用户写 <c>catch (AxiosException)</c> 会漏掉其中一边。
    /// <para>
    /// <b>注意 <see cref="AxiosAotMetadataException"/> 派生自 <see cref="InvalidOperationException"/></b>：
    /// 只看类型就会被下面这个 <c>catch</c> 一起改写掉。当前两个校验入口都只抛裸
    /// <see cref="InvalidOperationException"/>、不会抛它，所以今天不会被误吞；将来若在校验步骤里加入
    /// 会抛 <see cref="AxiosAotMetadataException"/> 的分支，必须在这里前置放行（模型的
    /// <c>AcquireMetadata</c> 已这么做）。
    /// </para>
    /// </remarks>
    /// <param name="validate">校验动作；正常返回即声明合法。</param>
    private static void ValidateInterfaceDeclaration(Action validate)
    {
        try
        {
            validate();
        }
        catch (InvalidOperationException ex)
        {
            // 声明期错误没有「本次请求配置」：Config 传 null，与模型式 AcquireMetadata 完全一致。
            throw AxiosException.InvalidOption(ex.Message, config: null, inner: ex);
        }
    }
}
