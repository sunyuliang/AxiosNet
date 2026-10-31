namespace AxiosNet.WebAPI.Internal;

/// <summary>返回类型的形态判定与可读描述（只读 <see cref="Type"/>，AOT 下可用）。</summary>
internal static class ReturnTypeShape
{
    /// <summary>正文类型是否为 <c>IAsyncEnumerable&lt;T&gt;</c>（流式响应按帧交付）。</summary>
    public static bool IsStreaming(Type responseType)
        => responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

    /// <summary>返回类型的可读形态（<c>ValueTask&lt;AxiosResponse&lt;User&gt;&gt;</c>），用于错误消息。</summary>
    public static string Describe(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var arity = name.IndexOf('`');
        if (arity >= 0)
        {
            name = name[..arity];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>";
    }
}
