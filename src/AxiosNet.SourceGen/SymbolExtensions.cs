using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AxiosNet.SourceGen
{
    internal static class SymbolExtensions
    {
        /// <summary>file-local 类型（C# 11 的 <c>file</c> 修饰符）：其它文件里的生成代码无法引用。</summary>
        public static bool IsFileLocal(this INamedTypeSymbol symbol)
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
                {
                    continue;
                }

                foreach (var modifier in declaration.Modifiers)
                {
                    if (modifier.ValueText == "file")
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
