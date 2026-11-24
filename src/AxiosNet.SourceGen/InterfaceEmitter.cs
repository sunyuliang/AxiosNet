using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// 把 <see cref="InterfaceModelInfo"/> 写成真实代理类 + 注册代码：代理持有生成的调用桥静态字段，
    /// 方法体只做「取参 → 调用共享绑定实现」，不反射、不 <c>MakeGenericMethod</c>。
    /// </summary>
    internal static partial class InterfaceEmitter
    {
        private const string Task = "global::System.Threading.Tasks.Task";
        private const string CancellationToken = "global::System.Threading.CancellationToken";
        private const string Runtime = "global::AxiosNet.WebAPI.Metadata.InterfaceMethodRuntime";
        private const string AxiosResponse = "global::AxiosNet.AxiosResponse";

        /// <summary>生成接口代理的调用分派代码（每个方法一个 if 分支）。</summary>
        public static string Emit(InterfaceModelInfo model)
        {
            var builder = new StringBuilder(4096);
            GeneratedFileHeader.Append(builder);
            builder.AppendLine("namespace AxiosNet.Generated");
            builder.AppendLine("{");

            EmitProxy(builder, model);
            builder.AppendLine();
            EmitRegistration(builder, model);

            builder.AppendLine("}");
            return builder.ToString();
        }

        // 写代理类本体：特性、继承的接口、构造函数，再逐个方法落到 EmitProxyMethod。
        private static void EmitProxy(StringBuilder builder, InterfaceModelInfo model)
        {
            builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"")
                .Append(RequestModelGenerator.GeneratorName)
                .Append("\", \"")
                .Append(RequestModelGenerator.GeneratorVersion)
                .AppendLine("\")]");
            builder.Append("    internal sealed class ").Append(model.ClassName).Append(" : ").AppendLine(model.InterfaceTypeExpression);
            builder.AppendLine("    {");

            foreach (var method in model.Methods)
            {
                builder.Append("        private static readonly ").Append(Runtime).Append(" __method").Append(method.Index).AppendLine(" =");
                builder.Append("            global::AxiosNet.WebAPI.Metadata.AxiosApiMetadata.GetInterfaceMethod(typeof(")
                    .Append(model.InterfaceTypeExpression)
                    .Append("), ")
                    .Append(method.Index)
                    .Append(", ")
                    .Append(CodeLiteral.Of(method.Name))
                    .AppendLine(");");
            }

            builder.AppendLine();
            builder.AppendLine("        private readonly global::AxiosNet.WebAPI.ApiClient _client;");
            builder.AppendLine();
            builder.Append("        public ").Append(model.ClassName).AppendLine("(global::AxiosNet.WebAPI.ApiClient client)");
            builder.AppendLine("        {");
            builder.AppendLine("            _client = client;");
            builder.AppendLine("        }");

            foreach (var method in model.Methods)
            {
                builder.AppendLine();
                EmitProxyMethod(builder, model, method);
            }

            builder.AppendLine("    }");
        }

        // 写一个显式接口实现：签名照抄声明，方法体落成一次发送调用（整份正文快路或参数数组通用路径）。
        private static void EmitProxyMethod(StringBuilder builder, InterfaceModelInfo model, InterfaceMethodInfo method)
        {
            var parameters = BuildParameterList(method);
            builder.Append("        ").Append(GetReturnType(method)).Append(' ')
                .Append(method.DeclaringInterfaceExpression).Append('.').Append(EscapeIdentifier(method.Name))
                .Append('(').Append(parameters).AppendLine(")");
            builder.Append("            => __method").Append(method.Index).Append('.').Append(BuildCall(model, method)).AppendLine(";");
        }

        // 拼形参表：类型与名字按原声明顺序（含默认值与 ref / out 修饰）。
        private static string BuildParameterList(InterfaceMethodInfo method)
        {
            if (method.Parameters.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(method.Parameters[i].TypeOfExpression).Append(" p").Append(method.Parameters[i].Index);
            }

            return builder.ToString();
        }

        // 按返回形态给出代理方法自己的返回类型（Void → Task，Data → Task<T>）。
        private static string GetReturnType(InterfaceMethodInfo method)
            => method.ReturnKind switch
            {
                InterfaceReturnKind.Void => Task,
                InterfaceReturnKind.Data => Task + "<" + method.ResponseTypeExpression + ">",
                _ when method.ResponseTypeExpression is null => Task + "<" + AxiosResponse + ">",
                _ => Task + "<" + AxiosResponse + "<" + method.ResponseTypeExpression + ">>"
            };

        // 拼出真正发出的那次调用：字面量形状走整份正文重载，其余形状按参数数组交给通用路径。
        private static string BuildCall(InterfaceModelInfo model, InterfaceMethodInfo method)
            => TryBuildLiteralCall(model, method, out var literal)
                ? literal
                : BuildArgumentCall(method);

        // 字面量快路：方法 + 字面量路由 + 单整份正文（或无正文），且没有别的绑定、静态头与媒体类型声明。
        // 判据必须与运行期 InterfaceMethodMetadata 的 _literalRootBody 一致：任何一侧放宽都会让快路变成通用路径，
        // 生成代理只传了正文与取消令牌，别的绑定通道在这里是拿不回来的。
        private static bool TryBuildLiteralCall(InterfaceModelInfo model, InterfaceMethodInfo method, out string call)
        {
            call = string.Empty;

            if (method.Multipart
                || method.IsStreaming
                || model.StaticHeaders.Length > 0
                || method.StaticHeaders.Length > 0
                || method.ContentType is not null
                || method.QueryCollectionFormat is not null
                || method.RoutePath.IndexOf('{') >= 0)
            {
                return false;
            }

            var bodyIndex = -1;
            foreach (var parameter in method.Parameters)
            {
                // 被忽略参数的实参在运行期仍要进入调用载荷，快路还原不出它，因此也不判快路。
                if (parameter.Path
                    || parameter.Query
                    || parameter.Header
                    || parameter.HeaderCollection
                    || parameter.Part
                    || parameter.Ignore
                    || parameter.FormatExpression is not null)
                {
                    return false;
                }

                if (parameter.Body)
                {
                    if (parameter.BodyName is not null)
                    {
                        return false;
                    }

                    bodyIndex = parameter.Index;
                    continue;
                }

                // 剩下只允许取消令牌的位置。
                if (parameter.Index != method.CancellationTokenIndex)
                {
                    return false;
                }
            }

            var body = bodyIndex >= 0 ? "p" + bodyIndex : "(object?)null";
            var cancellationToken = method.CancellationTokenIndex >= 0
                ? "p" + method.CancellationTokenIndex
                : "default(" + CancellationToken + ")";

            call = method.ReturnKind switch
            {
                InterfaceReturnKind.Void
                    => "SendRootBodyVoidAsync(_client, " + body + ", " + cancellationToken + ")",
                InterfaceReturnKind.Data
                    => "SendRootBodyDataAsync<" + method.ResponseTypeExpression + ">(_client, " + body + ", " + cancellationToken + ")",
                _ when method.ResponseTypeExpression is null
                    => "SendRootBodyUntypedAsync(_client, " + body + ", " + cancellationToken + ")",
                _ => "SendRootBodyResponseAsync<" + method.ResponseTypeExpression + ">(_client, " + body + ", " + cancellationToken + ")"
            };

            return true;
        }

        // 通用形状的调用：把实参装箱成数组，交给绑定计划。
        private static string BuildArgumentCall(InterfaceMethodInfo method)
        {
            var arguments = BuildArgumentArray(method);
            var cancellationToken = method.CancellationTokenIndex >= 0
                ? "p" + method.CancellationTokenIndex
                : "default(" + CancellationToken + ")";

            return method.ReturnKind switch
            {
                InterfaceReturnKind.Void
                    => "SendVoidAsync(_client, " + arguments + ", " + cancellationToken + ")",
                InterfaceReturnKind.Data
                    => "SendDataAsync<" + method.ResponseTypeExpression + ">(_client, " + arguments + ", " + cancellationToken + ")",
                _ when method.ResponseTypeExpression is null
                    => "SendUntypedAsync(_client, " + arguments + ", " + cancellationToken + ")",
                _ when method.IsStreaming
                    => "SendStreamAsync<" + method.StreamFrameTypeExpression + ">(_client, " + arguments + ", " + cancellationToken
                        + ", (global::AxiosNet.AxiosStreamFormat)" + method.StreamFormat + ")",
                _ => "SendResponseAsync<" + method.ResponseTypeExpression + ">(_client, " + arguments + ", " + cancellationToken + ")"
            };
        }

        // 把实参装箱成 Invocation 需要的数组；没有参数时直接给 Array.Empty，省一次分配。
        private static string BuildArgumentArray(InterfaceMethodInfo method)
        {
            if (method.Parameters.Length == 0)
            {
                return "global::System.Array.Empty<object>()";
            }

            var builder = new StringBuilder("new object[] { ");
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('p').Append(method.Parameters[i].Index);
            }

            builder.Append(" }");
            return builder.ToString();
        }

        // 写模块初始化器里的注册项：接口声明数据 + 代理工厂，进程启动时自动登记。
        // 生成代码分三层：RegisterGeneratedInterface(接口类型, 代理工厂, 元数据构建器) —— 第三段是
        // 一串链式调用，先写接口级声明，再逐个方法写 `b.Method(...)` 及其参数声明。
        private static void EmitRegistration(StringBuilder builder, InterfaceModelInfo model)
        {
            builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"")
                .Append(RequestModelGenerator.GeneratorName)
                .Append("\", \"")
                .Append(RequestModelGenerator.GeneratorVersion)
                .AppendLine("\")]");
            builder.Append("    internal static class ").Append(model.ClassName).AppendLine("_Registration");
            builder.AppendLine("    {");
            builder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializerAttribute]");
            builder.AppendLine("        internal static void Register()");
            builder.AppendLine("        {");
            builder.AppendLine("            global::AxiosNet.WebAPI.Metadata.AxiosApiMetadata.RegisterGeneratedInterface(");
            builder.Append("                typeof(").Append(model.InterfaceTypeExpression).AppendLine("),");
            builder.Append("                static client => (object)new ").Append(model.ClassName).AppendLine("(client),");
            builder.AppendLine("                static b =>");
            builder.AppendLine("                {");

            // 接口级声明：静态头、鉴权方案、免鉴权。
            if (model.StaticHeaders.Length > 0)
            {
                builder.Append("                    b.StaticHeaders(");
                AppendStringList(builder, model.StaticHeaders);
                builder.AppendLine(");");
            }

            if (model.AuthorizeScheme is not null)
            {
                builder.Append("                    b.Authorize(").Append(CodeLiteral.Of(model.AuthorizeScheme)).AppendLine(");");
            }

            if (model.AllowAnonymous)
            {
                builder.AppendLine("                    b.Anonymous();");
            }

            // 方法级声明：一个方法一条 `b.Method(名, 动词, 路由)` 链，后续每一行都是往这条链上追加配置。
            foreach (var method in model.Methods)
            {
                builder.Append("                    b.Method(")
                    .Append(CodeLiteral.Of(method.Name))
                    .Append(", ")
                    .Append(CodeLiteral.Of(method.RouteMethod))
                    .Append(", ")
                    .Append(CodeLiteral.Of(method.RoutePath))
                    .AppendLine(")");

                if (method.StaticHeaders.Length > 0)
                {
                    builder.Append("                        .StaticHeaders(");
                    AppendStringList(builder, method.StaticHeaders);
                    builder.AppendLine(")");
                }

                if (method.AuthorizeScheme is not null)
                {
                    builder.Append("                        .Authorize(").Append(CodeLiteral.Of(method.AuthorizeScheme)).AppendLine(")");
                }

                if (method.AllowAnonymous)
                {
                    builder.AppendLine("                        .Anonymous()");
                }

                if (method.Multipart)
                {
                    builder.AppendLine("                        .Multipart()");
                }

                if (method.ContentType is not null)
                {
                    builder.Append("                        .ContentType(").Append(CodeLiteral.Of(method.ContentType)).AppendLine(")");
                }

                if (method.QueryCollectionFormat is { } queryCollectionFormat)
                {
                    builder.Append("                        .QueryCollectionFormat((global::AxiosNet.AxiosCollectionFormat)")
                        .Append(queryCollectionFormat)
                        .AppendLine(")");
                }

                if (method.IsStreaming)
                {
                    builder.Append("                        .Stream((global::AxiosNet.AxiosStreamFormat)")
                        .Append(method.StreamFormat)
                        .AppendLine(")");
                }

                // 参数级声明：`.Parameter(序号, 名, 类型)` 之后再按需追加格式 / 忽略 / 各绑定目标。
                foreach (var parameter in method.Parameters)
                {
                    builder.Append("                        .Parameter(")
                        .Append(parameter.Index)
                        .Append(", ")
                        .Append(CodeLiteral.Of(parameter.Name))
                        .Append(", typeof(")
                        .Append(parameter.TypeOfExpression)
                        .AppendLine("))");

                    if (parameter.FormatExpression is { } format)
                    {
                        builder.Append("                        .Format(").Append(format).AppendLine(")");
                    }

                    if (parameter.Ignore)
                    {
                        builder.AppendLine("                        .Ignore()");
                    }

                    if (parameter.Path)
                    {
                        builder.Append("                        .Path(").Append(CodeLiteral.OrNull(parameter.PathName)).AppendLine(")");
                    }

                    if (parameter.Query)
                    {
                        builder.Append("                        .Query(").Append(CodeLiteral.OrNull(parameter.QueryName)).AppendLine(")");
                    }

                    if (parameter.Header)
                    {
                        builder.Append("                        .Header(").Append(CodeLiteral.OrNull(parameter.HeaderName)).AppendLine(")");
                    }

                    if (parameter.HeaderCollection)
                    {
                        builder.AppendLine("                        .HeaderCollection()");
                    }

                    if (parameter.Body)
                    {
                        // 无参 [Body] 是整份正文；带名字（含空串）是聚合正文的一个键。
                        if (parameter.BodyName is null)
                        {
                            builder.AppendLine("                        .Body()");
                        }
                        else
                        {
                            builder.Append("                        .Body(").Append(CodeLiteral.Of(parameter.BodyName)).AppendLine(")");
                        }
                    }

                    if (parameter.Part)
                    {
                        if (parameter.PartName is null && parameter.PartFileName is null && parameter.PartContentType is null)
                        {
                            builder.AppendLine("                        .Part()");
                        }
                        else
                        {
                            builder.Append("                        .Part(")
                                .Append(CodeLiteral.OrNull(parameter.PartName))
                                .Append(", ")
                                .Append(CodeLiteral.OrNull(parameter.PartFileName))
                                .Append(", ")
                                .Append(CodeLiteral.OrNull(parameter.PartContentType))
                                .AppendLine(")");
                        }
                    }

                    if (!parameter.Ignore
                        && !parameter.Path
                        && !parameter.Query
                        && !parameter.Header
                        && !parameter.HeaderCollection
                        && !parameter.Body
                        && !parameter.Part)
                    {
                        builder.AppendLine("                        .Unannotated()");
                    }
                }

                builder.Append("                        .").Append(GetReturnsCall(method)).AppendLine(";");
            }

            builder.AppendLine("                });");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
        }

        // 返回形态 → 校验器里的 Returns* 调用（ReturnsVoid / ReturnsData / ReturnsStream）。
        private static string GetReturnsCall(InterfaceMethodInfo method)
            => method.ReturnKind switch
            {
                InterfaceReturnKind.Void => "ReturnsVoid()",
                InterfaceReturnKind.Data => "ReturnsData(typeof(" + method.ResponseTypeExpression + "))",
                _ when method.ResponseTypeExpression is null => "ReturnsResponse()",
                _ => "ReturnsResponse(typeof(" + method.ResponseTypeExpression + "))"
            };

        // 把字符串集合写成 C# 集合表达式，逗号分隔、不产生尾逗号。
        private static void AppendStringList(StringBuilder builder, EquatableArray<string> values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(CodeLiteral.Of(values[i]));
            }
        }

        // 关键字标识符加 @ 前缀（接口方法名可能是关键字）。
        private static string EscapeIdentifier(string name)
            => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                ? "@" + name
                : name;
    }
}
