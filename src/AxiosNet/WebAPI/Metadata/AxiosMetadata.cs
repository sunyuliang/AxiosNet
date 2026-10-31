using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// 元数据注册入口：源生成器（模块初始化器）与手工兜底都通过它把声明写进注册表。
/// 隐藏 API：应用代码通常不需要直接调用；在 Native AOT 下缺少生成元数据时可按 <see cref="AxiosAotMetadataException"/> 的提示手工注册。
/// 注册只发生在启动期；同一类型重复注册直接抛错（生成器与手工声明不应同时存在）。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AxiosMetadata
{
    /// <summary>
    /// 冻结元数据注册表：通常放在启动末尾调用一次，之后任何注册（生成器模块初始化器 / 手工注册）都会抛错。
    /// 它把「运行期才加载、才注册」暴露成可定位的启动失败，而不是某次请求上出现的行为差异。可重复调用。
    /// </summary>
    /// <remarks>
    /// 冻结是进程级的，且只禁止真正写入（等价重复注册仍是空操作）：调用之后才被首次访问的程序集，
    /// 其模块初始化器里的注册会失败。因此只在「所有会用到的程序集都已加载」的宿主里使用（典型是启动即固定的服务端进程）。
    /// 不调用也完全可用——这是可选加固，不是必需步骤。
    /// </remarks>
    public static void Freeze() => GeneratedMetadataRegistry.Freeze();

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

    /// <summary>
    /// 注册对象形状（params / 表单 / multipart 展平）：每个成员一个取值委托。
    /// 配置过程抛出的声明错误不会在这里抛出，而是随形状进入首次展平该类型时的构建错误。
    /// </summary>
    /// <param name="type">参与展平的类型。</param>
    /// <param name="configure">形状构建器。</param>
    public static void RegisterShape(Type type, Action<ObjectShapeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ObjectShapeBuilder(type);
        ObjectShape shape;
        try
        {
            configure(builder);
            shape = builder.Build();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            shape = new ObjectShape(type, [], ex.Message);
        }

        GeneratedMetadataRegistry.RegisterShape(shape);
    }

    /// <summary>
    /// 按公共可读属性生成形状的兜底入口：类型参数上的 <see cref="DynamicallyAccessedMembersAttribute"/>
    /// 保证属性元数据被裁剪器保留，因此 Native AOT 下反射取值仍然可用。
    /// </summary>
    /// <typeparam name="T">参与展平的类型。</typeparam>
    public static void RegisterShape<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        => GeneratedMetadataRegistry.RegisterShape(ObjectShapeBuilder.FromReflection<T>());

    /// <summary>
    /// 生成代码专用的形状注册入口：<paramref name="sample"/> 只用于推断 <typeparamref name="T"/>
    /// （匿名类型无法写出类型名，只能由样本推断）；成员取值是编译期生成的直接委托。
    /// </summary>
    /// <typeparam name="T">参与展平的类型（可以是匿名类型）。</typeparam>
    /// <param name="sample">用于推断类型参数的样本，运行期不读取。</param>
    /// <param name="configure">成员声明。</param>
    public static void RegisterShapeFromSample<T>(T sample, Action<ShapeBuilder<T>> configure)
    {
        _ = sample;
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ShapeBuilder<T>();
        ObjectShape shape;
        try
        {
            configure(builder);
            shape = builder.Build(typeof(T));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            shape = new ObjectShape(typeof(T), [], ex.Message);
        }

        GeneratedMetadataRegistry.RegisterShape(shape);
    }

    /// <summary>
    /// 生成代码专用的形状注册入口：两个互不引用的程序集可能各自为同一被引用类型生成形状，
    /// 结果等价，保留先注册的一份（模块初始化器重复注册不应让进程启动失败）。
    /// </summary>
    public static void RegisterGeneratedShapeFromSample<T>(T sample, Action<ShapeBuilder<T>> configure)
    {
        _ = sample;
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ShapeBuilder<T>();
        ObjectShape shape;
        try
        {
            configure(builder);
            shape = builder.Build(typeof(T));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            shape = new ObjectShape(typeof(T), [], ex.Message);
        }

        GeneratedMetadataRegistry.RegisterGeneratedShape(shape);
    }

    /// <summary>注册自定义只读字典的枚举访问器（形状里映射不出 <see cref="IDictionary{TKey,TValue}"/> 的只读实现用）；每个类型只能注册一个。</summary>
    /// <param name="type">字典类型。</param>
    /// <param name="enumerate">把实例枚举成键值对（<c>null</c> 键在展平时跳过）。</param>
    public static void RegisterMap(Type type, Func<object, IEnumerable<KeyValuePair<string, object?>>> enumerate)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(enumerate);
        GeneratedMetadataRegistry.RegisterMap(new MapAccessor(type, enumerate));
    }

    /// <summary>生成代码专用的映射访问器注册入口：重复注册保留先注册的一份（与 <see cref="RegisterGeneratedShapeFromSample{T}"/> 同理）。</summary>
    /// <param name="type">字典类型。</param>
    /// <param name="enumerate">把实例枚举成键值对。</param>
    public static void RegisterGeneratedMap(Type type, Func<object, IEnumerable<KeyValuePair<string, object?>>> enumerate)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(enumerate);
        GeneratedMetadataRegistry.RegisterGeneratedMap(new MapAccessor(type, enumerate));
    }
}
