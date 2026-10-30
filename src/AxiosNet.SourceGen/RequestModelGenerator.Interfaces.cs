using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// 接口代理的发现与声明烘焙：接口声明（非泛型）与调用点的闭合泛型实例（<c>Create&lt;IRepo&lt;User&gt;&gt;()</c> /
    /// <c>AddApiInterface&lt;...&gt;()</c> / <c>ValidateInterfaces(typeof(...))</c>）都会生成真实代理类与注册声明。
    /// </summary>
    public sealed partial class RequestModelGenerator
    {
        private void InitializeInterfaces(
            IncrementalGeneratorInitializationContext context,
            IncrementalValueProvider<GeneratorOptions> options)
        {
            var declarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => TransformInterface(ctx, ct))
                .SelectMany(static (models, _) => models);

            var callSites = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCreateCallSite(node),
                    transform: static (ctx, ct) => TransformCallSite(ctx, ct))
                .SelectMany(static (models, _) => models);

            context.RegisterSourceOutput(
                declarations.Collect().Combine(callSites.Collect()).Combine(options),
                static (output, pair) => ExecuteInterfaces(output, pair.Left.Left, pair.Left.Right, pair.Right));
        }

        private static void ExecuteInterfaces(
            SourceProductionContext context,
            ImmutableArray<InterfaceModelInfo> declarations,
            ImmutableArray<InterfaceModelInfo> callSites,
            GeneratorOptions options)
        {
            if (!options.Enabled)
            {
                // AXA012 由请求模型管线统一报告，避免同一编译出现重复诊断。
                return;
            }

            // 同一编译里已经有闭合调用点时，开放泛型声明的 AXA011 不再报告（代理由调用点生成）。
            var closedDefinitions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in callSites)
            {
                if (model.GenericDefinitionFullName is { } definition)
                {
                    closedDefinitions.Add(definition);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in declarations.Concat(callSites))
            {
                if (!seen.Add(model.FullTypeName))
                {
                    continue;
                }

                foreach (var diagnostic in model.Diagnostics)
                {
                    if (model.OpenGeneric
                        && diagnostic.Descriptor.Id == "AXA011"
                        && model.GenericDefinitionFullName is { } definition
                        && closedDefinitions.Contains(definition))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.MessageArgs.ToArray()));
                }

                if (!model.Emit)
                {
                    continue;
                }

                context.AddSource(model.HintName, InterfaceEmitter.Emit(model));
            }
        }

        private static ImmutableArray<InterfaceModelInfo> TransformInterface(
            GeneratorSyntaxContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var declaration = (InterfaceDeclarationSyntax)context.Node;
            var compilation = context.SemanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null)
            {
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            if (context.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            {
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            if (!HasRouteMethod(symbol, wellKnown))
            {
                // 没有路由特性的普通接口不生成任何东西。
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            return ImmutableArray.Create(BuildInterfaceInfo(
                symbol,
                declaration.Identifier.GetLocation(),
                wellKnown,
                compilation,
                fromCallSite: false));
        }

        private static bool IsCreateCallSite(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => null
            };

            return name is "Create" or "AddApiInterface" or "ValidateInterfaces";
        }

        private static ImmutableArray<InterfaceModelInfo> TransformCallSite(
            GeneratorSyntaxContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            var compilation = semanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null || wellKnown.ApiClient is null)
            {
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            // 只认 AxiosNet 自己的入口：ApiClient.Create / ValidateInterfaces 与 DI 的 AddApiInterface。
            var isApiClient = SymbolEqualityComparer.Default.Equals(method.ContainingType, wellKnown.ApiClient);
            var isInterfaceRegistration = wellKnown.ApiInterfaceExtensions is not null
                && SymbolEqualityComparer.Default.Equals(method.ContainingType, wellKnown.ApiInterfaceExtensions);

            if (!isApiClient && !isInterfaceRegistration)
            {
                return ImmutableArray<InterfaceModelInfo>.Empty;
            }

            var candidates = new List<ITypeSymbol>();
            if (method.TypeArguments.Length == 1)
            {
                candidates.Add(method.TypeArguments[0]);
            }
            else
            {
                // Create(typeof(T)) / ValidateInterfaces(typeof(A), typeof(B))
                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.Expression is TypeOfExpressionSyntax typeOf
                        && semanticModel.GetTypeInfo(typeOf.Type, cancellationToken).Type is { } type)
                    {
                        candidates.Add(type);
                    }
                }
            }

            var models = ImmutableArray.CreateBuilder<InterfaceModelInfo>();
            var location = invocation.GetLocation();
            foreach (var candidate in candidates)
            {
                if (candidate is not INamedTypeSymbol named || named.TypeKind != TypeKind.Interface)
                {
                    continue;
                }

                // 声明管线已覆盖本编译里的非泛型接口；调用点补齐「其它程序集里的接口」与闭合泛型实例。
                if (named.IsUnboundGenericType || HasTypeParameter(named))
                {
                    continue;
                }

                if (!HasRouteMethod(named, wellKnown))
                {
                    continue;
                }

                // 被引用程序集已经为同一接口生成过代理（且注册在它的模块初始化器里）：
                // 本编译再生成会在启动期触发重复注册，直接复用对方的注册。
                if (compilation.GetTypeByMetadataName("AxiosNet.Generated." + BuildInterfaceClassName(named)) is not null)
                {
                    continue;
                }

                models.Add(BuildInterfaceInfo(named, location, wellKnown, compilation, fromCallSite: true));
            }

            return models.ToImmutable();
        }

        private static InterfaceModelInfo BuildInterfaceInfo(
            INamedTypeSymbol symbol,
            Location location,
            WellKnownSymbols wellKnown,
            Compilation compilation,
            bool fromCallSite)
        {
            var fullTypeName = symbol.ToDisplayString(TypeFormats.FullyQualified);
            var className = BuildInterfaceClassName(symbol);
            var hintName = "AxiosNet.InterfaceProxy." + className + ".g.cs";
            var interfaceExpression = TypeFormats.Display(symbol);
            // 外层类型是开放泛型时，接口自身没有类型参数却仍含未替换的 T：同样只能由闭合调用点生成。
            var openGeneric = !fromCallSite && HasTypeParameter(symbol);
            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
            var blocked = false;

            InterfaceModelInfo Result(
                EquatableArray<string> staticHeaders,
                bool allowAnonymous,
                string? authorizeScheme,
                EquatableArray<InterfaceMethodInfo> methods)
                => new(
                    fullTypeName,
                    className,
                    hintName,
                    interfaceExpression,
                    staticHeaders,
                    allowAnonymous,
                    authorizeScheme,
                    openGeneric,
                    openGeneric ? fullTypeName : symbol.OriginalDefinition.ToDisplayString(TypeFormats.FullyQualified),
                    methods,
                    new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()),
                    !blocked);

            InterfaceModelInfo Rejected(DiagnosticDescriptor descriptor, params string[] args)
            {
                diagnostics.Add(new DiagnosticInfo(descriptor, ToEquatable(args), location));
                blocked = true;
                return Result(EquatableArray<string>.Empty, false, null, EquatableArray<InterfaceMethodInfo>.Empty);
            }

            var unsupported = GetInterfaceUnsupportedReason(symbol, compilation);
            if (unsupported is not null)
            {
                return Rejected(Diagnostics.InterfaceUnsupportedType, symbol.Name, unsupported);
            }

            if (openGeneric)
            {
                // 开放泛型没有可注册的运行时类型：由使用方的闭合调用点生成。
                return Rejected(Diagnostics.ClosedGenericInterfaceMissing, symbol.Name);
            }

            var chain = GetInterfaceChain(symbol);

            foreach (var current in chain)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member is IPropertySymbol or IEventSymbol)
                    {
                        return Rejected(Diagnostics.InterfaceMemberNotSupported, symbol.Name, member.Name);
                    }

                    if (member is IMethodSymbol { IsStatic: true, IsAbstract: true } staticMethod)
                    {
                        return Rejected(Diagnostics.InterfaceMemberNotSupported, symbol.Name, staticMethod.Name);
                    }

                    // 非 public 的抽象成员无法在生成程序集里显式实现（C# 也不允许跨程序集实现），
                    // 继续生成会得到 CS0535；提前报错而不是让生成代码编译失败。
                    if (member is IMethodSymbol { IsAbstract: true } nonPublicMethod
                        && nonPublicMethod.DeclaredAccessibility != Accessibility.Public)
                    {
                        return Rejected(
                            Diagnostics.InterfaceShapeUnsupported,
                            symbol.Name,
                            nonPublicMethod.Name,
                            "是非 public 的抽象成员：生成的代理无法在消费程序集实现");
                    }
                }
            }

            // 接口级静态头：基接口在前、派生接口在后（同名后者覆盖）。
            var staticHeaders = new List<string>();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                staticHeaders.AddRange(ReadHeaders(chain[i], wellKnown));
            }

            var interfaceAuth = ReadNearestInterfaceAuthorization(chain, wellKnown);
            if (interfaceAuth.Conflict)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.InterfaceAuthorizationConflictAtType,
                    ToEquatable([symbol.Name]),
                    location));
            }

            var methods = ImmutableArray.CreateBuilder<InterfaceMethodInfo>();
            var methodIndex = 0;

            foreach (var current in chain)
            {
                foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.IsStatic || method.DeclaredAccessibility != Accessibility.Public)
                    {
                        // 与反射路径一致：静态与私有接口方法不参与代理契约。
                        continue;
                    }

                    var methodDisplay = method.Name;
                    var methodLocation = method.Locations.FirstOrDefault(static l => l.IsInSource) ?? location;

                    var routes = method.GetAttributes()
                        .Where(attribute => DerivesFrom(attribute.AttributeClass, wellKnown.HttpApi))
                        .ToList();

                    if (routes.Count == 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceRouteInvalid,
                            ToEquatable([symbol.Name, methodDisplay, "缺少路由特性"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    if (routes.Count > 1)
                    {
                        var names = string.Join("、", routes.Select(static route => route.AttributeClass!.Name));
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceRouteInvalid,
                            ToEquatable([symbol.Name, methodDisplay, $"标注了多个路由特性（{names}）"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    if (!TryReadRoute(routes[0], wellKnown, out var routeMethod, out var routePath))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceCustomRouteAttribute,
                            ToEquatable([symbol.Name, methodDisplay, routes[0].AttributeClass!.Name]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(routeMethod) || string.IsNullOrWhiteSpace(routePath))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceRouteInvalid,
                            ToEquatable([symbol.Name, methodDisplay, "的路由特性参数为空白或无法在编译期求值"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    if (method.IsGenericMethod)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceShapeUnsupported,
                            ToEquatable([symbol.Name, methodDisplay, "泛型方法无法代理"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    if (!TryGetReturnShape(method, wellKnown, out var returnKind, out var bodyType, out var streaming, out var shapeError))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceReturnUnsupported,
                            ToEquatable([symbol.Name, methodDisplay, shapeError!]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    var streamAttribute = FindAttribute(method, wellKnown.Stream);
                    var streamFormat = 0;
                    if (streamAttribute is not null)
                    {
                        if (!streaming)
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                Diagnostics.InterfaceStreamInvalid,
                                ToEquatable([symbol.Name, methodDisplay, "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法"]),
                                methodLocation));
                            blocked = true;
                            continue;
                        }

                        if (streamAttribute.ConstructorArguments.Length == 1
                            && streamAttribute.ConstructorArguments[0].Value is int declaredFormat)
                        {
                            streamFormat = declaredFormat;
                        }

                        if (!IsDefinedStreamFormat(streamFormat, wellKnown))
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                Diagnostics.InterfaceStreamInvalid,
                                ToEquatable([symbol.Name, methodDisplay, $"帧格式值 {streamFormat} 无效"]),
                                methodLocation));
                            blocked = true;
                            continue;
                        }
                    }

                    if (bodyType is not null && !IsTypeAccessible(bodyType, compilation.Assembly))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceUnsupportedType,
                            ToEquatable([symbol.Name, $"返回类型 {bodyType.ToDisplayString()} 对生成代码不可见"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    var parameters = new List<InterfaceParameterInfo>();
                    var cancellationTokenIndex = -1;
                    var parameterBlocked = false;

                    foreach (var parameter in method.Parameters)
                    {
                        if (parameter.RefKind != RefKind.None)
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                Diagnostics.InterfaceShapeUnsupported,
                                ToEquatable([symbol.Name, methodDisplay, $"参数 {parameter.Name} 是 ref / out / in：接口级声明不支持按引用传递"]),
                                methodLocation));
                            parameterBlocked = true;
                            continue;
                        }

                        if (parameter.Type is INamedTypeSymbol { IsRefLikeType: true }
                            || parameter.Type.TypeKind == TypeKind.Pointer
                            || parameter.Type.TypeKind == TypeKind.FunctionPointer)
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                Diagnostics.InterfaceShapeUnsupported,
                                ToEquatable([symbol.Name, methodDisplay, $"参数 {parameter.Name} 的类型 {parameter.Type.ToDisplayString()} 无法装箱"]),
                                methodLocation));
                            parameterBlocked = true;
                            continue;
                        }

                        if (!IsTypeAccessible(parameter.Type, compilation.Assembly))
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                Diagnostics.InterfaceUnsupportedType,
                                ToEquatable([symbol.Name, $"参数类型 {parameter.Type.ToDisplayString()} 对生成代码不可见"]),
                                methodLocation));
                            parameterBlocked = true;
                            continue;
                        }

                        if (SymbolEqualityComparer.Default.Equals(parameter.Type, wellKnown.CancellationToken))
                        {
                            if (HasBindingAttribute(parameter, wellKnown))
                            {
                                diagnostics.Add(new DiagnosticInfo(
                                    Diagnostics.InterfaceShapeUnsupported,
                                    ToEquatable([symbol.Name, methodDisplay, $"参数 {parameter.Name} 是 CancellationToken，不能同时标注 Path / Query / Header / HeaderCollection / Part / Body"]),
                                    methodLocation));
                                parameterBlocked = true;
                                continue;
                            }

                            if (cancellationTokenIndex >= 0)
                            {
                                diagnostics.Add(new DiagnosticInfo(
                                    Diagnostics.InterfaceShapeUnsupported,
                                    ToEquatable([symbol.Name, methodDisplay, "声明了多个 CancellationToken 参数：最多一个"]),
                                    methodLocation));
                                parameterBlocked = true;
                                continue;
                            }

                            cancellationTokenIndex = parameter.Ordinal;
                            parameters.Add(new InterfaceParameterInfo(
                                parameter.Ordinal,
                                ParameterName(parameter),
                                TypeFormats.Display(parameter.Type),
                                $"{symbol.Name}.{method.Name}.{ParameterName(parameter)}",
                                false, false, null, false, null, false, null, false, false, false, null, null, null));
                            continue;
                        }

                        parameters.Add(ReadParameter(parameter, method, symbol, wellKnown));
                    }

                    if (parameterBlocked)
                    {
                        blocked = true;
                        continue;
                    }

                    var methodAuth = ReadMethodAuthorization(method, wellKnown);
                    if (methodAuth.Conflict)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceAuthorizationConflict,
                            ToEquatable([symbol.Name, methodDisplay]),
                            methodLocation));
                        blocked = true;
                    }

                    // 显式实现写在「声明该成员的接口」上：菱形继承里两个基接口的同名同签名成员
                    // 只有用各自接口名实现才不产生二义性。基接口对生成代码不可见时退回根接口。
                    var declaringExpression = IsTypeAccessible(current, compilation.Assembly)
                        ? TypeFormats.Display(current)
                        : interfaceExpression;

                    methods.Add(new InterfaceMethodInfo(
                        methodIndex,
                        method.Name,
                        declaringExpression,
                        routeMethod!,
                        routePath!,
                        new EquatableArray<string>(ReadHeaders(method, wellKnown).ToImmutableArray()),
                        FindAttribute(method, wellKnown.Multipart) is not null,
                        methodAuth.AllowAnonymous,
                        methodAuth.Scheme,
                        streaming,
                        streamFormat,
                        bodyType is null ? null : TypeFormats.Display(bodyType),
                        streaming && bodyType is INamedTypeSymbol streamBody ? TypeFormats.Display(streamBody.TypeArguments[0]) : null,
                        returnKind,
                        cancellationTokenIndex,
                        new EquatableArray<InterfaceParameterInfo>(parameters.ToImmutableArray())));

                    methodIndex++;
                }
            }

            return Result(
                new EquatableArray<string>(staticHeaders.ToImmutableArray()),
                interfaceAuth.AllowAnonymous,
                interfaceAuth.Scheme,
                new EquatableArray<InterfaceMethodInfo>(methods.ToImmutableArray()));
        }

        private static InterfaceParameterInfo ReadParameter(
            IParameterSymbol parameter,
            IMethodSymbol method,
            INamedTypeSymbol owner,
            WellKnownSymbols wellKnown)
        {
            var attributes = parameter.GetAttributes();
            var name = ParameterName(parameter);

            var path = FindAttribute(attributes, wellKnown.Path);
            var query = FindAttribute(attributes, wellKnown.Query);
            var header = FindAttribute(attributes, wellKnown.Header);
            var part = FindAttribute(attributes, wellKnown.Part);
            var ignore = FindAttribute(attributes, wellKnown.ApiIgnore) is not null;

            string? partFileName = null;
            string? partContentType = null;
            if (part is not null)
            {
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

            return new InterfaceParameterInfo(
                parameter.Ordinal,
                name,
                TypeFormats.Display(parameter.Type),
                $"{owner.Name}.{method.Name}.{name}",
                ignore,
                path is not null,
                path is null ? null : GetStringArgument(path, 0),
                query is not null,
                query is null ? null : GetStringArgument(query, 0),
                header is not null,
                header is null ? null : GetStringArgument(header, 0),
                FindAttribute(attributes, wellKnown.HeaderCollection) is not null,
                FindAttribute(attributes, wellKnown.Body) is not null,
                part is not null,
                part is null ? null : GetStringArgument(part, 0),
                partFileName,
                partContentType);
        }

        private static bool HasBindingAttribute(IParameterSymbol parameter, WellKnownSymbols wellKnown)
        {
            var attributes = parameter.GetAttributes();
            return FindAttribute(attributes, wellKnown.Path) is not null
                || FindAttribute(attributes, wellKnown.Query) is not null
                || FindAttribute(attributes, wellKnown.Header) is not null
                || FindAttribute(attributes, wellKnown.HeaderCollection) is not null
                || FindAttribute(attributes, wellKnown.Part) is not null
                || FindAttribute(attributes, wellKnown.Body) is not null;
        }

        private static string ParameterName(IParameterSymbol parameter)
            => string.IsNullOrEmpty(parameter.Name) ? "arg" + parameter.Ordinal : parameter.Name;

        private static bool HasRouteMethod(INamedTypeSymbol symbol, WellKnownSymbols wellKnown)
        {
            foreach (var current in GetInterfaceChain(symbol))
            {
                foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.IsStatic)
                    {
                        continue;
                    }

                    if (method.GetAttributes().Any(attribute => DerivesFrom(attribute.AttributeClass, wellKnown.HttpApi)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 接口自身在前的接口链（与运行时 <c>GetInterfaceChain</c> 一致）：其余按「依赖的接口数量」降序，
        /// 派生接口必然排在自己继承的接口之前，菱形的同名静态头 / 最近授权才能与反射路径选中同一层。
        /// </summary>
        private static List<INamedTypeSymbol> GetInterfaceChain(INamedTypeSymbol symbol)
        {
            var inherited = symbol.AllInterfaces;
            var chain = new List<INamedTypeSymbol>(inherited.Length + 1) { symbol };
            foreach (var baseInterface in inherited.OrderByDescending(static type => type.AllInterfaces.Length))
            {
                chain.Add(baseInterface);
            }

            return chain;
        }

        private static IEnumerable<string> ReadHeaders(INamedTypeSymbol type, WellKnownSymbols wellKnown)
        {
            var attribute = FindAttribute(type.GetAttributes(), wellKnown.Headers);
            if (attribute is null || attribute.ConstructorArguments.Length == 0)
            {
                yield break;
            }

            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array)
            {
                yield break;
            }

            foreach (var element in argument.Values)
            {
                if (element.Value is string header)
                {
                    yield return header;
                }
            }
        }

        private static IEnumerable<string> ReadHeaders(IMethodSymbol method, WellKnownSymbols wellKnown)
        {
            var attribute = FindAttribute(method.GetAttributes(), wellKnown.Headers);
            if (attribute is null || attribute.ConstructorArguments.Length == 0)
            {
                yield break;
            }

            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array)
            {
                yield break;
            }

            foreach (var element in argument.Values)
            {
                if (element.Value is string header)
                {
                    yield return header;
                }
            }
        }

        private static bool TryReadRoute(
            AttributeData route,
            WellKnownSymbols wellKnown,
            out string? method,
            out string? path)
        {
            var routeClass = route.AttributeClass!;
            if (BuiltInRouteMethods.TryGetValue(routeClass.ToDisplayString(), out var builtIn))
            {
                method = builtIn;
                path = GetStringArgument(route, 0);
                return true;
            }

            if (SymbolEqualityComparer.Default.Equals(routeClass, wellKnown.HttpApi))
            {
                method = GetStringArgument(route, 0);
                path = GetStringArgument(route, 1);
                return true;
            }

            // 自定义 HttpApiAttribute 子类：构造器只能在运行期执行，静态求不出路由。
            method = null;
            path = null;
            return false;
        }

        private static bool TryGetReturnShape(
            IMethodSymbol method,
            WellKnownSymbols wellKnown,
            out InterfaceReturnKind kind,
            out ITypeSymbol? bodyType,
            out bool streaming,
            out string? error)
        {
            kind = InterfaceReturnKind.Void;
            bodyType = null;
            streaming = false;
            error = null;

            var returnType = method.ReturnType;
            if (SymbolEqualityComparer.Default.Equals(returnType, wellKnown.Task))
            {
                kind = InterfaceReturnKind.Void;
                return true;
            }

            if (returnType is not INamedTypeSymbol { IsGenericType: true } generic
                || !SymbolEqualityComparer.Default.Equals(generic.OriginalDefinition, wellKnown.TaskOfT))
            {
                error = TypeFormats.Display(returnType);
                return false;
            }

            var inner = generic.TypeArguments[0];
            if (SymbolEqualityComparer.Default.Equals(inner, wellKnown.AxiosResponse))
            {
                kind = InterfaceReturnKind.Response;
                return true;
            }

            if (inner is INamedTypeSymbol { IsGenericType: true } response
                && SymbolEqualityComparer.Default.Equals(response.OriginalDefinition, wellKnown.AxiosResponseOfT))
            {
                kind = InterfaceReturnKind.Response;
                bodyType = response.TypeArguments[0];
                streaming = IsAsyncEnumerable(bodyType, wellKnown);
                return true;
            }

            kind = InterfaceReturnKind.Data;
            bodyType = inner;
            if (IsAsyncEnumerable(inner, wellKnown))
            {
                error = "流式正文按帧交付需要响应元数据，请声明 Task<AxiosResponse<IAsyncEnumerable<T>>>";
                return false;
            }

            return true;
        }

        private static bool IsAsyncEnumerable(ITypeSymbol type, WellKnownSymbols wellKnown)
            => type is INamedTypeSymbol { IsGenericType: true } named
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, wellKnown.AsyncEnumerable);

        private static bool IsDefinedStreamFormat(int value, WellKnownSymbols wellKnown)
        {
            if (wellKnown.StreamFormat is null)
            {
                return value == 0;
            }

            foreach (var member in wellKnown.StreamFormat.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && member.ConstantValue is int constant && constant == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static (bool AllowAnonymous, string? Scheme, bool Conflict) ReadNearestInterfaceAuthorization(
            List<INamedTypeSymbol> chain,
            WellKnownSymbols wellKnown)
        {
            foreach (var type in chain)
            {
                var (allowAnonymous, scheme, conflict) = ReadAuthorization(type.GetAttributes(), wellKnown);
                if (allowAnonymous || scheme is not null)
                {
                    return (allowAnonymous, scheme, conflict);
                }
            }

            return (false, null, false);
        }

        private static (bool AllowAnonymous, string? Scheme, bool Conflict) ReadMethodAuthorization(
            IMethodSymbol method,
            WellKnownSymbols wellKnown)
            => ReadAuthorization(method.GetAttributes(), wellKnown);

        private static (bool AllowAnonymous, string? Scheme, bool Conflict) ReadAuthorization(
            ImmutableArray<AttributeData> attributes,
            WellKnownSymbols wellKnown)
        {
            var anonymous = attributes.Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.AllowAnonymous));
            var authorize = attributes.FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wellKnown.Authorize));

            if (!anonymous && authorize is null)
            {
                return (false, null, false);
            }

            // AuthorizeAttribute 的 scheme 默认 "Bearer"：省略实参时 AttributeData 可能给不出值，这里补上默认值。
            var scheme = authorize is null ? null : GetStringArgument(authorize, 0) ?? "Bearer";
            return (anonymous, scheme, anonymous && authorize is not null);
        }

        private static AttributeData? FindAttribute(ImmutableArray<AttributeData> attributes, INamedTypeSymbol? attributeType)
        {
            if (attributeType is null)
            {
                return null;
            }

            foreach (var attribute in attributes)
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                {
                    return attribute;
                }
            }

            return null;
        }

        private static AttributeData? FindAttribute(IMethodSymbol method, INamedTypeSymbol? attributeType)
            => FindAttribute(method.GetAttributes(), attributeType);

        private static string BuildInterfaceClassName(INamedTypeSymbol symbol)
        {
            var display = symbol.ToDisplayString();
            var builder = new StringBuilder(display.Length);
            foreach (var character in display)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            var hash = Fnv1a(symbol.ToDisplayString(TypeFormats.FullyQualified));
            return "__AxiosProxy_" + builder + "_" + hash.ToString("x8");
        }

        /// <summary>接口（含闭合泛型的类型实参）是否仍含未替换的类型参数。</summary>
        private static bool HasTypeParameter(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol:
                    return true;
                case IArrayTypeSymbol array:
                    return HasTypeParameter(array.ElementType);
                case INamedTypeSymbol named:
                    if (named.ContainingType is not null && HasTypeParameter(named.ContainingType))
                    {
                        return true;
                    }

                    foreach (var argument in named.TypeArguments)
                    {
                        if (HasTypeParameter(argument))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }

        /// <summary>接口可生成性：file-local、外层泛型、访问级别（类型实参在闭合类型上逐项检查）。</summary>
        private static string? GetInterfaceUnsupportedReason(INamedTypeSymbol symbol, Compilation compilation)
        {
            if (symbol.IsFileLocal())
            {
                return "file-local 接口不能被其它文件中的生成代码引用";
            }

            for (var current = symbol; current is not null; current = current.ContainingType)
            {
                var sameAssembly = SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, compilation.Assembly);
                if (!IsAccessible(current.DeclaredAccessibility, sameAssembly))
                {
                    return $"接口 {current.ToDisplayString()} 的访问级别（{current.DeclaredAccessibility}）对生成代码不可见";
                }
            }

            // 开放泛型声明由 AXA011 单独提示；闭合类型逐项检查类型实参与外层类型的可见性。
            if (!HasTypeParameter(symbol) && !IsTypeAccessible(symbol, compilation.Assembly))
            {
                return $"接口 {symbol.ToDisplayString()} 的类型实参对生成代码不可见";
            }

            return null;
        }
    }
}
