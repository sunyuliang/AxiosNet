namespace AxiosNet;

/// <summary>
/// 显式要求源生成器为一个类型烘焙对象形状（params / 表单 / multipart 展平用的成员清单）。
/// 用于类型只在运行期经由 <c>object</c> / 接口 / 泛型成员出现、生成器看不到具体类型的情况。
/// </summary>
/// <remarks>
/// Native AOT 下缺少形状会在展平时抛 <see cref="Metadata.AxiosAotMetadataException"/>，JIT 下反射兜底。
/// 只在编译期生效，运行期不参与判定。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class AxiosShapeAttribute : Attribute
{
}
