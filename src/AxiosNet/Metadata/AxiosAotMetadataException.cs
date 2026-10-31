namespace AxiosNet.Metadata;

/// <summary>
/// Native AOT 下缺少生成元数据时抛出：说明缺的是哪类元数据、哪个类型，以及三种修复方式。
/// 该异常继承 <see cref="InvalidOperationException"/>，与「声明非法」走同一条错误通道（现有的 catch 无需调整）。
/// </summary>
public sealed class AxiosAotMetadataException : InvalidOperationException
{
    private const string RepairHint =
        "修复方式（任选其一）："
        + "（1）启用 AxiosNet 源生成器（默认随包启用，确认项目未设置 <AxiosNetSourceGen>false</AxiosNetSourceGen>）；"
        + "（2）在启动期调用 AxiosApiMetadata.RegisterRequestModel / RegisterInterface / RegisterShape 手工注册（或对形状类型调用 RegisterShape<T>() 并加 DynamicallyAccessedMembers 标注，匿名形状用 RegisterShapeFromSample）；"
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
    internal static AxiosAotMetadataException MissingJsonPropertyInfo(Type type)
        => new($"类型 {Describe(type)} 没有可用的 System.Text.Json 元数据："
            + "Native AOT 下反射回退默认关闭。"
            + (IsAnonymousType(type)
                ? "该类型是匿名类型：匿名类型无法写进 JsonSerializerContext 的 [JsonSerializable]，请改用命名类型，"
                    + "或改用 params / 表单发送该对象的扁平字段。"
                : "请把该类型加入 JsonSerializerContext 的 [JsonSerializable]，"
                    + "并在 AxiosJson.Options（或实例 / 单次请求的 Serialization.JsonSerializerOptions）上设置 TypeInfoResolver。"));

    /// <summary>匿名类型的编译器生成名形如 <c>&lt;&gt;f__AnonymousType0`1</c>（C#）/ <c>VB$AnonymousType_0</c>（VB）；只做前缀判断。</summary>
    private static bool IsAnonymousType(Type type)
        => type.Name.StartsWith("<>", StringComparison.Ordinal)
            || type.Name.StartsWith("VB$AnonymousType", StringComparison.Ordinal);

    private static string Describe(Type type) => type.FullName ?? type.Name;

    private static string DescribePath(string? fieldPath)
        => string.IsNullOrEmpty(fieldPath) ? "位置：根对象。" : $"位置：{fieldPath}。";
}
