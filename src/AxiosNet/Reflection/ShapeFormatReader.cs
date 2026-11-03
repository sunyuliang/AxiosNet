using System.Reflection;
using AxiosNet.Metadata;

namespace AxiosNet.Reflection;

/// <summary>
/// 对象形状构建时读取成员级格式化的可空回调：核心不引用声明式 API 的特性类型，
/// 由 <c>WebAPI/</c> 的模块初始化器注入；删掉 <c>WebAPI/</c> 后回调为 <c>null</c>，形状照常构建（无成员格式化）。
/// </summary>
internal static class ShapeFormatReader
{
    /// <summary>按属性读取成员级格式化；<c>null</c> 表示未注入（无声明式 API）。</summary>
    internal static Func<PropertyInfo, ValueFormat?>? Reader;
}
