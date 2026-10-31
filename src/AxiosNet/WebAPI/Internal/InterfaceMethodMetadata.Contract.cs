namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 接口方法的契约构建：把声明（源生成器 / 手工注册烘焙，或 JIT 反射读出的同一形状）归一化成路由、
/// 绑定计划与授权。<see cref="BuildCore"/> 是两条路径唯一的一份绑定规则，<see cref="FromDeclaration"/> 是声明路径的入口。
/// </summary>
internal sealed partial class InterfaceMethodMetadata
{
    /// <summary>按声明构建方法契约；声明错误随元数据携带，首次使用时抛出。</summary>
    internal static InterfaceMethodMetadata FromDeclaration(
        InterfaceDeclarationData owner,
        InterfaceMethodDeclaration declaration)
    {
        try
        {
            return BuildCore(owner, declaration);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return FromError(ex.Message);
        }
    }

    /// <summary>声明路径的契约构建：只做归一化、绑定与授权。</summary>
    internal static InterfaceMethodMetadata BuildCore(
        InterfaceDeclarationData owner,
        InterfaceMethodDeclaration declaration)
    {
        if (owner.Error is { } ownerError)
        {
            throw new InvalidOperationException(ownerError);
        }

        if (declaration.Error is { } declarationError)
        {
            throw new InvalidOperationException(declarationError);
        }

        var interfaceType = owner.InterfaceType;
        var methodName = declaration.Name;
        if (string.IsNullOrWhiteSpace(declaration.MethodName) || string.IsNullOrWhiteSpace(declaration.Path))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 必须且只能标注一个路由特性（[Get] / [Post] / ...）。");
        }

        var method = DeclarationNormalization.HttpMethodFrom(declaration.MethodName);
        var path = DeclarationNormalization.Path(declaration.Path);
        var ownerText = $"接口 {interfaceType.Name}.{methodName}";
        var accumulator = new BindingAccumulator(ownerText, "参数");

        // 接口级声明在前、方法级在后（同名后者覆盖）。
        accumulator.AddStaticHeaders(owner.StaticHeaders);
        accumulator.AddStaticHeaders(declaration.StaticHeaders);

        var isMultipart = declaration.Multipart;
        var parts = new List<PartBinding>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);
        var cancellationTokenIndex = declaration.CancellationTokenIndex;

        foreach (var parameter in declaration.Parameters)
        {
            if (parameter.ValueType == typeof(CancellationToken))
            {
                // 声明可以统一列出全部参数，取消令牌按类型跳过，位置由 CancellationToken(index) 标注。
                if (cancellationTokenIndex >= 0 && cancellationTokenIndex != parameter.Index)
                {
                    throw new InvalidOperationException($"接口 {interfaceType.Name} 的 {methodName} 声明了多个 CancellationToken 参数：最多一个。");
                }

                cancellationTokenIndex = parameter.Index;
                continue;
            }

            if (parameter.Ignore)
            {
                if (parameter.HasAnyBinding)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{methodName}.{parameter.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                continue;
            }

            var source = new ArgumentValueSource(
                parameter.Index,
                parameter.Name,
                parameter.ValueType,
                parameter.Description ?? $"{interfaceType.Name}.{methodName}.{parameter.Name}");

            if (parameter.Path)
            {
                accumulator.AddPath(source, parameter.PathName ?? string.Empty);
            }

            if (parameter.Header)
            {
                accumulator.AddHeader(source, parameter.HeaderName ?? string.Empty);
            }

            if (parameter.HeaderCollection)
            {
                accumulator.AddHeaderCollection(source);
            }

            if (parameter.Part)
            {
                if (!isMultipart)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{methodName}.{parameter.Name} 标注了 [Part]，但方法没有 [Multipart]：请在方法上声明 [Multipart]。");
                }

                var display = $"参数 {source.Description}";
                var explicitName = DeclarationNormalization.OptionalPartValue(parameter.PartName, display, nameof(PartAttribute.Name));
                var partName = explicitName ?? parameter.Name;
                if (!seenPartNames.Add(partName))
                {
                    throw DuplicatePart(ownerText, partName);
                }

                parts.Add(new PartBinding(
                    partName,
                    source,
                    explicitName is not null,
                    DeclarationNormalization.OptionalPartValue(parameter.PartFileName, display, nameof(PartAttribute.FileName)),
                    DeclarationNormalization.OptionalPartValue(parameter.PartContentType, display, nameof(PartAttribute.ContentType))));
            }

            if (parameter.Body)
            {
                accumulator.AddBody(source);
            }

            if (!parameter.HasAnyBinding)
            {
                if (isMultipart)
                {
                    if (!seenPartNames.Add(parameter.Name))
                    {
                        throw DuplicatePart(ownerText, parameter.Name);
                    }

                    parts.Add(new PartBinding(parameter.Name, source, false, null, null));
                    continue;
                }

                accumulator.AddUnannotated(source);
            }
            else if (parameter.Query)
            {
                accumulator.AddQuery(source, parameter.QueryName ?? string.Empty);
            }
        }

        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + "multipart 方法的请求体由全部参数装配，两者互斥。");
        }

        var plan = accumulator.Build(method, path, allowDefaultBody: !isMultipart);

        var shape = declaration.ReturnShape;
        if (shape == InterfaceReturnShape.Unsupported)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} {ReturnTypeClause(declaration, "返回类型不受支持")}："
                + "接口方法必须返回 Task<AxiosResponse<T>> / Task<T> / Task<AxiosResponse> / Task。");
        }

        var isStreaming = shape == InterfaceReturnShape.Response
            && declaration.ResponseType is not null
            && ReturnTypeShape.IsStreaming(declaration.ResponseType);

        if (declaration.HasStreamAttribute && !isStreaming)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 标注了 [Stream]，但"
                + $"{ReturnTypeClause(declaration, "返回类型不是 Task<AxiosResponse<IAsyncEnumerable<T>>>")}："
                + "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法。");
        }

        if (isStreaming && !Enum.IsDefined(typeof(AxiosStreamFormat), declaration.StreamFormat))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 的 [Stream] 帧格式值 {(int)declaration.StreamFormat} 无效：请使用 AxiosStreamFormat 的已定义值。");
        }

        // 方法级声明压过接口级；两层都未声明时继承实例默认授权。
        var authorization = declaration.AllowAnonymous || declaration.AuthorizeScheme is not null
            ? DeclarationNormalization.Authorization(declaration.AllowAnonymous, declaration.AuthorizeScheme, ownerText)
            : DeclarationNormalization.Authorization(owner.AllowAnonymous, owner.AuthorizeScheme, $"接口 {interfaceType.Name}");

        return new InterfaceMethodMetadata(
            interfaceType,
            methodName,
            method,
            path,
            plan,
            authorization,
            cancellationTokenIndex,
            isMultipart,
            [.. parts],
            invoker: null,
            fault: null);
    }

    /// <summary>错误消息里「返回类型…」这一句：能拿到 <see cref="Type"/> 时给出具体类型，否则用调用方给的固定说法。</summary>
    private static string ReturnTypeClause(InterfaceMethodDeclaration declaration, string fallback)
        => declaration.ReturnTypeText is { } text ? $"返回类型是 {text}" : fallback;
}
