using System.Runtime.CompilerServices;

// 测试项目需要直接访问内部类型（解析器 / 读取器 / 发射器），不额外暴露成 public API。
[assembly: InternalsVisibleTo("AxiosNet.Cli.Tests")]
