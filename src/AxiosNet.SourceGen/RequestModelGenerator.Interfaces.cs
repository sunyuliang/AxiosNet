using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using AxiosNet.Internal.Spec;
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
        /// <summary>注册接口代理生成，并返回「声明 + 调用点」合并后的接口模型流（供校验入口复用）。</summary>
        private static IncrementalValueProvider<ImmutableArray<InterfaceModelInfo>> InitializeInterfaces(
            IncrementalGeneratorInitializationContext context,
            IncrementalValueProvider<GeneratorOptions> options)
        {
            var declarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => TransformInterface(ctx, ct))
                .Where(static batch => batch is not null)
                .Select(static (batch, _) => batch!)
                .SelectMany(static (batch, _) => batch.Models);

            var callSites = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCreateCallSite(node),
                    transform: static (ctx, ct) => TransformCallSite(ctx, ct))
                .SelectMany(static (batch, _) => batch.Models);

            var collectedDeclarations = declarations.Collect();
            var collectedCallSites = callSites.Collect();

            context.RegisterSourceOutput(
                collectedDeclarations.Combine(collectedCallSites).Combine(options),
                static (output, pair) => ExecuteInterfaces(output, pair.Left.Left, pair.Left.Right, pair.Right));

            return collectedDeclarations
                .Combine(collectedCallSites)
                .Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        }

        // 输出阶段：接口声明与调用点两路数据合流去重后逐个生成代理；开关关掉整段跳过。
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

        /// <summary>
        /// 一次接口声明展开出的模型（0 或 1 个）。返回包装而不是 <c>ImmutableArray</c>：
        /// <c>ImmutableArray</c> 按底层数组引用比较，当管线值会让每次编辑都被判成「已修改」，增量缓存直接失效。
        /// </summary>
        private static InterfaceBatchInfo? TransformInterface(
            GeneratorSyntaxContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var declaration = (InterfaceDeclarationSyntax)context.Node;
            var compilation = context.SemanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            if (!HasRouteMethod(symbol, wellKnown))
            {
                // 没有路由特性的普通接口不生成任何东西。
                return null;
            }

            return new InterfaceBatchInfo(ImmutableArray.Create(
                BuildInterfaceInfo(symbol, declaration.Identifier.GetLocation(), wellKnown, compilation, fromCallSite: false)));
        }

        // 语法层粗筛：只看 Create / AddApiInterface / ValidateInterfaces 三个入口名，真正判定在语义层。
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

        // 调用点 → 接口批次：只认 ApiClient 与 DI 扩展这两个宿主类型，别的同名方法一律放过。
        private static InterfaceBatchInfo TransformCallSite(
            GeneratorSyntaxContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            var compilation = semanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null || wellKnown.ApiClient is null)
            {
                return InterfaceBatchInfo.Empty;
            }

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                return InterfaceBatchInfo.Empty;
            }

            // 只认 AxiosNet 自己的入口：ApiClient.Create / ValidateInterfaces 与 DI 的 AddApiInterface。
            var isApiClient = SymbolEqualityComparer.Default.Equals(method.ContainingType, wellKnown.ApiClient);
            var isInterfaceRegistration = wellKnown.ApiInterfaceExtensions is not null
                && SymbolEqualityComparer.Default.Equals(method.ContainingType, wellKnown.ApiInterfaceExtensions);

            if (!isApiClient && !isInterfaceRegistration)
            {
                return InterfaceBatchInfo.Empty;
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

            return new InterfaceBatchInfo(models.ToImmutable());
        }

        // 接口符号 → 代理生成数据：全名、类名、静态头、方法表与诊断；调用点来的还要处理闭合泛型。
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

            // 诊断已经写进收集器的收场：产物为空壳，Emit 由 blocked 关掉。
            InterfaceModelInfo Blocked()
            {
                blocked = true;
                return Result(EquatableArray<string>.Empty, false, null, EquatableArray<InterfaceMethodInfo>.Empty);
            }

            InterfaceModelInfo Rejected(DiagnosticDescriptor descriptor, params string[] args)
            {
                diagnostics.Add(new DiagnosticInfo(descriptor, ToEquatable(args), location));
                return Blocked();
            }

            var unsupported = GetInterfaceUnsupportedReason(symbol, compilation);
            if (unsupported is not null)
            {
                return Rejected(Diagnostics.UnsupportedType, "接口 " + symbol.Name, unsupported);
            }

            if (openGeneric)
            {
                // 开放泛型没有可注册的运行时类型：由使用方的闭合调用点生成。
                return Rejected(Diagnostics.ClosedGenericInterfaceMissing, symbol.Name);
            }

            var chain = GetInterfaceChain(symbol);

            if (RejectUnsupportedMember(chain, symbol, location, diagnostics))
            {
                return Blocked();
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
                // 只记诊断、不置 blocked：契约照常产出。
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.AuthorizationConflict,
                    ToEquatable(["接口 " + symbol.Name]),
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

                    var (methodInfo, methodBlocked) = BuildMethodInfo(
                        method,
                        current,
                        symbol,
                        interfaceExpression,
                        location,
                        wellKnown,
                        compilation,
                        methodIndex,
                        diagnostics);

                    blocked |= methodBlocked;

                    if (methodInfo is null)
                    {
                        continue;
                    }

                    methods.Add(methodInfo);
                    methodIndex++;
                }
            }

            return Result(
                new EquatableArray<string>(staticHeaders.ToImmutableArray()),
                interfaceAuth.AllowAnonymous,
                interfaceAuth.Scheme,
                new EquatableArray<InterfaceMethodInfo>(methods.ToImmutableArray()));
        }

        /// <summary>接口成员形状预检：属性 / 事件、静态抽象方法、非 public 抽象方法都无法在生成程序集里实现。</summary>
        /// <param name="chain">接口自身在前的接口链。</param>
        /// <param name="symbol">被代理的接口。</param>
        /// <param name="location">诊断落点。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <returns>发现不可生成成员时返回 <c>true</c>，并已写入诊断。</returns>
        private static bool RejectUnsupportedMember(
            List<INamedTypeSymbol> chain,
            INamedTypeSymbol symbol,
            Location location,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        {
            foreach (var current in chain)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member is IPropertySymbol or IEventSymbol)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceMemberNotSupported,
                            ToEquatable([symbol.Name, member.Name]),
                            location));
                        return true;
                    }

                    if (member is IMethodSymbol { IsStatic: true, IsAbstract: true } staticMethod)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceMemberNotSupported,
                            ToEquatable([symbol.Name, staticMethod.Name]),
                            location));
                        return true;
                    }

                    // 非 public 的抽象成员无法在生成程序集里显式实现（C# 也不允许跨程序集实现），
                    // 继续生成会得到 CS0535；提前报错而不是让生成代码编译失败。
                    if (member is IMethodSymbol { IsAbstract: true } nonPublicMethod
                        && nonPublicMethod.DeclaredAccessibility != Accessibility.Public)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceShapeUnsupported,
                            ToEquatable([symbol.Name, nonPublicMethod.Name, "是非 public 的抽象成员：生成的代理无法在消费程序集实现"]),
                            location));
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>单个接口方法 → 代理契约项：路由 / 返回 / 编码 / 参数四组校验全过才产出。</summary>
        /// <param name="method">待烘焙的接口方法。</param>
        /// <param name="declaringType">声明该方法的接口，用于显式实现的接口名。</param>
        /// <param name="symbol">被代理的根接口。</param>
        /// <param name="interfaceExpression">根接口的生成代码可引用写法，基接口不可见时作为兜底。</param>
        /// <param name="interfaceLocation">方法没有源码位置时的诊断落点。</param>
        /// <param name="wellKnown">本编译解析出的框架符号。</param>
        /// <param name="compilation">当前编译，用于可见性判定。</param>
        /// <param name="methodIndex">方法在契约里的序号。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <returns>契约项（校验未全过时为 <c>null</c>）与该次扫描是否置位 <c>blocked</c>；授权冲突只置位、不丢弃契约项。</returns>
        private static (InterfaceMethodInfo? Method, bool Blocked) BuildMethodInfo(
            IMethodSymbol method,
            INamedTypeSymbol declaringType,
            INamedTypeSymbol symbol,
            string interfaceExpression,
            Location interfaceLocation,
            WellKnownSymbols wellKnown,
            Compilation compilation,
            int methodIndex,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        {
            var methodLocation = method.Locations.FirstOrDefault(static l => l.IsInSource) ?? interfaceLocation;
            var subject = "接口 " + symbol.Name + " 的 " + method.Name;

            if (!TryResolveRoute(method, subject, methodLocation, wellKnown, diagnostics, out var routeMethod, out var routePath))
            {
                return (null, true);
            }

            if (!TryResolveReturnShape(
                method, symbol, methodLocation, wellKnown, compilation, diagnostics,
                out var returnKind, out var bodyType, out var streaming, out var streamFormat))
            {
                return (null, true);
            }

            if (!TryResolveEncodingOptions(
                method, symbol, methodLocation, wellKnown, diagnostics,
                out var isMultipart, out var contentType, out var queryCollectionFormat))
            {
                return (null, true);
            }

            if (!TryResolveParameters(
                method, symbol, methodLocation, wellKnown, compilation, diagnostics,
                out var parameters, out var cancellationTokenIndex))
            {
                return (null, true);
            }

            var methodAuth = ReadMethodAuthorization(method, wellKnown);
            if (methodAuth.Conflict)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.AuthorizationConflict,
                    ToEquatable([subject]),
                    methodLocation));
            }

            // 显式实现写在「声明该成员的接口」上：菱形继承里两个基接口的同名同签名成员
            // 只有用各自接口名实现才不产生二义性。基接口对生成代码不可见时退回根接口。
            var declaringExpression = IsTypeAccessible(declaringType, compilation.Assembly)
                ? TypeFormats.Display(declaringType)
                : interfaceExpression;

            var info = new InterfaceMethodInfo(
                methodIndex,
                method.Name,
                declaringExpression,
                routeMethod!,
                routePath!,
                new EquatableArray<string>(ReadHeaders(method, wellKnown).ToImmutableArray()),
                isMultipart,
                contentType,
                queryCollectionFormat,
                methodAuth.AllowAnonymous,
                methodAuth.Scheme,
                streaming,
                streamFormat,
                bodyType is null ? null : TypeFormats.Display(bodyType),
                streaming && bodyType is INamedTypeSymbol streamBody ? TypeFormats.Display(streamBody.TypeArguments[0]) : null,
                returnKind,
                cancellationTokenIndex,
                new EquatableArray<InterfaceParameterInfo>(parameters.ToImmutableArray()));

            return (info, methodAuth.Conflict);
        }

        /// <summary>方法上的唯一路由特性 → 方法名与路径；缺失、多个、自定义子类、空值各报一条诊断。</summary>
        /// <param name="method">待解析的接口方法。</param>
        /// <param name="subject">诊断文案主语（<c>接口 X 的 M</c>）。</param>
        /// <param name="methodLocation">诊断落点。</param>
        /// <param name="wellKnown">本编译解析出的框架符号。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <param name="routeMethod">路由的 HTTP 方法名。</param>
        /// <param name="routePath">路由的路径模板。</param>
        /// <returns>解析成功返回 <c>true</c>；否则已写入诊断。</returns>
        private static bool TryResolveRoute(
            IMethodSymbol method,
            string subject,
            Location methodLocation,
            WellKnownSymbols wellKnown,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics,
            out string? routeMethod,
            out string? routePath)
        {
            routeMethod = null;
            routePath = null;

            var routes = method.GetAttributes()
                .Where(attribute => DerivesFrom(attribute.AttributeClass, wellKnown.HttpApi))
                .ToList();

            if (routes.Count == 0)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.RouteCountInvalid,
                    ToEquatable([subject, "缺少路由特性"]),
                    methodLocation));
                return false;
            }

            if (routes.Count > 1)
            {
                var names = string.Join("、", routes.Select(static route => route.AttributeClass!.Name));
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.RouteCountInvalid,
                    ToEquatable([subject, $"标注了多个路由特性（{names}）"]),
                    methodLocation));
                return false;
            }

            if (!TryReadRoute(routes[0], wellKnown, out routeMethod, out routePath))
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.CustomRouteAttribute,
                    ToEquatable([subject, routes[0].AttributeClass!.Name]),
                    methodLocation));
                return false;
            }

            if (string.IsNullOrWhiteSpace(routeMethod) || string.IsNullOrWhiteSpace(routePath))
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.RouteValueInvalid,
                    ToEquatable([subject]),
                    methodLocation));
                return false;
            }

            return true;
        }

        /// <summary>方法的返回形态：泛型方法、非 Task 家族返回值、流式帧格式与正文类型可见性都在这里判定。</summary>
        /// <param name="method">待解析的接口方法。</param>
        /// <param name="symbol">被代理的根接口。</param>
        /// <param name="methodLocation">诊断落点。</param>
        /// <param name="wellKnown">本编译解析出的框架符号。</param>
        /// <param name="compilation">当前编译，用于可见性判定。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <param name="returnKind">返回形态。</param>
        /// <param name="bodyType">正文类型；纯 <c>AxiosResponse</c> 与空返回为 <c>null</c>。</param>
        /// <param name="streaming">是否按帧交付的流式响应。</param>
        /// <param name="streamFormat">流式帧格式的枚举值；没有 <c>[Stream]</c> 时为 <c>0</c>。</param>
        /// <returns>可生成返回 <c>true</c>；否则已写入诊断。</returns>
        private static bool TryResolveReturnShape(
            IMethodSymbol method,
            INamedTypeSymbol symbol,
            Location methodLocation,
            WellKnownSymbols wellKnown,
            Compilation compilation,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics,
            out InterfaceReturnKind returnKind,
            out ITypeSymbol? bodyType,
            out bool streaming,
            out int streamFormat)
        {
            returnKind = InterfaceReturnKind.Void;
            bodyType = null;
            streaming = false;
            streamFormat = 0;

            var methodDisplay = method.Name;

            // 泛型方法的返回形态随类型实参而变，代理无法烘焙成具体契约。
            if (method.IsGenericMethod)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.InterfaceShapeUnsupported,
                    ToEquatable([symbol.Name, methodDisplay, "泛型方法无法代理"]),
                    methodLocation));
                return false;
            }

            if (!TryGetReturnShape(method, wellKnown, out returnKind, out bodyType, out streaming, out var shapeError))
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.InterfaceReturnUnsupported,
                    ToEquatable([symbol.Name, methodDisplay, shapeError!]),
                    methodLocation));
                return false;
            }

            var streamAttribute = FindAttribute(method, wellKnown.Stream);
            if (streamAttribute is not null)
            {
                if (!streaming)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.InterfaceStreamInvalid,
                        ToEquatable([symbol.Name, methodDisplay, "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法"]),
                        methodLocation));
                    return false;
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
                    return false;
                }
            }

            if (bodyType is not null && !IsTypeAccessible(bodyType, compilation.Assembly))
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnsupportedType,
                    ToEquatable(["接口 " + symbol.Name, $"返回类型 {bodyType.ToDisplayString()} 对生成代码不可见"]),
                    methodLocation));
                return false;
            }

            return true;
        }

        /// <summary>方法级编码选项：multipart 开关、<c>[BodyContentType]</c> 媒体类型与 <c>[QueryCollectionFormat]</c> 取值。</summary>
        /// <param name="method">待解析的接口方法。</param>
        /// <param name="symbol">被代理的根接口。</param>
        /// <param name="methodLocation">诊断落点。</param>
        /// <param name="wellKnown">本编译解析出的框架符号。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <param name="isMultipart">是否标了 <c>[Multipart]</c>。</param>
        /// <param name="contentType"><c>[BodyContentType]</c> 的媒体类型；没标为 <c>null</c>。</param>
        /// <param name="queryCollectionFormat"><c>[QueryCollectionFormat]</c> 的枚举值；没标为 <c>null</c>。</param>
        /// <returns>取值合法返回 <c>true</c>；否则已写入诊断。</returns>
        private static bool TryResolveEncodingOptions(
            IMethodSymbol method,
            INamedTypeSymbol symbol,
            Location methodLocation,
            WellKnownSymbols wellKnown,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics,
            out bool isMultipart,
            out string? contentType,
            out int? queryCollectionFormat)
        {
            var methodDisplay = method.Name;
            isMultipart = FindAttribute(method, wellKnown.Multipart) is not null;
            contentType = null;
            queryCollectionFormat = null;

            var contentTypeAttribute = FindAttribute(method, wellKnown.BodyContentType);
            if (contentTypeAttribute is not null)
            {
                contentType = GetStringArgument(contentTypeAttribute, 0)?.Trim();
                if (contentType is null || contentType.Length == 0 || HasHeaderControlCharacter(contentType))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.InterfaceEncodingInvalid,
                        ToEquatable([symbol.Name, methodDisplay, "[BodyContentType] 的媒体类型不能为空白或包含控制字符"]),
                        methodLocation));
                    return false;
                }

                if (isMultipart)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.InterfaceEncodingInvalid,
                        ToEquatable([symbol.Name, methodDisplay, "[Multipart] 与 [BodyContentType] 互斥：multipart 的媒体类型固定为 multipart/form-data"]),
                        methodLocation));
                    return false;
                }
            }

            var collectionFormatAttribute = FindAttribute(method, wellKnown.QueryCollectionFormat);
            if (collectionFormatAttribute is not null)
            {
                var value = collectionFormatAttribute.ConstructorArguments.Length == 1
                    && collectionFormatAttribute.ConstructorArguments[0].Value is int declaredFormat
                    ? declaredFormat
                    : -1;

                if (!IsDefinedCollectionFormat(value, wellKnown))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.InterfaceEncodingInvalid,
                        ToEquatable([symbol.Name, methodDisplay, $"查询集合格式值 {value} 无效"]),
                        methodLocation));
                    return false;
                }

                queryCollectionFormat = value;
            }

            return true;
        }

        /// <summary>方法参数表：按引用、无法装箱、对生成代码不可见、<c>CancellationToken</c> 位置都在这里判定。</summary>
        /// <param name="method">待解析的接口方法。</param>
        /// <param name="symbol">被代理的根接口。</param>
        /// <param name="methodLocation">诊断落点。</param>
        /// <param name="wellKnown">本编译解析出的框架符号。</param>
        /// <param name="compilation">当前编译，用于可见性判定。</param>
        /// <param name="diagnostics">诊断收集器。</param>
        /// <param name="parameters">按声明顺序的绑定声明；失败时内容不完整，调用方应丢弃。</param>
        /// <param name="cancellationTokenIndex"><c>CancellationToken</c> 参数的序号；没有为 <c>-1</c>。</param>
        /// <returns>全部参数可用返回 <c>true</c>；否则已写入诊断。</returns>
        private static bool TryResolveParameters(
            IMethodSymbol method,
            INamedTypeSymbol symbol,
            Location methodLocation,
            WellKnownSymbols wellKnown,
            Compilation compilation,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics,
            out List<InterfaceParameterInfo> parameters,
            out int cancellationTokenIndex)
        {
            var methodDisplay = method.Name;
            parameters = new List<InterfaceParameterInfo>();
            cancellationTokenIndex = -1;
            var blocked = false;

            foreach (var parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.InterfaceShapeUnsupported,
                        ToEquatable([symbol.Name, methodDisplay, $"参数 {parameter.Name} 是 ref / out / in：接口级声明不支持按引用传递"]),
                        methodLocation));
                    blocked = true;
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
                    blocked = true;
                    continue;
                }

                if (!IsTypeAccessible(parameter.Type, compilation.Assembly))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.UnsupportedType,
                        ToEquatable(["接口 " + symbol.Name, $"参数类型 {parameter.Type.ToDisplayString()} 对生成代码不可见"]),
                        methodLocation));
                    blocked = true;
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
                        blocked = true;
                        continue;
                    }

                    if (cancellationTokenIndex >= 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            Diagnostics.InterfaceShapeUnsupported,
                            ToEquatable([symbol.Name, methodDisplay, "声明了多个 CancellationToken 参数：最多一个"]),
                            methodLocation));
                        blocked = true;
                        continue;
                    }

                    cancellationTokenIndex = parameter.Ordinal;
                    parameters.Add(new InterfaceParameterInfo(
                        parameter.Ordinal,
                        ParameterName(parameter),
                        TypeFormats.Display(parameter.Type),
                        $"{symbol.Name}.{method.Name}.{ParameterName(parameter)}",
                        null,
                        false, false, null, false, null, false, null, false, false, null, false, null, null, null));
                    continue;
                }

                parameters.Add(ReadParameter(parameter, method, symbol, wellKnown, diagnostics));
            }

            return !blocked;
        }

        // 单个方法参数 → 绑定声明：特性决定它走路径 / 查询 / 头 / 正文，未标注的按名字兜底成查询参数。
        private static InterfaceParameterInfo ReadParameter(
            IParameterSymbol parameter,
            IMethodSymbol method,
            INamedTypeSymbol owner,
            WellKnownSymbols wellKnown,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        {
            var attributes = parameter.GetAttributes();
            var name = ParameterName(parameter);

            var path = FindAttribute(attributes, wellKnown.Path);
            var query = FindAttribute(attributes, wellKnown.Query);
            var header = FindAttribute(attributes, wellKnown.Header);
            var part = FindAttribute(attributes, wellKnown.Part);
            var body = FindAttribute(attributes, wellKnown.Body);
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

            var formatExpression = BuildFormatExpression(
                FindAttribute(attributes, wellKnown.Format),
                $"参数 {owner.Name}.{method.Name}.{name}",
                parameter.Locations.FirstOrDefault(static location => location.IsInSource)
                    ?? method.Locations.FirstOrDefault()
                    ?? Location.None,
                diagnostics.Add);

            return new InterfaceParameterInfo(
                parameter.Ordinal,
                name,
                TypeFormats.Display(parameter.Type),
                $"{owner.Name}.{method.Name}.{name}",
                formatExpression,
                ignore,
                path is not null,
                path is null ? null : GetStringArgument(path, 0),
                query is not null,
                query is null ? null : GetStringArgument(query, 0),
                header is not null,
                header is null ? null : GetStringArgument(header, 0),
                FindAttribute(attributes, wellKnown.HeaderCollection) is not null,
                body is not null,
                body is null ? null : GetStringArgument(body, 0),
                part is not null,
                part is null ? null : GetStringArgument(part, 0),
                partFileName,
                partContentType);
        }

        // 参数上是否标了任一绑定特性：标了就不再按名字推断。
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

        // 取参数名；匿名位置参数没有名字时用 arg + 序号，保证生成代码合法。
        private static string ParameterName(IParameterSymbol parameter)
            => string.IsNullOrEmpty(parameter.Name) ? "arg" + parameter.Ordinal : parameter.Name;

        // 整条接口链上是否存在带路由特性的方法：没有就没什么可生成。
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
        /// 接口自身在前的接口链（与运行时 <c>CollectInterfaceChain</c> 一致）：其余按「依赖的接口数量」降序，
        /// 同层再按元数据名排序（反射侧同规则）——同层顺序因此不再依赖各自的原始遍历顺序，
        /// 菱形的同名静态头 / 最近授权才能在两条路径上选中同一层。
        /// </summary>
        private static List<INamedTypeSymbol> GetInterfaceChain(INamedTypeSymbol symbol)
        {
            var inherited = symbol.AllInterfaces;
            var chain = new List<INamedTypeSymbol>(inherited.Length + 1) { symbol };
            foreach (var baseInterface in inherited
                .OrderByDescending(static type => type.AllInterfaces.Length)
                .ThenBy(static type => GetChainSortKey(type), StringComparer.Ordinal))
            {
                chain.Add(baseInterface);
            }

            return chain;
        }

        /// <summary>
        /// 接口链同层排序键：定义元数据名 + 各类型实参的元数据名。
        /// 拼装规则与反射期共用 <see cref="InterfaceChainOrdering.SortKey"/>，两侧不再各自维护。
        /// </summary>
        private static string GetChainSortKey(INamedTypeSymbol type)
            => InterfaceChainOrdering.SortKey(GetMetadataName(type.OriginalDefinition), GetChainArgumentNames(type));

        // 泛型实参的元数据名列表；非泛型返回空集合（排序键随之退化为定义名）。
        private static string[] GetChainArgumentNames(INamedTypeSymbol type)
        {
            if (type.TypeArguments.Length == 0)
            {
                return Array.Empty<string>();
            }

            var names = new string[type.TypeArguments.Length];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = GetChainArgumentName(type.TypeArguments[i]);
            }

            return names;
        }

        // 拼泛型实参的链式名字：数组加 []、泛型递归展开，用于区分同一接口的不同闭合形态。
        private static string GetChainArgumentName(ITypeSymbol type)
            => type switch
            {
                IArrayTypeSymbol array => GetChainArgumentName(array.ElementType) + "[]",
                INamedTypeSymbol named => GetMetadataName(named.OriginalDefinition),
                _ => type.Name
            };

        // 读接口上的 Headers 静态头（构造参数是字符串数组，非数组形态一律忽略）。
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

        // 读方法上的 Headers 静态头：只作用于这一个方法，不继承到整个接口。
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

        // 从路由特性里取出方法名与路径；不是路由特性或参数形态不认识时返回 false。
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

        // 解析方法返回类型：Task / Task<T> / AxiosResponse / IAsyncEnumerable 各对应一种返回形态与是否流式。
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

        // 是否 IAsyncEnumerable<T>（按原始定义比较，不看实参）。
        private static bool IsAsyncEnumerable(ITypeSymbol type, WellKnownSymbols wellKnown)
            => type is INamedTypeSymbol { IsGenericType: true } named
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, wellKnown.AsyncEnumerable);

        // 枚举值是否落在 AxiosStreamFormat 的定义范围内：越界值要报诊断，不能照抄进生成代码。
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

        // 枚举值是否落在 AxiosCollectionFormat 的定义范围内。
        private static bool IsDefinedCollectionFormat(int value, WellKnownSymbols wellKnown)
        {
            if (wellKnown.CollectionFormat is null)
            {
                return value == 0;
            }

            foreach (var member in wellKnown.CollectionFormat.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && member.ConstantValue is int constant && constant == value)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>与运行时头取值校验同口径：拒绝 C0 控制字符（HTAB 除外）与 DEL。</summary>
        private static bool HasHeaderControlCharacter(string value)
        {
            foreach (var character in value)
            {
                if (character == '\t')
                {
                    continue;
                }

                if (character < 0x20 || character == 0x7F)
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

            // 防御性默认值：AuthorizeAttribute 的 scheme 构造参数本身有默认值 "Bearer"，这里只防 AttributeData 缺项，口径与模型侧一致。
            var scheme = authorize is null ? null : GetStringArgument(authorize, 0) ?? "Bearer";
            return (anonymous, scheme, anonymous && authorize is not null);
        }

        // 按符号相等在特性列表里找目标特性；特性类型符号缺失时直接返回 null。
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

        // 方法版重载：只是取一次 GetAttributes 再交给上面那个。
        private static AttributeData? FindAttribute(IMethodSymbol method, INamedTypeSymbol? attributeType)
            => FindAttribute(method.GetAttributes(), attributeType);

        // 接口全名 → 代理类名：把命名空间分隔符与泛型符号都换成下划线，前缀截到固定长度，尾部拼 64 位哈希区分同前缀类型。
        private static string BuildInterfaceClassName(INamedTypeSymbol symbol)
        {
            var display = symbol.ToDisplayString();
            var builder = new StringBuilder(display.Length);
            foreach (var character in display)
            {
                if (builder.Length >= MaxClassNamePrefix)
                {
                    break;
                }

                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            var hash = Fnv1a(symbol.ToDisplayString(TypeFormats.FullyQualified));
            return "__AxiosProxy_" + builder + "_" + hash.ToString("x16");
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
