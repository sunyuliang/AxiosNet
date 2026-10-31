using System.ComponentModel;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// 声明式 API 的元数据注册入口：源生成器（模块初始化器）与手工兜底都通过它把请求模型与接口声明写进注册表。
/// 隐藏 API：应用代码通常不需要直接调用；在 Native AOT 下缺少生成元数据时可按 <see cref="AxiosAotMetadataException"/> 的提示手工注册。
/// 注册只发生在启动期；同一类型重复注册直接抛错（生成器与手工声明不应同时存在）。
/// 对象形状与字典映射的注册入口在 <see cref="AxiosMetadata"/>。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AxiosApiMetadata
{
    /// <summary>
    /// 注册请求模型的声明（路由 / 静态头 / 成员绑定 / 授权）。
    /// 配置过程抛出的声明错误不会在这里抛出，而是随声明进入首次请求的构建错误（与反射路径的时序一致）。
    /// </summary>
    /// <param name="modelType">请求模型类型（仅作注册键与错误消息定位，取值由生成期写死的 getter 完成）。</param>
    /// <param name="configure">声明构建器。</param>
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
    /// <param name="interfaceType">被代理的接口类型。</param>
    /// <param name="factory">由 <see cref="ApiClient"/> 创建代理实例的工厂（生成代理的构造函数）。</param>
    /// <param name="configure">声明构建器。</param>
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
    /// 源生成代码专用的接口注册入口：同一接口可能被多个引用同一契约程序集、但互不引用的程序集各生成一份，
    /// 结果等价，保留先注册的一份而不是抛错。
    /// </summary>
    /// <param name="interfaceType">被代理的接口类型。</param>
    /// <param name="factory">由 <see cref="ApiClient"/> 创建代理实例的工厂（生成代理的构造函数）。</param>
    /// <param name="configure">声明构建器。</param>
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
    /// 取某个已注册接口的方法调用桥（生成代理的静态字段初始化时调用）。
    /// 声明错误在首次取用该方法时抛出；元数据缺失（未生成 / 未手工注册）属编程错误，直接提示如何修复。
    /// </summary>
    /// <param name="interfaceType">被代理的接口类型（与注册时一致）。</param>
    /// <param name="index">方法在注册声明里的顺序（生成器与代理共用同一顺序）。</param>
    /// <param name="methodName">生成代理写入的方法名；用于识别「代理与注册声明来自不同版本的生成器」。</param>
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
