namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// Native AOT 下缺少生成元数据时抛出：说明缺的是哪类元数据、哪个类型，以及三种修复方式。
/// 该异常继承 <see cref="InvalidOperationException"/>，与「声明非法」走同一条错误通道（现有的 catch 无需调整）。
/// </summary>
public sealed class AxiosAotMetadataException : InvalidOperationException
{
    private const string RepairHint =
        "修复方式（任选其一）："
        + "（1）启用 AxiosNet 源生成器（默认随包启用，确认项目未设置 <AxiosNetSourceGen>false</AxiosNetSourceGen>）；"
        + "（2）在启动期调用 AxiosMetadata.RegisterRequestModel / RegisterInterface / RegisterShape 手工注册（或对形状类型调用 RegisterShape<T>() 并加 DynamicallyAccessedMembers 标注）；"
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
            + "或用 AxiosMetadata.RegisterInterface 手工注册。" + RepairHint);

    /// <summary>System.Text.Json 缺少该类型的源生成元数据。</summary>
    internal static AxiosAotMetadataException MissingJsonPropertyInfo(Type type)
        => new($"类型 {Describe(type)} 没有可用的 System.Text.Json 元数据："
            + "Native AOT 下反射回退默认关闭。请把该类型加入 JsonSerializerContext 的 [JsonSerializable]，"
            + "并在 AxiosJson.Options（或实例 / 单次请求的 Serialization.JsonSerializerOptions）上设置 TypeInfoResolver。");

    private static string Describe(Type type) => type.FullName ?? type.Name;
}
