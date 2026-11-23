using System.Collections.Generic;
using System.Text;

namespace AxiosNet.SourceGen
{
    /// <summary>DI 注册扩展的发射：把声明过路由的接口按单例注册进容器。</summary>
    internal static partial class InterfaceEmitter
    {
        /// <summary>
        /// 生成 DI 注册扩展：把本程序集里声明过路由的接口一次性按单例注册进容器。
        /// 逐字调用公开的 <c>AddApiInterface&lt;T&gt;()</c>，因此站点冲突检测、命名站点、单例语义与手写注册完全同一份实现；
        /// 相对手写的好处是类型在编译期绑定——接口改名会让这一行编译失败，而不是在运行时少注册一个服务。
        /// </summary>
        public static string EmitDependencyInjection(IReadOnlyList<InterfaceModelInfo> models)
        {
            const string Services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
            const string Extensions = "global::AxiosNet.WebAPI.DependencyInjection.ApiInterfaceServiceCollectionExtensions";

            var builder = new StringBuilder(1024 + (models.Count * 96));
            GeneratedFileHeader.Append(builder);
            builder.AppendLine("namespace AxiosNet.Generated");
            builder.AppendLine("{");
            builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"")
                .Append(RequestModelGenerator.GeneratorName)
                .Append("\", \"")
                .Append(RequestModelGenerator.GeneratorVersion)
                .AppendLine("\")]");
            builder.AppendLine("    public static class AxiosNetGeneratedDependencyInjection");
            builder.AppendLine("    {");
            builder.AppendLine("        /// <summary>把本程序集里全部声明过路由的接口按单例注册到未命名站点。</summary>");
            builder.Append("        public static ").Append(Services).AppendLine(" AddAxiosGeneratedApiInterfaces(this " + Services + " services)");
            builder.AppendLine("            => AddAxiosGeneratedApiInterfaces(services, siteName: null);");
            builder.AppendLine();
            builder.AppendLine("        /// <summary>把本程序集里全部声明过路由的接口按单例注册到指定命名站点（null = 未命名站点）。</summary>");
            builder.Append("        public static ").Append(Services).AppendLine(" AddAxiosGeneratedApiInterfaces(this " + Services + " services, string siteName)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (services is null)");
            builder.AppendLine("            {");
            builder.AppendLine("                throw new global::System.ArgumentNullException(nameof(services));");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (siteName is null)");
            builder.AppendLine("            {");

            foreach (var model in models)
            {
                builder.Append("                ").Append(Extensions).Append(".")
                    .Append("AddApiInterface<").Append(model.InterfaceTypeExpression).AppendLine(">(services);");
            }

            builder.AppendLine("            }");
            builder.AppendLine("            else");
            builder.AppendLine("            {");

            foreach (var model in models)
            {
                builder.Append("                ").Append(Extensions).Append(".")
                    .Append("AddApiInterface<").Append(model.InterfaceTypeExpression).AppendLine(">(services, siteName);");
            }

            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return services;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");

            return builder.ToString();
        }
    }
}
