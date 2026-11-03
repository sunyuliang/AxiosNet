using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// 对象形状发现：从请求模型、接口参数、Axios 调用点、<c>[AxiosShape]</c> 与匿名对象创建出发，
    /// 递归展开成「类型 → 公共可读属性清单」，并烘焙成 <c>AxiosMetadata.RegisterGeneratedShapeFromSample</c> 调用。
    /// 形状只描述事实（成员名 / 字段名 / 忽略标记 / 取值委托），展平语义仍由运行时决定。
    /// </summary>
    public sealed partial class RequestModelGenerator
    {
        private const string ShapeNamespace = "AxiosNet.Generated";

        // 被视为「调用点」的容器方法名：出现在形状构建器里说明是声明式代码，跳过。
        private static readonly HashSet<string> AxiosCallNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetAsync",
            "PostAsync",
            "PutAsync",
            "PatchAsync",
            "DeleteAsync",
            "HeadAsync",
            "OptionsAsync",
            "PostFormAsync",
            "PutFormAsync",
            "PatchFormAsync",
            "RequestAsync",
            "RequestStreamAsync",
            "GetUri"
        };

        // 承载形状声明的容器类型名（同样用于跳过声明式代码）。
        private static readonly HashSet<string> AxiosContainerNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "AxiosNet.Axios",
            "AxiosNet.AxiosClient",
            "AxiosNet.WebAPI.ApiClient"
        };

        // 注册形状管道：语法种子 → 形状批次 → 一次性执行（与请求模型那条管道并行，互不依赖）。
        private static void InitializeShapes(
            IncrementalGeneratorInitializationContext context,
            IncrementalValueProvider<GeneratorOptions> options)
        {
            var batches = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsShapeSeedCandidate(node),
                    transform: static (ctx, ct) => TransformShapeSeeds(ctx, ct))
                .Where(static batch => batch is not null)
                .Select(static (batch, _) => batch!);

            context.RegisterSourceOutput(
                batches.Collect().Combine(options),
                static (output, pair) => ExecuteShapes(output, pair.Left, pair.Right));
        }

        // 语法层粗筛：只放行匿名对象、类型声明、接口方法、Axios 容器调用与配置赋值/初始化器。
        private static bool IsShapeSeedCandidate(SyntaxNode node)
        {
            switch (node)
            {
                case AnonymousObjectCreationExpressionSyntax _:
                    return true;

                case TypeDeclarationSyntax declaration:
                    return declaration.AttributeLists.Count > 0;

                case MethodDeclarationSyntax method:
                    return method.AttributeLists.Count > 0 && method.Parent is InterfaceDeclarationSyntax;

                case InvocationExpressionSyntax invocation:
                    return invocation.Expression is MemberAccessExpressionSyntax access
                        && AxiosCallNames.Contains(access.Name.Identifier.ValueText);

                case AssignmentExpressionSyntax assignment:
                    return assignment.Left is MemberAccessExpressionSyntax member
                        && IsConfigSlot(member.Name.Identifier.ValueText);

                case InitializerExpressionSyntax initializer:
                    foreach (var expression in initializer.Expressions)
                    {
                        if (expression is AssignmentExpressionSyntax
                            {
                                Left: IdentifierNameSyntax name
                            }
                            && IsConfigSlot(name.Identifier.ValueText))
                        {
                            return true;
                        }
                    }

                    return false;
            }

            return false;
        }

        // 只有 Params / Data 两个槽位承载会被序列化的值，其它配置项不产形状。
        private static bool IsConfigSlot(string name) => name == "Params" || name == "Data";

        // 把一个语法种子的收集结果折成批次；没引用 AxiosNet 时直接返回（不可能有调用点）。
        private static ShapeBatchInfo? TransformShapeSeeds(GeneratorSyntaxContext context, CancellationToken cancellationToken)
        {
            var compilation = context.SemanticModel.Compilation;
            var wellKnown = WellKnownSymbols.Resolve(compilation);
            if (wellKnown.HttpApi is null)
            {
                // 没引用 AxiosNet：不可能是 Axios 调用点 / 形状种子。
                return null;
            }

            var collection = new ShapeCollection(context.SemanticModel, compilation, wellKnown, cancellationToken);
            switch (context.Node)
            {
                case AnonymousObjectCreationExpressionSyntax anonymous:
                    collection.AddAnonymous(anonymous);
                    break;

                case TypeDeclarationSyntax declaration:
                    collection.AddTypeDeclaration(declaration);
                    break;

                case MethodDeclarationSyntax method:
                    collection.AddInterfaceMethod(method);
                    break;

                case InvocationExpressionSyntax invocation:
                    collection.AddInvocation(invocation);
                    break;

                case AssignmentExpressionSyntax assignment:
                    collection.AddConfigAssignment(assignment);
                    break;

                case InitializerExpressionSyntax initializer:
                    collection.AddConfigInitializer(initializer);
                    break;
            }

            return collection.Shapes.Count == 0 && collection.DiagnosticItems.Count == 0
                ? null
                : new ShapeBatchInfo(
                    new EquatableArray<ShapeInfo>(collection.Shapes.ToImmutableArray()),
                    new EquatableArray<DiagnosticInfo>(collection.DiagnosticItems.ToImmutableArray()));
        }

        // 形状开关关掉就整段不跑；否则按 key 去重后每个形状输出一个生成文件。
        private static void ExecuteShapes(
            SourceProductionContext context,
            ImmutableArray<ShapeBatchInfo> batches,
            GeneratorOptions options)
        {
            if (!options.Enabled)
            {
                return;
            }

            var seenShapes = new HashSet<string>(StringComparer.Ordinal);
            var seenDiagnostics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in batches)
            {
                foreach (var diagnostic in batch.Diagnostics)
                {
                    var key = diagnostic.Descriptor.Id + "|" + string.Join("|", diagnostic.MessageArgs);
                    if (seenDiagnostics.Add(key))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            diagnostic.Descriptor,
                            diagnostic.Location,
                            diagnostic.MessageArgs.ToArray()));
                    }
                }

                foreach (var shape in batch.Shapes)
                {
                    if (seenShapes.Add(shape.Key))
                    {
                        context.AddSource(shape.HintName, ShapeEmitter.Emit(shape));
                    }
                }
            }
        }

        /// <summary>一次语法候选的形状收集上下文（可变、仅在一次 Transform 内使用）。</summary>
        private sealed class ShapeCollection
        {
            private readonly SemanticModel _model;
            private readonly Compilation _compilation;
            private readonly WellKnownSymbols _wellKnown;
            private readonly CancellationToken _cancellationToken;
            private readonly HashSet<string> _seenTypes = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenDiagnostics = new HashSet<string>(StringComparer.Ordinal);

            public ShapeCollection(
                SemanticModel model,
                Compilation compilation,
                WellKnownSymbols wellKnown,
                CancellationToken cancellationToken)
            {
                _model = model;
                _compilation = compilation;
                _wellKnown = wellKnown;
                _cancellationToken = cancellationToken;
            }

            public List<ShapeInfo> Shapes { get; } = new List<ShapeInfo>();

            public List<DiagnosticInfo> DiagnosticItems { get; } = new List<DiagnosticInfo>();

            // 匿名对象字面量：确认它真是匿名类型再登记，是否报 AXA013 取决于它是否处在 Axios 上下文。
            public void AddAnonymous(AnonymousObjectCreationExpressionSyntax syntax)
            {
                if (_model.GetTypeInfo(syntax, _cancellationToken).Type is not INamedTypeSymbol { IsAnonymousType: true } anonymous)
                {
                    return;
                }

                AddAnonymousShape(anonymous, syntax.GetLocation(), IsAxiosContext(syntax));
            }

            /// <summary>匿名对象是否直接出现在 Axios 调用 / <c>Params</c> / <c>Data</c> 上下文（用于决定是否报 AXA013）。</summary>
            private bool IsAxiosContext(SyntaxNode node)
            {
                for (var current = node.Parent; current is not null; current = current.Parent)
                {
                    switch (current)
                    {
                        case InvocationExpressionSyntax invocation
                            when _model.GetSymbolInfo(invocation, _cancellationToken).Symbol is IMethodSymbol method
                                && method.ContainingType is not null
                                && AxiosContainerNames.Contains(method.ContainingType.ToDisplayString()):
                            return true;

                        case AssignmentExpressionSyntax assignment
                            when assignment.Left is MemberAccessExpressionSyntax member
                                && IsConfigSlot(member.Name.Identifier.ValueText)
                                && IsAxiosConfig(member.Expression):
                            return true;

                        case InitializerExpressionSyntax initializer:
                            // 只认 AxiosConfig 的对象初始化器：别的类型上同名 Params / Data 不是 Axios 上下文。
                            if (IsObjectCreation(initializer.Parent, out var creation)
                                && IsAxiosConfig(creation))
                            {
                                foreach (var expression in initializer.Expressions)
                                {
                                    if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax name }
                                        && IsConfigSlot(name.Identifier.ValueText))
                                    {
                                        return true;
                                    }
                                }
                            }

                            break;

                        case StatementSyntax _:
                        case MemberDeclarationSyntax _:
                            return false;
                    }
                }

                return false;
            }

            // 按显示名判定 AxiosConfig：配置赋值与初始化器只关心这一个类型，不做符号相等。
            private bool IsAxiosConfig(ExpressionSyntax expression)
                => _model.GetTypeInfo(expression, _cancellationToken).Type?.ToDisplayString() == "AxiosNet.AxiosConfig";

            /// <summary><c>new C { ... }</c> 与目标类型推断的 <c>new() { ... }</c> 都算对象创建。</summary>
            private static bool IsObjectCreation(SyntaxNode? node, out ExpressionSyntax creation)
            {
                if (node is ObjectCreationExpressionSyntax explicitCreation)
                {
                    creation = explicitCreation;
                    return true;
                }

                if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
                {
                    creation = implicitCreation;
                    return true;
                }

                creation = null!;
                return false;
            }

            // 类型声明只有两种进法：显式标了 [AxiosShape]，或自身就是带路由特性的请求模型；不可见的请求模型由模型管线报 AXA009，这里不重复报。
            public void AddTypeDeclaration(TypeDeclarationSyntax declaration)
            {
                if (_model.GetDeclaredSymbol(declaration, _cancellationToken) is not INamedTypeSymbol symbol)
                {
                    return;
                }

                var hasShapeAttribute = false;
                var isRequestModel = false;
                foreach (var attribute in symbol.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _wellKnown.AxiosShape))
                    {
                        hasShapeAttribute = true;
                    }
                    else if (DerivesFrom(attribute.AttributeClass, _wellKnown.HttpApi))
                    {
                        isRequestModel = true;
                    }
                }

                if (hasShapeAttribute || isRequestModel)
                {
                    // 不可见的请求模型已由模型管线报 AXA009（错误）；这里不再重复报警告。
                    if (isRequestModel && !IsTypeAccessible(symbol, _compilation.Assembly))
                    {
                        return;
                    }

                    AddSeed(symbol, declaration.Identifier.GetLocation());
                }
            }

            // 接口方法带路由特性时，把每个参数的类型登记成种子：声明式接口的入参同样要能烘焙形状。
            public void AddInterfaceMethod(MethodDeclarationSyntax declaration)
            {
                if (_model.GetDeclaredSymbol(declaration, _cancellationToken) is not IMethodSymbol method
                    || method.ContainingType is not { TypeKind: TypeKind.Interface })
                {
                    return;
                }

                var hasRoute = false;
                foreach (var attribute in method.GetAttributes())
                {
                    if (DerivesFrom(attribute.AttributeClass, _wellKnown.HttpApi))
                    {
                        hasRoute = true;
                        break;
                    }
                }

                if (!hasRoute)
                {
                    return;
                }

                foreach (var parameter in method.Parameters)
                {
                    AddSeed(parameter.Type, parameter.Locations.FirstOrDefault(static location => location.IsInSource) ?? declaration.GetLocation());
                }
            }

            // Axios / AxiosClient / ApiClient 的调用点：每个实参类型登记为种子，再顺带扫配置里的 Params / Data 赋值。
            public void AddInvocation(InvocationExpressionSyntax invocation)
            {
                if (_model.GetSymbolInfo(invocation, _cancellationToken).Symbol is not IMethodSymbol method
                    || method.ContainingType is null
                    || !AxiosContainerNames.Contains(method.ContainingType.ToDisplayString()))
                {
                    return;
                }

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (_model.GetTypeInfo(argument.Expression, _cancellationToken).Type is { } type)
                    {
                        AddSeed(type, argument.GetLocation());
                    }
                }

                SeedConfigSlots(invocation.DescendantNodes().OfType<AssignmentExpressionSyntax>());
            }

            // 形如 config.Params = … 的赋值：接收者必须是 AxiosConfig，右侧表达式登记为种子。
            public void AddConfigAssignment(AssignmentExpressionSyntax assignment)
            {
                if (assignment.Left is not MemberAccessExpressionSyntax access
                    || _model.GetTypeInfo(access.Expression, _cancellationToken).Type is not { } receiver
                    || receiver.ToDisplayString() != "AxiosNet.AxiosConfig")
                {
                    return;
                }

                SeedExpression(assignment.Right, assignment.Right.GetLocation());
            }

            // new AxiosConfig { … } 的初始化器：目标类型是 AxiosConfig 时逐个表达式登记。
            public void AddConfigInitializer(InitializerExpressionSyntax initializer)
            {
                if (!IsObjectCreation(initializer.Parent, out var creation) || !IsAxiosConfig(creation))
                {
                    return;
                }

                foreach (var expression in initializer.Expressions)
                {
                    if (expression is AssignmentExpressionSyntax
                        {
                            Left: IdentifierNameSyntax name
                        } assignment
                        && IsConfigSlot(name.Identifier.ValueText))
                    {
                        SeedExpression(assignment.Right, assignment.Right.GetLocation());
                    }
                }
            }

            // 从一批赋值里挑出 Params / Data（成员访问与裸标识符两种写法）并登记其右侧表达式。
            private void SeedConfigSlots(IEnumerable<AssignmentExpressionSyntax> assignments)
            {
                foreach (var assignment in assignments)
                {
                    switch (assignment.Left)
                    {
                        case MemberAccessExpressionSyntax member when IsConfigSlot(member.Name.Identifier.ValueText):
                        case IdentifierNameSyntax name when IsConfigSlot(name.Identifier.ValueText):
                            SeedExpression(assignment.Right, assignment.Right.GetLocation());
                            break;
                    }
                }
            }

            // 取表达式的静态类型登记种子；取不到类型（error 类型等）就跳过，生成期不抛。
            private void SeedExpression(ExpressionSyntax expression, Location location)
            {
                if (_model.GetTypeInfo(expression, _cancellationToken).Type is { } type)
                {
                    AddSeed(type, location);
                }
            }

            /// <summary>把一个「可能要展平」的类型加入收集：数组 / 集合 / 字典先拆元素，再由 <see cref="Collect"/> 建形状。</summary>
            private void AddSeed(ITypeSymbol type, Location location)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        AddSeed(array.ElementType, location);
                        return;

                    case ITypeParameterSymbol _:
                    case IPointerTypeSymbol _:
                    case IFunctionPointerTypeSymbol _:
                    case IDynamicTypeSymbol _:
                        return;

                    case INamedTypeSymbol named:
                        if (named.IsAnonymousType || IsScalar(named) || IsSkippedType(named))
                        {
                            return;
                        }

                        if (UnwrapNullable(named) is { } underlying)
                        {
                            AddSeed(underlying, location);
                            return;
                        }

                        if (IsDictionary(named, out var valueType))
                        {
                            if (valueType is not null)
                            {
                                // 非泛型 IDictionary 由运行期直接枚举；只有「仅实现泛型字典」的类型才需要生成访问器。
                                if (!HasInterface(named, "System.Collections.IDictionary"))
                                {
                                    CollectMap(named, location);
                                }

                                AddSeed(valueType, location);
                            }

                            return;
                        }

                        if (IsAsyncEnumerable(named))
                        {
                            return;
                        }

                        if (GetEnumerableElement(named) is { } element)
                        {
                            AddSeed(element, location);
                            return;
                        }

                        if (ImplementsNonGenericEnumerable(named))
                        {
                            // 元素类型未知（ArrayList 等）：运行期逐项展平，生成器无从下手。
                            return;
                        }

                        Collect(named, location);
                        return;
                }
            }

            // 递归收集一个类型的成员形状：接口与抽象类型不收，含类型参数或匿名类型的也不收（构造不出样本）。
            private void Collect(INamedTypeSymbol type, Location location)
            {
                if (type.TypeKind is TypeKind.Interface || type.IsAbstract)
                {
                    return;
                }

                if (ContainsTypeParameter(type) || ContainsAnonymousType(type))
                {
                    // 匿名类型实参在源码里写不出名字（Collect 无法为它们生成形状）；匿名对象本身走样本通道。
                    return;
                }

                var expression = TypeFormats.Display(type);
                var key = "named:" + expression;
                if (!_seenTypes.Add(key))
                {
                    return;
                }

                if (!IsTypeAccessible(type, _compilation.Assembly))
                {
                    AddDiagnostic(
                        Diagnostics.ShapeTypeInaccessible,
                        location,
                        type.ToDisplayString(),
                        "类型的访问级别对生成代码不可见（private 嵌套 / file-local / 跨程序集 internal）");
                    return;
                }

                var members = ImmutableArray.CreateBuilder<ShapeMemberInfo>();

                // 请求模型成员的 [Format] 由模型管线报 AXA015：同一处声明只报一次，
                // 形状这边只覆盖「不是请求模型、但会被展平」的普通类型。
                var reportFormat = HasRouteAttribute(type) ? IgnoreFormatDiagnostic : AddFormatDiagnostic;

                foreach (var property in EnumerateProperties(type))
                {
                    var getter = property.GetMethod;
                    if (getter is null)
                    {
                        continue;
                    }

                    // 展平只读 public getter：反射形状同样只收录 public getter，非 public 读访问器两边一致跳过。
                    if (getter.DeclaredAccessibility != Accessibility.Public)
                    {
                        continue;
                    }

                    if (property.Type is INamedTypeSymbol { IsRefLikeType: true }
                        || property.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
                    {
                        continue;
                    }

                    var fieldName = FindInOverrideChain(property, _wellKnown.JsonPropertyName) is { } jsonName
                        && jsonName.ConstructorArguments.Length == 1
                        && jsonName.ConstructorArguments[0].Value is string name
                            ? name
                            : property.Name;

                    // 接收者显式转成声明类型：派生类可能用 static new 遮挡同名属性（不可见成员不参与形状，
                    // 但 x.Name 仍会绑定到派生类的 static 成员）。
                    members.Add(new ShapeMemberInfo(
                        property.Name,
                        fieldName,
                        IsIgnored(property),
                        "static x => (object)((" + TypeFormats.Display(property.ContainingType) + ")x)."
                            + EscapeIdentifier(property.Name),
                        BuildFormatExpression(
                            FindInOverrideChain(property, _wellKnown.Format),
                            $"属性 {type.Name}.{property.Name}",
                            location,
                            reportFormat)));

                    AddSeed(property.Type, location);
                }

                Shapes.Add(new ShapeInfo(
                    key,
                    BuildShapeClassName(key),
                    "AxiosNet.Shape." + BuildShapeClassName(key) + ".g.cs",
                    expression,
                    null,
                    new EquatableArray<ShapeMemberInfo>(members.ToImmutable()),
                    null,
                    true));
            }

            /// <summary>自定义只读字典：只为 params 平铺生成枚举访问器，不注册 POCO 形状（表单按 IEnumerable 展平）。</summary>
            private void CollectMap(INamedTypeSymbol type, Location location)
            {
                var mapInterface = GetMapInterface(type);
                if (mapInterface is null || ContainsAnonymousType(type))
                {
                    return;
                }

                var expression = TypeFormats.Display(type);
                var key = "map:" + expression;
                if (!_seenTypes.Add(key))
                {
                    return;
                }

                if (!IsTypeAccessible(type, _compilation.Assembly))
                {
                    AddDiagnostic(
                        Diagnostics.ShapeTypeInaccessible,
                        location,
                        type.ToDisplayString(),
                        "类型的访问级别对生成代码不可见（private 嵌套 / file-local / 跨程序集 internal）");
                    return;
                }

                Shapes.Add(new ShapeInfo(
                    key,
                    BuildShapeClassName(key),
                    "AxiosNet.Shape." + BuildShapeClassName(key) + ".g.cs",
                    expression,
                    null,
                    new EquatableArray<ShapeMemberInfo>(ImmutableArray<ShapeMemberInfo>.Empty),
                    mapInterface,
                    false));
            }

            // 匿名类型收敛成形状；泛型上下文里的匿名类型直接放弃（模块初始化器无法构造样本）。
            private void AddAnonymousShape(INamedTypeSymbol anonymous, Location location, bool reportInaccessible)
            {
                if (ContainsTypeParameter(anonymous))
                {
                    // 泛型上下文里的匿名类型（成员类型含类型参数）无法在模块初始化器里构造样本。
                    return;
                }

                foreach (var property in anonymous.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!IsTypeAccessible(property.Type, _compilation.Assembly))
                    {
                        // 与 Axios 无关的匿名对象（例如 LINQ 投影）不应产生警告；形状照样跳过，AOT 下真用到会显式失败。
                        if (reportInaccessible)
                        {
                            AddDiagnostic(
                                Diagnostics.ShapeTypeInaccessible,
                                location,
                                property.Type.ToDisplayString(),
                                "匿名类型的成员类型对生成代码不可见，无法构造结构相同的样本");
                        }

                        return;
                    }
                }

                var sample = BuildSample(anonymous);
                if (sample is null)
                {
                    // 样本写不出来（指针 / ref struct）：不生成，AOT 下展平到该匿名类型会显式缺形状。
                    return;
                }

                var members = ImmutableArray.CreateBuilder<ShapeMemberInfo>();
                var keyParts = new List<string>();
                foreach (var property in anonymous.GetMembers().OfType<IPropertySymbol>())
                {
                    members.Add(new ShapeMemberInfo(
                        property.Name,
                        property.Name,
                        false,
                        "static x => (object)x." + EscapeIdentifier(property.Name),
                        null));
                    keyParts.Add(property.Name + ":" + TypeFormats.Display(property.Type));

                    // 匿名对象的成员类型同样参与展平（嵌套对象 / 字典值），必须一起生成形状。
                    AddSeed(property.Type, location);
                }

                var key = "anon:" + string.Join(";", keyParts);
                if (!_seenTypes.Add(key))
                {
                    return;
                }

                Shapes.Add(new ShapeInfo(
                    key,
                    BuildShapeClassName(key),
                    "AxiosNet.Shape." + BuildShapeClassName(key) + ".g.cs",
                    null,
                    sample,
                    new EquatableArray<ShapeMemberInfo>(members.ToImmutable()),
                    null,
                    true));
            }

            /// <summary>
            /// 构造与目标匿名类型「属性名 / 类型 / 顺序」完全一致的样本：匿名类型按结构同一，
            /// 样本在 <c>RegisterGeneratedShapeFromSample&lt;T&gt;(T sample, ...)</c> 里推断出目标类型。
            /// </summary>
            private string? BuildSample(ITypeSymbol type)
            {
                if (type is IArrayTypeSymbol array)
                {
                    if (!ContainsAnonymousType(array.ElementType))
                    {
                        return IsSampleable(type) ? "default(" + TypeFormats.Display(type) + ")" : null;
                    }

                    // 匿名元素无法在源码里命名，但 new[] { 样本 } 可以让编译器按元素样本推断出同结构数组。
                    // 多维数组写不出来（new[] 只有一维），宁可不注册，AOT 下真用到会显式缺形状。
                    if (array.Rank != 1)
                    {
                        return null;
                    }

                    var elementSample = BuildSample(array.ElementType);
                    return elementSample is null ? null : "new[] { " + elementSample + " }";
                }

                if (type is not INamedTypeSymbol { IsAnonymousType: true } anonymous)
                {
                    // 其它含匿名类型实参的泛型（List<匿名类型> 等）在源码里无法命名：不注册，避免生成非法样本。
                    if (ContainsAnonymousType(type))
                    {
                        return null;
                    }

                    return IsSampleable(type) ? "default(" + TypeFormats.Display(type) + ")" : null;
                }

                if (ContainsTypeParameter(anonymous))
                {
                    return null;
                }

                var parts = new List<string>();
                foreach (var property in anonymous.GetMembers().OfType<IPropertySymbol>())
                {
                    var sample = BuildSample(property.Type);
                    if (sample is null)
                    {
                        return null;
                    }

                    parts.Add(EscapeIdentifier(property.Name) + " = " + sample);
                }

                return "new { " + string.Join(", ", parts) + " }";
            }

            // 能否在生成代码里构造样本：指针、函数指针、类型参数、ref struct 一律不行。
            private static bool IsSampleable(ITypeSymbol type)
                => type.TypeKind switch
                {
                    TypeKind.Pointer => false,
                    TypeKind.FunctionPointer => false,
                    TypeKind.TypeParameter => false,
                    _ => type is not INamedTypeSymbol { IsRefLikeType: true } && !ContainsTypeParameter(type)
                };

            // JsonIgnore 在 override 链上也算：基类属性标了忽略，派生类的同名属性同样不进形状。
            private bool IsIgnored(IPropertySymbol property)
            {
                var attribute = FindInOverrideChain(property, _wellKnown.JsonIgnore);
                if (attribute is null)
                {
                    return false;
                }

                // JsonIgnoreCondition：Never=0 / Always=1 / WhenWritingDefault=2 / WhenWritingNull=3；缺省即 Always。
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Key == "Condition")
                    {
                        return named.Value.Value is int condition && condition == 1;
                    }
                }

                return true;
            }

            // 同一组 (诊断 ID, 参数) 只报一次：多个语法种子会命中同一个类型。
            private void AddDiagnostic(DiagnosticDescriptor descriptor, Location location, params string[] args)
            {
                var key = descriptor.Id + "|" + string.Join("|", args);
                if (_seenDiagnostics.Add(key))
                {
                    DiagnosticItems.Add(new DiagnosticInfo(descriptor, new EquatableArray<string>(args.ToImmutableArray()), location));
                }
            }

            /// <summary>请求模型的成员由模型管线报 <c>[Format]</c> 诊断：形状这边不再重复报。</summary>
            private static readonly Action<DiagnosticInfo> IgnoreFormatDiagnostic = static _ => { };

            /// <summary><c>[Format]</c> 的构建期诊断也走同一份去重表（同一个属性被多次收集时只报一条）。</summary>
            private void AddFormatDiagnostic(DiagnosticInfo diagnostic)
                => AddDiagnostic(diagnostic.Descriptor, diagnostic.Location, diagnostic.MessageArgs.ToArray());

            /// <summary>该类型是否带路由特性（即由模型管线负责报 <c>[Format]</c> 诊断）。</summary>
            private bool HasRouteAttribute(INamedTypeSymbol type)
            {
                foreach (var attribute in type.GetAttributes())
                {
                    if (DerivesFrom(attribute.AttributeClass, _wellKnown.HttpApi))
                    {
                        return true;
                    }
                }

                return false;
            }

            // 由形状 key 拼合法类名：只留字母数字、总长截到 48，避免生成超长标识符。
            private static string BuildShapeClassName(string key)
            {
                var builder = new System.Text.StringBuilder();
                foreach (var character in key)
                {
                    if (builder.Length >= 48)
                    {
                        break;
                    }

                    builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
                }

                return "__AxiosShape_" + builder + "_" + Fnv1a(key).ToString("x8");
            }
        }

        // 拆掉 Nullable 取内层类型；不是可空值类型时返回 null。
        private static INamedTypeSymbol? UnwrapNullable(INamedTypeSymbol type)
            => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? type.TypeArguments[0] as INamedTypeSymbol
                : null;

        // 递归看数组元素与泛型实参：含类型参数就无法烘焙出具体形状。
        private static bool ContainsTypeParameter(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol _:
                    return true;
                case IArrayTypeSymbol array:
                    return ContainsTypeParameter(array.ElementType);
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                    {
                        if (ContainsTypeParameter(argument))
                        {
                            return true;
                        }
                    }

                    return named.ContainingType is not null && ContainsTypeParameter(named.ContainingType);
                default:
                    return false;
            }
        }

        // 递归看是否含匿名类型：匿名类型既不能出现在公开签名上，也构造不出样本。
        private static bool ContainsAnonymousType(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return ContainsAnonymousType(array.ElementType);
                case INamedTypeSymbol named:
                    if (named.IsAnonymousType)
                    {
                        return true;
                    }

                    foreach (var argument in named.TypeArguments)
                    {
                        if (ContainsAnonymousType(argument))
                        {
                            return true;
                        }
                    }

                    return named.ContainingType is not null && ContainsAnonymousType(named.ContainingType);
                default:
                    return false;
            }
        }

        /// <summary>是否按标量处理（先解可空）：集合与复杂类型要展平成多个键。</summary>
        private static bool IsScalar(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return named.TypeArguments.Length == 1 && IsScalar(named.TypeArguments[0]);
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_String:
                case SpecialType.System_Char:
                case SpecialType.System_DateTime:
                    return true;
            }

            if (type is not INamedTypeSymbol namedType)
            {
                return false;
            }

            var metadataName = GetMetadataName(namedType);
            return metadataName == "System.DateTimeOffset"
                || metadataName == "System.DateOnly"
                || metadataName == "System.TimeOnly"
                || metadataName == "System.TimeSpan"
                || metadataName == "System.Guid"
                || metadataName == "System.Uri"
                || metadataName == "System.Threading.CancellationToken";
        }

        /// <summary>展平通道自己会处理、不需要对象形状的类型（JSON 树 / 文件 / 任务 / 委托等）。</summary>
        private static bool IsSkippedType(INamedTypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Object || type.TypeKind is TypeKind.Delegate or TypeKind.Error)
            {
                return true;
            }

            if (DerivesFromName(type, "System.Text.Json.JsonElement")
                || DerivesFromName(type, "System.Text.Json.JsonDocument")
                || DerivesFromName(type, "System.Text.Json.Nodes.JsonNode")
                || DerivesFromName(type, "System.IO.Stream")
                || DerivesFromName(type, "System.Net.Http.HttpContent")
                || DerivesFromName(type, "AxiosNet.FormFile")
                || DerivesFromName(type, "System.Threading.Tasks.Task")
                || DerivesFromName(type, "System.Threading.Tasks.ValueTask"))
            {
                return true;
            }

            var metadataName = GetMetadataName(type);
            return metadataName == "System.Memory`1"
                || metadataName == "System.ReadOnlyMemory`1"
                || metadataName == "System.ArraySegment`1"
                || metadataName == "System.Threading.Tasks.ValueTask`1"
                // 配置对象出现在「url, config」重载的参数位，不是待展平的数据。
                || metadataName == "AxiosNet.AxiosConfig";
        }

        // 沿基类链按元数据名比较，所以具体特性（HttpGet 之类）也能命中特性基类。
        private static bool DerivesFromName(INamedTypeSymbol type, string metadataName)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (GetMetadataName(current) == metadataName)
                {
                    return true;
                }
            }

            return false;
        }

        // 是否字典：泛型字典顺带取出值类型；只实现非泛型 IDictionary 的也算（值形状仍要收集）。
        private static bool IsDictionary(INamedTypeSymbol type, out ITypeSymbol? valueType)
        {
            valueType = FindDictionaryValueType(type);

            // 同时实现泛型字典与非泛型 IDictionary 时值形状仍要收集，只是不需要映射访问器。
            return valueType is not null || HasInterface(type, "System.Collections.IDictionary");
        }

        /// <summary>
        /// 字典的值类型（任意键类型）：运行期按 <c>IDictionary</c> 展平时值的类型仍需形状，
        /// 非字符串键的字典拿不到映射访问器，但值形状照样要收集。
        /// </summary>
        private static ITypeSymbol? FindDictionaryValueType(INamedTypeSymbol type)
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(type))
            {
                if (!candidate.IsGenericType)
                {
                    continue;
                }

                var metadataName = GetMetadataName(candidate.OriginalDefinition);
                if (metadataName != "System.Collections.Generic.IDictionary`2"
                    && metadataName != "System.Collections.Generic.IReadOnlyDictionary`2")
                {
                    continue;
                }

                var arguments = candidate.TypeArguments;
                if (arguments.Length == 2)
                {
                    return arguments[1];
                }
            }

            return null;
        }

        // 自身或任一接口是 IAsyncEnumerable 时为真：流式返回的类型不做形状烘焙。
        private static bool IsAsyncEnumerable(INamedTypeSymbol type)
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(type))
            {
                if (candidate.IsGenericType
                    && GetMetadataName(candidate.OriginalDefinition) == "System.Collections.Generic.IAsyncEnumerable`1")
                {
                    return true;
                }
            }

            return false;
        }

        // 取 IEnumerable 的元素类型；只是普通集合（非泛型枚举）时返回 null。
        private static ITypeSymbol? GetEnumerableElement(INamedTypeSymbol type)
        {
            if (type.IsGenericType
                && GetMetadataName(type.OriginalDefinition) == "System.Collections.Generic.IEnumerable`1")
            {
                return type.TypeArguments[0];
            }

            foreach (var candidate in type.AllInterfaces)
            {
                if (candidate.IsGenericType
                    && GetMetadataName(candidate.OriginalDefinition) == "System.Collections.Generic.IEnumerable`1")
                {
                    return candidate.TypeArguments[0];
                }
            }

            return null;
        }

        // 是否实现非泛型 IEnumerable：手写集合常只继承它。
        private static bool ImplementsNonGenericEnumerable(INamedTypeSymbol type)
            => HasInterface(type, "System.Collections.IEnumerable");

        // 自身或任一接口的元数据名是否等于给定名。
        private static bool HasInterface(INamedTypeSymbol type, string metadataName)
        {
            if (GetMetadataName(type) == metadataName)
            {
                return true;
            }

            foreach (var candidate in type.AllInterfaces)
            {
                if (GetMetadataName(candidate) == metadataName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>只读字典的自定义实现：生成映射访问器（运行期 QueryMap 优先用注册表）。</summary>
        private static string? GetMapInterface(INamedTypeSymbol type)
        {
            if (HasInterface(type, "System.Collections.IDictionary"))
            {
                // 与 AddSeed 的判定保持同一口径：非泛型 IDictionary 由运行期直接枚举。
                return null;
            }

            foreach (var candidate in EnumerateSelfAndInterfaces(type))
            {
                if (!candidate.IsGenericType)
                {
                    continue;
                }

                var metadataName = GetMetadataName(candidate.OriginalDefinition);
                if (metadataName != "System.Collections.Generic.IDictionary`2"
                    && metadataName != "System.Collections.Generic.IReadOnlyDictionary`2")
                {
                    continue;
                }

                var arguments = candidate.TypeArguments;
                if (arguments.Length == 2
                    && arguments[0].SpecialType == SpecialType.System_String
                    && arguments[1].SpecialType != SpecialType.System_Object)
                {
                    return TypeFormats.Display(candidate);
                }
            }

            return null;
        }

        // 先自身再全部接口：查找接口时不能漏掉「类型本身就是该接口」这种情况。
        private static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var contract in type.AllInterfaces)
            {
                yield return contract;
            }
        }

        // 拼出带外层类型的元数据名（Outer+Inner），与 GetTypeByMetadataName 的写法对齐。
        private static string GetMetadataName(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
            {
                name = containing.MetadataName + "+" + name;
            }

            var ns = type.ContainingNamespace;
            return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() + "." + name : name;
        }
    }
}
