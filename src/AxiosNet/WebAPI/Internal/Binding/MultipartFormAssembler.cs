
using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>把已校验的部件绑定装成 <see cref="FormData"/>：类模型与接口参数共用同一套取值 / 覆盖 / 回收规则。</summary>
internal static class MultipartFormAssembler
{
    /// <summary>按绑定顺序装配表单；值为 <c>null</c> 的部件跳过，全部跳过时返回 <c>null</c>。</summary>
    /// <param name="sourceName">表单来源名（错误消息与诊断用）。</param>
    /// <param name="bindings">部件绑定（名字、取值源、覆盖项）。</param>
    /// <param name="target">请求模型实例或方法参数数组。</param>
    /// <returns>装配好的表单；没有可发部件时为 <c>null</c>。</returns>
    /// <remarks>装配中途失败时回收已经取得的流与内容，再把异常交给调用方。</remarks>
    public static FormData? Build(string sourceName, PartBinding[] bindings, object target)
    {
        var form = new FormData { SourceName = sourceName };

        try
        {
            foreach (var binding in bindings)
            {
                var value = binding.Source.GetValue(target);
                if (value is null)
                {
                    continue;
                }

                if (binding.Source.Format is { } format)
                {
                    form.Formats[binding.Name] = format;
                }

                // 声明覆盖与值分开登记，覆盖在装配时由 MultipartBodyBuilder 应用。
                var partOverride = new FormPartOverride(binding.FileName, binding.ContentType, binding.HasExplicitName);
                if (partOverride.Any)
                {
                    form.Overrides[binding.Name] = partOverride;
                    form.Add(binding.Name, MultipartValues.MaterializeDeclaredCollection(value));
                }
                else
                {
                    form.Add(binding.Name, value);
                }
            }
        }
        catch
        {
            // 还没交给 MultipartBodyBuilder：已取得的流 / 内容必须就地释放，否则会随异常泄漏。
            form.DisposeOwned();
            throw;
        }

        return form.Count > 0 ? form : null;
    }
}
