namespace AxiosNet;

/// <summary>
/// 显式要求源生成器为一个类型烘焙对象形状（params / 表单 / multipart 展平用的成员清单）。
/// 需要它的情况：类型只在运行期经由 <c>object</c> / 接口 / 泛型成员出现，生成器看不到具体类型。
/// Native AOT 下没有形状会在展平时抛出 <see cref="WebAPI.Metadata.AxiosAotMetadataException"/>；JIT 下反射兜底。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class AxiosShapeAttribute : Attribute
{
}
