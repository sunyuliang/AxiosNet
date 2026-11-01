namespace AxiosNet.Cli;

/// <summary>命令行用法错误：把错误打到 stderr、附带帮助，并返回退出码 2。</summary>
internal sealed class CliUsageException : Exception
{
    /// <summary>用一条可读的用法错误信息构造异常。</summary>
    public CliUsageException(string message) : base(message)
    {
    }
}

/// <summary>文档或代码生成失败：无法继续时返回退出码 1。</summary>
internal sealed class GenerationException : Exception
{
    /// <summary>只带错误信息（由生成器自身的校验或 IO 判定产生）。</summary>
    public GenerationException(string message) : base(message)
    {
    }

    /// <summary>保留底层异常（解析器、HTTP 下载等），便于 <c>-v</c> / 日志里追根因。</summary>
    public GenerationException(string message, Exception inner) : base(message, inner)
    {
    }
}
