using System.ComponentModel;
using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// 声明式 API 的元数据注册入口：源生成器与手工兜底都用它把请求模型与接口声明写进注册表。隐藏 API。
/// </summary>
/// <remarks>
/// <para>应用代码通常不需要直接调用；Native AOT 下缺少生成元数据时可按 <see cref="AxiosAotMetadataException"/> 的提示手工注册。注册只发生在启动期。</para>
/// <para>手工入口对同一类型重复注册直接抛错；生成器入口对等价重复保留先注册的一份。对象形状与字典映射的注册入口在 <see cref="AxiosMetadata"/>。</para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AxiosApiMetadata
{
    /// <summary>
    /// 注册请求模型的声明（路由 / 静态头 / 成员绑定 / 授权）；配置期错误不在这里抛出，而是随声明进入首次请求的构建错误。
    /// </summary>
    public static void RegisterRequestModel(Type modelType, Action<RequestModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new RequestModelBuilder(modelType);
        try
        {
            configure(builder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            builder.Declaration.Error = ex.Message;
        }

        GeneratedMetadataRegistry.RegisterRequestModel(builder.Declaration);
    }

    /// <summary>注册接口级声明的代理工厂与方法声明；配置期错误延迟到首次创建代理时抛出。</summary>
    public static void RegisterInterface(
        Type interfaceType,
        Func<ApiClient, object> factory,
        Action<InterfaceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new InterfaceBuilder(interfaceType);
        try
        {
            configure(builder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            builder.Declaration.Error = ex.Message;
        }

        GeneratedMetadataRegistry.RegisterInterface(builder.Declaration, factory);
    }

    /// <summary>
    /// 源生成代码专用的接口注册入口：同一接口可能被多个程序集各生成一份，结果等价，保留先注册的一份而不是抛错。
    /// </summary>
    public static void RegisterGeneratedInterface(
        Type interfaceType,
        Func<ApiClient, object> factory,
        Action<InterfaceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new InterfaceBuilder(interfaceType);
        try
        {
            configure(builder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            builder.Declaration.Error = ex.Message;
        }

        GeneratedMetadataRegistry.RegisterGeneratedInterface(builder.Declaration, factory);
    }

    /// <summary>
    /// 取某个已注册接口的方法调用桥（生成代理的静态字段初始化时调用）；声明错误在首次取用该方法时抛出，元数据缺失直接抛 <see cref="AxiosAotMetadataException"/>。
    /// </summary>
    public static InterfaceMethodRuntime GetInterfaceMethod(Type interfaceType, int index, string? methodName = null)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry))
        {
            throw AxiosAotMetadataException.MissingInterface(interfaceType);
        }

        var methods = entry.GetMethods();
        if ((uint)index >= (uint)methods.Length)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.FullName} 没有第 {index} 个方法声明：代理与注册声明的顺序不一致（元数据已过期，请重新编译）。");
        }

        if (methodName is not null && !string.Equals(methods[index].Name, methodName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.FullName} 的第 {index} 个方法声明是 {methods[index].Name}，代理期望 {methodName}："
                + "代理与注册声明来自不同版本的生成器（元数据已过期，请重新编译）。");
        }

        methods[index].EnsureValid();
        return new InterfaceMethodRuntime(methods[index]);
    }
}
