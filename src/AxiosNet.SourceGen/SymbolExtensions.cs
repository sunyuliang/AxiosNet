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
                // class / struct / interface / record / enum 都是 BaseTypeDeclarationSyntax，delegate 单独一类。
                var modifiers = reference.GetSyntax() switch
                {
                    BaseTypeDeclarationSyntax declaration => declaration.Modifiers,
                    DelegateDeclarationSyntax declaration => declaration.Modifiers,
                    _ => default,
                };

                foreach (var modifier in modifiers)
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
