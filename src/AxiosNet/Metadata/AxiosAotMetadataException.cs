namespace AxiosNet.Metadata;

/// <summary>
/// Native AOT 下缺少生成元数据时抛出：说明缺的是哪类元数据、哪个类型，以及三种修复方式。
/// 该异常继承 <see cref="InvalidOperationException"/>，与「声明非法」走同一条错误通道（现有的 catch 无需调整）。
/// </summary>
public sealed class AxiosAotMetadataException : InvalidOperationException
{
    // 修复提示：给出三个可选动作。
    private const string RepairHint =
        "修复方式（任选其一）："
        + "（1）启用 AxiosNet 源生成器（默认随包启用，确认项目未设置 <AxiosNetSourceGen>false</AxiosNetSourceGen>）；"
        + "（2）在启动期手工注册：请求模型 / 接口用 AxiosApiMetadata.RegisterRequestModel / RegisterInterface，"
        + "对象形状与字典映射用 AxiosMetadata.RegisterShape / RegisterMap（形状类型也可用 RegisterShape<T>() 并加 DynamicallyAccessedMembers 标注，匿名形状用 RegisterShapeFromSample）；"
        + "（3）改用支持动态代码（JIT）的运行时。";

    private AxiosAotMetadataException(string message)
        : base(message)
    {
    }

    /// <summary>请求模型缺少生成元数据。</summary>
    internal static AxiosAotMetadataException MissingRequestModel(Type type)
        => new($"请求模型 {Describe(type)} 缺少生成元数据：当前运行时不支持动态代码（Native AOT），无法通过反射读取属性与特性。" + RepairHint);

    /// <summary>接口代理缺少生成元数据。</summary>
    internal static AxiosAotMetadataException MissingInterface(Type type)
        => new($"接口 {Describe(type)} 缺少生成元数据：当前运行时不支持动态代码（Native AOT），无法通过 DispatchProxy 生成代理。"
            + "请确认接口声明在启用 AxiosNet 源生成器的程序集里（闭合泛型接口还需在调用点 Create<T>() / AddApiInterface<T>() 出现），"
            + "或用 AxiosApiMetadata.RegisterInterface 手工注册。" + RepairHint);

    /// <summary>params / 表单 / multipart 展平缺少对象的形状元数据。</summary>
    /// <param name="type">展平时的运行时类型（派生类型需要自己的形状，不会退回基类布局）。</param>
    /// <param name="fieldPath">展平到该对象时的字段路径（根对象为 <c>null</c>）。</param>
    internal static AxiosAotMetadataException MissingShape(Type type, string? fieldPath)
        => new($"类型 {Describe(type)} 缺少对象形状元数据：当前运行时不支持动态代码（Native AOT），无法反射读取属性来展平 params / 表单 / multipart。"
            + $"{DescribePath(fieldPath)}形状由源生成器从请求模型属性、接口方法参数与调用点参数自动生成；"
            + "运行时构造的派生类型 / 动态类型没有生成形状，请改用静态类型、为它标注 [AxiosShape]，"
            + "或在启动期调用 AxiosMetadata.RegisterShape / RegisterShapeFromSample(sample, ...) 手工注册。");

    /// <summary>自定义只读字典缺少映射访问器。</summary>
    internal static AxiosAotMetadataException MissingMap(Type type)
        => new($"类型 {Describe(type)} 缺少字典映射访问器：当前运行时不支持动态代码（Native AOT），"
            + "无法反射读取自定义只读字典的键值对。请让该类型在源生成器可见的调用点出现（如作为 params / 请求模型属性），"
            + "或在启动期调用 AxiosMetadata.RegisterMap 手工注册。");

    /// <summary>System.Text.Json 缺少该类型的源生成元数据。</summary>
    /// <remarks>
    /// 修复建议按资产分派：<c>TypeInfoResolver</c> 是 .NET 7 才有的属性，net6.0 的
    /// <c>JsonSerializerOptions</c> 上没有它；netstandard2.0 资产虽然引用 STJ 8.x（有该属性），
    /// 但序列化路径只编了反射分支，同样接不进源生成元数据。两种资产都不能建议「去设置它」。
    /// </remarks>
    internal static AxiosAotMetadataException MissingJsonPropertyInfo(Type type)
        => new($"类型 {Describe(type)} 没有可用的 System.Text.Json 元数据："
            + "Native AOT 下反射回退默认关闭。"
            + (IsAnonymousType(type)
                ? "该类型是匿名类型：匿名类型无法写进 JsonSerializerContext 的 [JsonSerializable]，请改用命名类型，"
                    + "或改用 params / 表单发送该对象的扁平字段。"
                : JsonMetadataHint));

#if NET8_0_OR_GREATER
    /// <summary>net8.0：<c>TypeInfoResolver</c> 可用，按源生成上下文接入。</summary>
    private const string JsonMetadataHint =
        "请把该类型加入 JsonSerializerContext 的 [JsonSerializable]，"
        + "并在 AxiosJson.Options（或实例 / 单次请求的 Serialization.JsonOptions）上设置 TypeInfoResolver。";
#elif NETSTANDARD2_0
    /// <summary>netstandard2.0 兼容资产：序列化路径只编了反射分支，接不进源生成元数据。</summary>
    private const string JsonMetadataHint =
        "netstandard2.0 兼容资产的序列化路径只走反射，不接入源生成元数据："
        + "请改用自定义 IContentSerializer，或让项目多目标到 net8.0 以取得现代资产。";
#else
    /// <summary>net6.0：该目标的 <c>JsonSerializerOptions</c> 没有 <c>TypeInfoResolver</c>，只能换序列化器或升目标框架。</summary>
    private const string JsonMetadataHint =
        "net6.0 目标的 System.Text.Json 没有 TypeInfoResolver 属性，无法接入源生成元数据："
        + "请改用自定义 IContentSerializer，或把目标框架提升到 net8.0。";
#endif

    /// <summary>匿名类型的编译器生成名形如 <c>&lt;&gt;f__AnonymousType0`1</c>（C#）/ <c>VB$AnonymousType_0</c>（VB）；只做前缀判断。</summary>
    private static bool IsAnonymousType(Type type)
        => type.Name.StartsWith("<>", StringComparison.Ordinal)
            || type.Name.StartsWith("VB$AnonymousType", StringComparison.Ordinal);

    /// <summary>异常消息里的类型名：全名优先，无名类型退回短名。</summary>
    private static string Describe(Type type) => type.FullName ?? type.Name;

    /// <summary>异常消息里的位置描述：没有字段路径时写成「根对象」。</summary>
    private static string DescribePath(string? fieldPath)
        => string.IsNullOrEmpty(fieldPath) ? "位置：根对象。" : $"位置：{fieldPath}。";
}
