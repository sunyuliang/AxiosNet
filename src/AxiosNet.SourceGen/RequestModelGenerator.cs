using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// 请求模型源生成器：编译期为每个带路由特性的请求模型烘焙一份声明，经 <c>AxiosApiMetadata.RegisterRequestModel</c>
    /// 写入运行时注册表。声明里只有事实（路由 / 静态头 / 成员 / 绑定 / 授权），校验仍由运行时共享构建器完成。
    /// 接口代理（真实代理类 + <c>RegisterInterface</c>）见 <c>RequestModelGenerator.Interfaces.cs</c>。
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed partial class RequestModelGenerator : IIncrementalGenerator
    {
        internal const string GeneratorName = "AxiosNet.SourceGen";
        internal const string GeneratorVersion = "1.0.0";

        /// <summary>生成器入口：请求模型、接口代理、对象形状、启动期校验与 DI 注册扩展各自独立成管线。</summary>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, ct) => Transform(ctx, ct))
                .Where(static model => model is not null)
                .Select(static (model, _) => model!);

            var collectedModels = models.Collect();
            var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => new GeneratorOptions(
                Enabled: !string.Equals(GetProperty(provider, "build_property.AxiosNetSourceGen"), "false", StringComparison.OrdinalIgnoreCase),
                PublishAot: string.Equals(GetProperty(provider, "build_property.PublishAot"), "true", StringComparison.OrdinalIgnoreCase)));

            context.RegisterSourceOutput(collectedModels.Combine(options), static (output, pair)
                => Execute(output, pair.Left, pair.Right));

            var interfaces = InitializeInterfaces(context, options);
            InitializeShapes(context, options);
            InitializeValidation(context, collectedModels, interfaces, options);
            InitializeDependencyInjection(context, interfaces, options);
        }

        private static bool IsCandidate(SyntaxNode node)
            => node is TypeDeclarationSyntax declaration
                && declaration is not InterfaceDeclarationSyntax
                && declaration.AttributeLists.Count > 0;

        private static string? GetProperty(AnalyzerConfigOptionsProvider provider, string key)
            => provider.GlobalOptions.TryGetValue(key, out var value) ? value : null;

        private static void Execute(SourceProductionContext context, ImmutableArray<RequestModelInfo> models, GeneratorOptions options)
        {
            if (!options.Enabled)
            {
                if (options.PublishAot)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.GeneratorDisabledWithAot, Location.None));
                }

                return;
            }

            // 同一类型的多个 partial 声明只处理首个（transform 里有同样过滤），这里再按全名兜底去重。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                if (!seen.Add(model.FullTypeName))
                {
                    continue;
                }

                foreach (var diagnostic in model.Diagnostics)
                {
                    context.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.MessageArgs.ToArray()));
                }

                if (!model.Emit)
                {
                    continue;
                }

                context.AddSource(model.HintName, RequestModelEmitter.Emit(model));
            }
        }

        private static RequestModelInfo? Transform(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken)
        {
            var declaration = (TypeDeclarationSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            // partial：只在「第一段带特性的声明」生成（每段都会触发 transform）。
            // 不能固定用 references[0]：路由特性可能写在后一段，而前一段只有成员声明。
            var references = symbol.DeclaringSyntaxReferences;
            if (references.Length > 1)
            {
                foreach (var reference in references)
                {
                    if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax part || part.AttributeLists.Count == 0)
                    {
                        continue;
                    }

                    if (part.SyntaxTree != declaration.SyntaxTree || !part.Span.Equals(declaration.Span))
                    {
                        return null;
                    }

                    break;
                }
            }

            var compilation = semanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null)
            {
                // 没有引用 AxiosNet：不可能是请求模型。
                return null;
            }

            var routeAttributes = symbol.GetAttributes()
                .Where(attribute => DerivesFrom(attribute.AttributeClass, wellKnown.HttpApi))
                .ToList();

            if (routeAttributes.Count == 0)
            {
                return null;
            }

            return BuildModelInfo(symbol, routeAttributes, declaration, wellKnown, compilation);
        }

        private static RequestModelInfo BuildModelInfo(
            INamedTypeSymbol symbol,
            List<AttributeData> routeAttributes,
            TypeDeclarationSyntax declaration,
            WellKnownSymbols wellKnown,
            Compilation compilation)
        {
            var location = declaration.Identifier.GetLocation();
            var fullTypeName = symbol.ToDisplayString(TypeFormats.FullyQualified);
            var className = BuildClassName(symbol);
            var hintName = "AxiosNet.RequestModel." + className + ".g.cs";
            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

            RequestModelInfo Rejected(DiagnosticDescriptor descriptor, Location diagnosticLocation, params string[] args)
                => new RequestModelInfo(
                    fullTypeName,
                    className,
                    hintName,
                    string.Empty,
                    string.Empty,
                    EquatableArray<string>.Empty,
                    false,
                    false,
                    null,
                    false,
                    EquatableArray<RequestMemberInfo>.Empty,
                    AddDiagnostic(diagnostics, new DiagnosticInfo(descriptor, ToEquatable(args), diagnosticLocation)),
                    false);

            if (routeAttributes.Count > 1)
            {
                var names = string.Join("、", routeAttributes.Select(attribute => attribute.AttributeClass!.Name));
                return Rejected(Diagnostics.RouteCountInvalid, location, "请求模型 " + symbol.Name, "标注了多个路由特性（" + names + "）");
            }

            var route = routeAttributes[0];
            var routeClass = route.AttributeClass!;
            var isBuiltInRoute = BuiltInRouteMethods.TryGetValue(routeClass.ToDisplayString(), out var builtInMethod);
            string? routeMethod;
            string? routePath;

            if (isBuiltInRoute)
            {
                routeMethod = builtInMethod;
                routePath = GetStringArgument(route, 0);
            }
            else if (SymbolEqualityComparer.Default.Equals(routeClass, wellKnown.HttpApi))
            {
                routeMethod = GetStringArgument(route, 0);
                routePath = GetStringArgument(route, 1);
            }
            else
            {
                // 自定义 HttpApiAttribute 子类：只有运行期才能执行其构造器，生成器静态求不出路由。
                return Rejected(Diagnostics.CustomRouteAttribute, route.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? location, "请求模型 " + symbol.Name, routeClass.Name);
            }

            if (string.IsNullOrWhiteSpace(routeMethod) || string.IsNullOrWhiteSpace(routePath))
            {
                return Rejected(Diagnostics.RouteValueInvalid, route.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? location, "请求模型 " + symbol.Name);
            }

            var unsupportedReason = GetUnsupportedReason(symbol, compilation);
            if (unsupportedReason is not null)
            {
                return Rejected(Diagnostics.UnsupportedType, location, "请求模型 " + symbol.Name, unsupportedReason);
            }

            var members = ImmutableArray.CreateBuilder<RequestMemberInfo>();
            var memberDiags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
            foreach (var property in EnumerateProperties(symbol))
            {
                if (property.GetMethod is null)
                {
                    continue;
                }

                if (property.GetMethod.DeclaredAccessibility != Accessibility.Public)
                {
                    unsupportedReason = $"属性 {property.Name} 的 getter 不是 public，生成代码无法直接取值";
                }
                else if (!IsTypeAccessible(property.Type, compilation.Assembly))
                {
                    unsupportedReason = $"属性 {property.Name} 的类型 {property.Type.ToDisplayString()} 对生成代码不可见";
                }
                else if (property.Type is INamedTypeSymbol { IsRefLikeType: true } || property.Type.TypeKind == TypeKind.Pointer || property.Type.TypeKind == TypeKind.FunctionPointer)
                {
                    unsupportedReason = $"属性 {property.Name} 的类型 {property.Type.ToDisplayString()} 是 ref struct / 指针，无法装箱取值";
                }

                if (unsupportedReason is not null)
                {
                    return Rejected(Diagnostics.UnsupportedType, location, "请求模型 " + symbol.Name, unsupportedReason);
                }

                members.Add(BuildMemberInfo(symbol, property, wellKnown, memberDiags));
            }

            diagnostics.AddRange(memberDiags);

            var (multipart, allowAnonymous, authorizeScheme) = ReadDeclarationLevelAttributes(symbol, wellKnown, location, diagnostics);

            return new RequestModelInfo(
                fullTypeName,
                className,
                hintName,
                routeMethod!,
                routePath!,
                new EquatableArray<string>(ReadStaticHeaders(symbol, wellKnown).ToImmutableArray()),
                multipart,
                allowAnonymous,
                authorizeScheme,
                ImplementsApiRequest(symbol, wellKnown),
                new EquatableArray<RequestMemberInfo>(members.ToImmutable()),
                new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()),
                true);
        }

        private static RequestMemberInfo BuildMemberInfo(
            INamedTypeSymbol model,
            IPropertySymbol property,
            WellKnownSymbols wellKnown,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        {
            var ownAttributes = property.GetAttributes();
            var ignored = FindInOverrideChain(property, wellKnown.ApiIgnore) is not null;
            var ownBindingCount = CountOwnBindings(ownAttributes, wellKnown);

            var bindings = ImmutableArray.CreateBuilder<MemberBindingInfo>();
            if (!ignored || ownBindingCount > 0)
            {
                // [ApiIgnore] 只和「本属性自己声明的」参数特性冲突；继承来的参数特性被 [ApiIgnore] 静默压掉（与反射一致）。
                var inherit = !ignored;
                var binding = ReadBinding(property, wellKnown, inherit);
                if (binding is not null)
                {
                    bindings.Add(binding);
                }
            }

            if (ignored && ownBindingCount > 0)
            {
                var message =
                    $"属性 {model.Name}.{property.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                    + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。";

                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.IgnoreConflict,
                    new EquatableArray<string>(ImmutableArray.Create(message)),
                    GetLocation(property)));
            }

            var fieldName = FindInOverrideChain(property, wellKnown.JsonPropertyName) is { } jsonName
                && jsonName.ConstructorArguments.Length == 1
                && jsonName.ConstructorArguments[0].Value is string name
                    ? name
                    : property.Name;

            return new RequestMemberInfo(
                property.Name,
                fieldName,
                // 与反射的 DeclaringType.Name 一致：泛型类型带 arity（Base`1），非泛型就是简单名。
                $"{property.ContainingType.MetadataName}.{property.Name}",
                "typeof(" + TypeFormats.Display(property.Type) + ")",
                "static o => ((" + TypeFormats.Display(property.ContainingType) + ")o)." + EscapeIdentifier(property.Name),
                ignored,
                new EquatableArray<MemberBindingInfo>(bindings.ToImmutable()));
        }

        private static MemberBindingInfo? ReadBinding(IPropertySymbol property, WellKnownSymbols wellKnown, bool inherit)
        {
            var path = Find(property, wellKnown.Path, inherit);
            var query = Find(property, wellKnown.Query, inherit);
            var header = Find(property, wellKnown.Header, inherit);
            var headerCollection = Find(property, wellKnown.HeaderCollection, inherit);
            var body = Find(property, wellKnown.Body, inherit);
            var part = Find(property, wellKnown.Part, inherit);

            if (path is null && query is null && header is null && headerCollection is null && body is null && part is null)
            {
                return null;
            }

            string? partName = null;
            string? partFileName = null;
            string? partContentType = null;
            if (part is not null)
            {
                partName = GetStringArgument(part, 0);
                foreach (var named in part.NamedArguments)
                {
                    if (named.Key == "FileName")
                    {
                        partFileName = named.Value.Value as string;
                    }
                    else if (named.Key == "ContentType")
                    {
                        partContentType = named.Value.Value as string;
                    }
                }
            }

            return new MemberBindingInfo(
                path is not null,
                path is null ? null : GetStringArgument(path, 0),
                query is not null,
                query is null ? null : GetStringArgument(query, 0),
                header is not null,
                header is null ? null : GetStringArgument(header, 0),
                headerCollection is not null,
                body is not null,
                part is not null,
                partName,
                partFileName,
                partContentType);
        }

        private static (bool Multipart, bool AllowAnonymous, string? AuthorizeScheme) ReadDeclarationLevelAttributes(
            INamedTypeSymbol symbol,
            WellKnownSymbols wellKnown,
            Location modelLocation,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        {
            var multipart = false;
            var allowAnonymous = false;
            string? authorizeScheme = null;

            for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var attribute in current.GetAttributes())
                {
                    if (wellKnown.Multipart is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Multipart))
                    {
                        multipart = true;
                    }
                }
            }

            // 授权：最近一层声明优先（方法 → 类型链不适用请求模型，只按类型链）。
            for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                var attributes = current.GetAttributes();
                var anonymous = attributes.Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.AllowAnonymous));
                var authorize = attributes.FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Authorize));

                if (!anonymous && authorize is null)
                {
                    continue;
                }

                if (anonymous && authorize is not null)
                {
                    // 两条声明都写进构建器：共享构建器抛出的错误文本与反射路径完全一致。
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.AuthorizationConflict,
                        new EquatableArray<string>(ImmutableArray.Create("请求模型 " + symbol.Name)),
                        modelLocation));
                }

                allowAnonymous = anonymous;
                authorizeScheme = authorize is null ? null : GetStringArgument(authorize, 0);
                break;
            }

            return (multipart, allowAnonymous, authorizeScheme);
        }

        private static IEnumerable<string> ReadStaticHeaders(INamedTypeSymbol symbol, WellKnownSymbols wellKnown)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                chain.Add(current);
            }

            // 基类在前、派生类在后（同名后者覆盖由运行时按声明顺序应用）。
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                foreach (var attribute in chain[i].GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Headers)
                        || attribute.ConstructorArguments.Length == 0)
                    {
                        continue;
                    }

                    var argument = attribute.ConstructorArguments[0];
                    if (argument.Kind != TypedConstantKind.Array)
                    {
                        continue;
                    }

                    foreach (var element in argument.Values)
                    {
                        if (element.Value is string header)
                        {
                            yield return header;
                        }
                    }
                }
            }
        }

        private static bool ImplementsApiRequest(INamedTypeSymbol symbol, WellKnownSymbols wellKnown)
        {
            if (wellKnown.ApiRequest is null)
            {
                return false;
            }

            foreach (var contract in symbol.AllInterfaces)
            {
                if (contract.IsGenericType
                    && SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, wellKnown.ApiRequest))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? GetUnsupportedReason(INamedTypeSymbol symbol, Compilation compilation)
        {
            if (symbol.IsStatic)
            {
                return "静态类型不能作为请求模型";
            }

            if (symbol.IsRefLikeType)
            {
                return "ref struct 无法装箱成 object";
            }

            if (symbol.IsFileLocal())
            {
                return "file-local 类型不能被其它文件中的生成代码引用";
            }

            for (var current = symbol; current is not null; current = current.ContainingType)
            {
                if (current.IsGenericType)
                {
                    return current.Arity > 0
                        ? $"泛型类型 {current.Name} 无法生成（开放泛型没有可注册的运行时类型）"
                        : "嵌套在泛型类型里的模型无法生成";
                }
            }

            for (var current = symbol; current is not null; current = current.ContainingType)
            {
                if (!IsAccessible(current.DeclaredAccessibility, SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, compilation.Assembly)))
                {
                    return $"类型 {current.ToDisplayString()} 的访问级别（{current.DeclaredAccessibility}）对生成代码不可见";
                }
            }

            return null;
        }

        private static bool IsTypeAccessible(ITypeSymbol type, IAssemblySymbol currentAssembly)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return IsTypeAccessible(array.ElementType, currentAssembly);
                case IPointerTypeSymbol _:
                case IFunctionPointerTypeSymbol _:
                    return false;
                case ITypeParameterSymbol _:
                    return false;
                case INamedTypeSymbol named:
                    for (var current = named; current is not null; current = current.ContainingType)
                    {
                        // file-local 类型在别的文件里写不出名字（生成代码是独立文件），与访问级别无关。
                        if (current.IsFileLocal())
                        {
                            return false;
                        }

                        var sameAssembly = SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, currentAssembly);
                        if (!IsAccessible(current.DeclaredAccessibility, sameAssembly))
                        {
                            return false;
                        }
                    }

                    foreach (var argument in named.TypeArguments)
                    {
                        if (!IsTypeAccessible(argument, currentAssembly))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return true;
            }
        }

        private static bool IsAccessible(Accessibility accessibility, bool sameAssembly)
            => sameAssembly
                ? accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
                : accessibility is Accessibility.Public;

        private static IEnumerable<IPropertySymbol> EnumerateProperties(INamedTypeSymbol symbol)
        {
            // 与反射 GetProperties(Public | Instance) 一致：本类型声明的属性在前、基类在后；
            // override 与 new 隐藏都只保留最派生的一份——反射按「同名 + 同属性类型」判定隐藏，
            // 隐藏方不需要是 public（private / internal 的 new 同样会挡住基类可见性），
            // 但 static 与索引器不参与隐藏；new 成不同类型时基类属性仍然可见（两份都返回）。
            var seen = new List<(string Name, ITypeSymbol Type)>();
            for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member is not IPropertySymbol property
                        || property.IsStatic
                        || property.Parameters.Length != 0)
                    {
                        continue;
                    }

                    if (seen.Any(declared => declared.Name == property.Name
                        && SamePropertySignature(declared.Type, property.Type)))
                    {
                        continue;
                    }

                    seen.Add((property.Name, property.Type));

                    if (property.DeclaredAccessibility != Accessibility.Public)
                    {
                        continue;
                    }

                    yield return property;
                }
            }
        }

        /// <summary>反射的「同名同签名」判定：按 CLR 类型比较，<c>dynamic</c> == <c>object</c>，元组元素名不参与类型标识。</summary>
        private static bool SamePropertySignature(ITypeSymbol left, ITypeSymbol right)
            => SameClrType(left, right);

        /// <summary>CLR 类型等价：<c>(int a, int b)</c>、<c>(int x, int y)</c> 与显式写出的 <c>ValueTuple&lt;int, int&gt;</c> 是同一个运行时类型，嵌套元组同样展开。</summary>
        private static bool SameClrType(ITypeSymbol? left, ITypeSymbol? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (IsDynamicOrObject(left) || IsDynamicOrObject(right))
            {
                return IsDynamicOrObject(left) && IsDynamicOrObject(right);
            }

            if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
            {
                return leftArray.Rank == rightArray.Rank && SameClrType(leftArray.ElementType, rightArray.ElementType);
            }

            if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
            {
                // 元组语法与底层 ValueTuple 只差元素名，CLR 签名完全相同：先统一到 System.ValueTuple 再比较，
                // 否则「基类写元组、派生类写 ValueTuple」的 new 会被误判为不同类型而多绑一个成员。
                if (leftNamed.IsTupleType)
                {
                    var underlying = GetValueTuple(leftNamed);
                    if (underlying is null)
                    {
                        return false;
                    }

                    leftNamed = underlying;
                }

                if (rightNamed.IsTupleType)
                {
                    var underlying = GetValueTuple(rightNamed);
                    if (underlying is null)
                    {
                        return false;
                    }

                    rightNamed = underlying;
                }

                if (!SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition)
                    || leftNamed.TypeArguments.Length != rightNamed.TypeArguments.Length
                    || !SameClrType(leftNamed.ContainingType, rightNamed.ContainingType))
                {
                    return false;
                }

                for (var i = 0; i < leftNamed.TypeArguments.Length; i++)
                {
                    if (!SameClrType(leftNamed.TypeArguments[i], rightNamed.TypeArguments[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return SymbolEqualityComparer.Default.Equals(left, right);
        }

        private static bool IsDynamicOrObject(ITypeSymbol type)
            => type.TypeKind == TypeKind.Dynamic || type.SpecialType == SpecialType.System_Object;

        /// <summary>
        /// 把元组类型统一成 CLR 层的 <c>System.ValueTuple</c>。<c>TupleUnderlyingType</c> 在部分编译上下文里为 <c>null</c>
        /// （元组符号的 OriginalDefinition 仍是元组本身），此时按元素类型从元素所在程序集自行构造。
        /// </summary>
        private static INamedTypeSymbol? GetValueTuple(INamedTypeSymbol tuple)
            => tuple.TupleUnderlyingType ?? BuildValueTuple(tuple.ContainingAssembly, tuple.TupleElements, 0);

        /// <summary>构造 8 个元素以上的 ValueTuple：前 7 个 + 递归的 Rest。</summary>
        private static INamedTypeSymbol? BuildValueTuple(IAssemblySymbol? assembly, ImmutableArray<IFieldSymbol> elements, int offset)
        {
            if (assembly is null)
            {
                return null;
            }

            var remaining = elements.Length - offset;
            if (remaining <= 7)
            {
                var definition = assembly.GetTypeByMetadataName("System.ValueTuple`" + remaining);
                if (definition is null)
                {
                    return null;
                }

                var arguments = ImmutableArray.CreateBuilder<ITypeSymbol>(remaining);
                for (var i = offset; i < elements.Length; i++)
                {
                    arguments.Add(elements[i].Type);
                }

                return definition.Construct(arguments.ToArray());
            }

            var rest = BuildValueTuple(assembly, elements, offset + 7);
            var definition8 = assembly.GetTypeByMetadataName("System.ValueTuple`8");
            if (rest is null || definition8 is null)
            {
                return null;
            }

            var head = ImmutableArray.CreateBuilder<ITypeSymbol>(8);
            for (var i = offset; i < offset + 7; i++)
            {
                head.Add(elements[i].Type);
            }

            head.Add(rest);
            return definition8.Construct(head.ToArray());
        }

        private static AttributeData? Find(IPropertySymbol property, INamedTypeSymbol? attributeType, bool inherit)
        {
            if (attributeType is null)
            {
                return null;
            }

            if (inherit)
            {
                return FindInOverrideChain(property, attributeType);
            }

            foreach (var attribute in property.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                {
                    return attribute;
                }
            }

            return null;
        }

        private static AttributeData? FindInOverrideChain(IPropertySymbol property, INamedTypeSymbol? attributeType)
        {
            if (attributeType is null)
            {
                return null;
            }

            for (var current = property; current is not null; current = current.OverriddenProperty)
            {
                foreach (var attribute in current.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                    {
                        return attribute;
                    }
                }
            }

            return null;
        }

        private static int CountOwnBindings(ImmutableArray<AttributeData> attributes, WellKnownSymbols wellKnown)
        {
            var count = 0;
            foreach (var attribute in attributes)
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Path)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Query)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Header)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.HeaderCollection)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Part)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Body))
                {
                    count++;
                }
            }

            return count;
        }

        private static string? GetStringArgument(AttributeData attribute, int index)
            => attribute.ConstructorArguments.Length > index && attribute.ConstructorArguments[index].Value is string value
                ? value
                : null;

        private static bool DerivesFrom(INamedTypeSymbol? candidate, INamedTypeSymbol? baseType)
        {
            for (var current = candidate; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static Location GetLocation(IPropertySymbol property)
            => property.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None;

        private static EquatableArray<string> ToEquatable(string[] values) => new EquatableArray<string>(values.ToImmutableArray());

        private static EquatableArray<DiagnosticInfo> AddDiagnostic(ImmutableArray<DiagnosticInfo>.Builder diagnostics, DiagnosticInfo diagnostic)
        {
            diagnostics.Add(diagnostic);
            return new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable());
        }

        private static string BuildClassName(INamedTypeSymbol symbol)
        {
            var builder = new StringBuilder();
            foreach (var character in symbol.ToDisplayString())
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            var hash = Fnv1a(symbol.ToDisplayString());
            return "__AxiosRequestModel_" + builder + "_" + hash.ToString("x8");
        }

        private static uint Fnv1a(string value)
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }

        private static string EscapeIdentifier(string name)
            => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                ? "@" + name
                : name;
    }

    /// <summary>编译期解析到的 AxiosNet / STJ 类型符号；缺失时为 null（对应特性根本没出现）。</summary>
    internal sealed class WellKnownSymbols
    {
        private WellKnownSymbols(Compilation compilation)
        {
            HttpApi = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.HttpApiAttribute");
            Headers = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.HeadersAttribute");
            Multipart = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.MultipartAttribute");
            Authorize = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.AuthorizeAttribute");
            AllowAnonymous = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.AllowAnonymousAttribute");
            Path = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.PathAttribute");
            Query = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.QueryAttribute");
            Header = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.HeaderAttribute");
            HeaderCollection = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.HeaderCollectionAttribute");
            Body = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.BodyAttribute");
            Part = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.PartAttribute");
            ApiIgnore = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.ApiIgnoreAttribute");
            ApiRequest = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.IApiRequest`1");
            JsonPropertyName = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
            JsonIgnore = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
            AxiosShape = compilation.GetTypeByMetadataName("AxiosNet.AxiosShapeAttribute");
            Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            AxiosResponse = compilation.GetTypeByMetadataName("AxiosNet.AxiosResponse");
            AxiosResponseOfT = compilation.GetTypeByMetadataName("AxiosNet.AxiosResponse`1");
            AsyncEnumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
            CancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
            Stream = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.StreamAttribute");
            StreamFormat = compilation.GetTypeByMetadataName("AxiosNet.AxiosStreamFormat");
            ApiClient = compilation.GetTypeByMetadataName("AxiosNet.WebAPI.ApiClient");
            ApiInterfaceExtensions = compilation.GetTypeByMetadataName(
                "AxiosNet.WebAPI.DependencyInjection.ApiInterfaceServiceCollectionExtensions");
        }

        public INamedTypeSymbol? HttpApi { get; }

        public INamedTypeSymbol? Headers { get; }

        public INamedTypeSymbol? Multipart { get; }

        public INamedTypeSymbol? Authorize { get; }

        public INamedTypeSymbol? AllowAnonymous { get; }

        public INamedTypeSymbol? Path { get; }

        public INamedTypeSymbol? Query { get; }

        public INamedTypeSymbol? Header { get; }

        public INamedTypeSymbol? HeaderCollection { get; }

        public INamedTypeSymbol? Body { get; }

        public INamedTypeSymbol? Part { get; }

        public INamedTypeSymbol? ApiIgnore { get; }

        public INamedTypeSymbol? ApiRequest { get; }

        public INamedTypeSymbol? JsonPropertyName { get; }

        public INamedTypeSymbol? JsonIgnore { get; }

        public INamedTypeSymbol? AxiosShape { get; }

        public INamedTypeSymbol? Task { get; }

        public INamedTypeSymbol? TaskOfT { get; }

        public INamedTypeSymbol? AxiosResponse { get; }

        public INamedTypeSymbol? AxiosResponseOfT { get; }

        public INamedTypeSymbol? AsyncEnumerable { get; }

        public INamedTypeSymbol? CancellationToken { get; }

        public INamedTypeSymbol? Stream { get; }

        public INamedTypeSymbol? StreamFormat { get; }

        public INamedTypeSymbol? ApiClient { get; }

        public INamedTypeSymbol? ApiInterfaceExtensions { get; }

        /// <summary>
        /// 按编译缓存：每次 transform 都要查 25 个 well-known 类型，逐个查会随「带特性的类型数量」线性放大。
        /// 值随键（编译实例）一起回收，不会跨编译泄漏。
        /// </summary>
        private static readonly ConditionalWeakTable<Compilation, WellKnownSymbols> ResolvedCache = new();

        public static WellKnownSymbols Resolve(Compilation compilation)
            => ResolvedCache.GetValue(compilation, static current => new WellKnownSymbols(current));
    }

    internal static class BuiltInRouteMethods
    {
        private static readonly Dictionary<string, string> Methods = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AxiosNet.WebAPI.GetAttribute"] = "GET",
            ["AxiosNet.WebAPI.PostAttribute"] = "POST",
            ["AxiosNet.WebAPI.PutAttribute"] = "PUT",
            ["AxiosNet.WebAPI.PatchAttribute"] = "PATCH",
            ["AxiosNet.WebAPI.DeleteAttribute"] = "DELETE",
            ["AxiosNet.WebAPI.HeadAttribute"] = "HEAD",
            ["AxiosNet.WebAPI.OptionsAttribute"] = "OPTIONS"
        };

        public static bool TryGetValue(string attributeName, out string method) => Methods.TryGetValue(attributeName, out method!);
    }

    internal static class TypeFormats
    {
        /// <summary>全限定、global:: 前缀、关键字用特殊类型（int / string）；不含可空引用标注（typeof 不允许）。</summary>
        public static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

        public static string Display(ITypeSymbol type)
            => type.TypeKind == TypeKind.Dynamic ? "object" : type.ToDisplayString(FullyQualified);
    }
}
