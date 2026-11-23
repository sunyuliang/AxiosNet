using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.SourceGen
{
    /// <summary>把 <see cref="RequestModelInfo"/> 写成模块初始化器里的注册代码（只调用公开隐藏元数据 API，不碰 internal 类型）。</summary>
    internal static class RequestModelEmitter
    {
        /// <summary>生成请求模型的元数据注册代码。</summary>
        public static string Emit(RequestModelInfo model)
        {
            var builder = new StringBuilder(2048);
            GeneratedFileHeader.Append(builder);
            builder.Append("namespace ").AppendLine("AxiosNet.Generated");
            builder.AppendLine("{");
            builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"").Append(RequestModelGenerator.GeneratorName).Append("\", \"").Append(RequestModelGenerator.GeneratorVersion).AppendLine("\")]");
            builder.Append("    internal static class ").AppendLine(model.ClassName);
            builder.AppendLine("    {");
            builder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializerAttribute]");
            builder.AppendLine("        internal static void Register()");
            builder.AppendLine("        {");
            builder.AppendLine("            global::AxiosNet.WebAPI.Metadata.AxiosApiMetadata.RegisterRequestModel(");
            builder.Append("                typeof(").Append(model.FullTypeName).AppendLine("),");
            builder.AppendLine("                static b =>");
            builder.AppendLine("                {");
            builder.Append("                    b.Route(").Append(CodeLiteral.Of(model.RouteMethod)).Append(", ").Append(CodeLiteral.Of(model.RoutePath)).AppendLine(");");

            if (model.StaticHeaders.Length > 0)
            {
                builder.Append("                    b.StaticHeaders(");
                for (var i = 0; i < model.StaticHeaders.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(CodeLiteral.Of(model.StaticHeaders[i]));
                }

                builder.AppendLine(");");
            }

            if (model.Multipart)
            {
                builder.AppendLine("                    b.Multipart();");
            }

            if (model.ContentType is not null)
            {
                builder.Append("                    b.ContentType(").Append(CodeLiteral.Of(model.ContentType)).AppendLine(");");
            }

            if (model.QueryCollectionFormat is not null)
            {
                builder.Append("                    b.QueryCollectionFormat((global::AxiosNet.AxiosCollectionFormat)")
                    .Append(model.QueryCollectionFormat.Value)
                    .AppendLine(");");
            }

            if (model.HasStreamAttribute)
            {
                builder.Append("                    b.Stream((global::AxiosNet.AxiosStreamFormat)")
                    .Append(model.StreamFormat)
                    .AppendLine(");");
            }

            if (model.AuthorizeScheme is not null)
            {
                builder.Append("                    b.Authorize(").Append(CodeLiteral.Of(model.AuthorizeScheme)).AppendLine(");");
            }

            if (model.AllowAnonymous)
            {
                builder.AppendLine("                    b.Anonymous();");
            }

            if (model.ImplementsApiRequest)
            {
                builder.AppendLine("                    b.ImplementsApiRequest();");
            }

            foreach (var member in model.Members)
            {
                EmitMember(builder, member);
            }

            builder.AppendLine("                });");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");

            return builder.ToString();
        }

        // 写一个成员的注册：名字、类型、取值表达式与绑定，全部是编译期算好的字符串。
        private static void EmitMember(StringBuilder builder, RequestMemberInfo member)
        {
            builder.Append("                    b.Member(")
                .Append(CodeLiteral.Of(member.Name))
                .Append(", ")
                .Append(member.TypeOfExpression)
                .Append(", ")
                .Append(member.GetterExpression)
                .AppendLine(", static m =>");
            builder.AppendLine("                    {");
            // 只有显式命名（[JsonPropertyName]）才需要写出字段名；未显式时默认值就是成员名，且参与命名策略。
            if (member.NameFromAttribute)
            {
                builder.Append("                        m.FieldName(").Append(CodeLiteral.Of(member.FieldName)).AppendLine(");");
            }

            if (member.ContentType is not null)
            {
                builder.Append("                        m.ContentType(").Append(CodeLiteral.Of(member.ContentType)).AppendLine(");");
            }

            if (member.QueryCollectionFormat is not null)
            {
                builder.Append("                        m.QueryCollectionFormat((global::AxiosNet.AxiosCollectionFormat)")
                    .Append(member.QueryCollectionFormat.Value)
                    .AppendLine(");");
            }

            if (member.HasStreamAttribute)
            {
                builder.Append("                        m.Stream((global::AxiosNet.AxiosStreamFormat)")
                    .Append(member.StreamFormat)
                    .AppendLine(");");
            }

            if (member.FormatExpression is { } format)
            {
                builder.Append("                        m.Format(").Append(format).AppendLine(");");
            }

            if (member.Ignore)
            {
                builder.AppendLine("                        m.Ignore();");
            }

            // 一个成员可以同时命中多个绑定目标（如 Path + Query 都用同一个值），
            // 这里逐项照原样写出去，冲突留给构建期校验统一报错。
            foreach (var binding in member.Bindings)
            {
                if (binding.Path)
                {
                    EmitOptionalName(builder, "Path", binding.PathName);
                }

                if (binding.Query)
                {
                    EmitOptionalName(builder, "Query", binding.QueryName);
                }

                if (binding.Header)
                {
                    EmitOptionalName(builder, "Header", binding.HeaderName);
                }

                if (binding.HeaderCollection)
                {
                    builder.AppendLine("                        m.HeaderCollection();");
                }

                if (binding.Body)
                {
                    EmitOptionalName(builder, "Body", binding.BodyName);
                }

                if (binding.RootBody)
                {
                    builder.AppendLine("                        m.RootBody();");
                }

                if (binding.Part)
                {
                    if (binding.PartName is null && binding.PartFileName is null && binding.PartContentType is null)
                    {
                        builder.AppendLine("                        m.Part();");
                    }
                    else
                    {
                        builder.Append("                        m.Part(")
                            .Append(CodeLiteral.OrNull(binding.PartName))
                            .Append(", ")
                            .Append(CodeLiteral.OrNull(binding.PartFileName))
                            .Append(", ")
                            .Append(CodeLiteral.OrNull(binding.PartContentType))
                            .AppendLine(");");
                    }
                }
            }

            builder.Append("                    }, ").Append(CodeLiteral.Of(member.Description)).AppendLine(");");
        }

        // 带名字的绑定调用：名字为空时省掉实参，调用不带名字的重载。
        private static void EmitOptionalName(StringBuilder builder, string method, string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                builder.Append("                        m.").Append(method).AppendLine("();");
                return;
            }

            builder.Append("                        m.").Append(method).Append('(').Append(CodeLiteral.Of(name!)).AppendLine(");");
        }

    }
}
