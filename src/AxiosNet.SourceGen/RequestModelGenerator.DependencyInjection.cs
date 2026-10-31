using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// DI 生成扩展：为「声明过路由的接口」生成一个 <c>AddAxiosGeneratedApiInterfaces()</c> 扩展方法，
    /// 让容器注册不必逐个手写 <c>AddApiInterface&lt;T&gt;()</c>。
    /// </summary>
    /// <remarks>
    /// 只在编译引用了 <c>AxiosNet.DependencyInjection</c> 时才产生这段源码：没引 DI 的工程里生成它会让编译直接失败。
    /// 注册动作逐字调用公开扩展方法，语义（命名站点、单例、冲突检测）与手写完全一致。
    /// </remarks>
    public sealed partial class RequestModelGenerator
    {
        private const string DependencyInjectionAssembly = "AxiosNet.DependencyInjection";

        private static void InitializeDependencyInjection(
            IncrementalGeneratorInitializationContext context,
            IncrementalValueProvider<ImmutableArray<InterfaceModelInfo>> interfaces,
            IncrementalValueProvider<GeneratorOptions> options)
        {
            // 只把「有没有引用 DI 包」这一位布尔喂进管线：Compilation 每次编辑都会变，
            // 直接 Combine 整个 Compilation 会让这段输出在每次按键后重跑。
            var hasDependencyInjection = context.CompilationProvider.Select(static (compilation, _) =>
            {
                foreach (var reference in compilation.ReferencedAssemblyNames)
                {
                    if (string.Equals(reference.Name, DependencyInjectionAssembly, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            });

            context.RegisterSourceOutput(
                interfaces.Combine(options).Combine(hasDependencyInjection),
                static (output, pair) => ExecuteDependencyInjection(output, pair.Left.Left, pair.Left.Right, pair.Right));
        }

        private static void ExecuteDependencyInjection(
            SourceProductionContext context,
            ImmutableArray<InterfaceModelInfo> interfaces,
            GeneratorOptions options,
            bool hasDependencyInjection)
        {
            if (!options.Enabled || !hasDependencyInjection)
            {
                return;
            }

            var registrable = new List<InterfaceModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in interfaces)
            {
                // 闭合泛型调用点生成的代理注册不进容器：调用点要的是那一个闭合类型，
                // 自动注册成服务反而会把「随手用过一次」变成容器契约。
                if (!model.Emit || model.OpenGeneric || !IsPlainDeclaration(model))
                {
                    continue;
                }

                if (seen.Add(model.FullTypeName))
                {
                    registrable.Add(model);
                }
            }

            if (registrable.Count == 0)
            {
                return;
            }

            registrable.Sort(static (left, right) => string.CompareOrdinal(left.FullTypeName, right.FullTypeName));
            context.AddSource("AxiosNet.GeneratedDependencyInjection.g.cs", InterfaceEmitter.EmitDependencyInjection(registrable));
        }

        /// <summary>闭合泛型调用点生成的模型：<c>GenericDefinitionFullName</c> 是定义而不是自身全名。</summary>
        private static bool IsPlainDeclaration(InterfaceModelInfo model)
            => string.Equals(model.GenericDefinitionFullName, model.FullTypeName, StringComparison.Ordinal);
    }
}
