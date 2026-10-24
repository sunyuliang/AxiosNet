using System.Runtime.CompilerServices;

// 测试 seam：HolderDisposal 的失败容错循环（首个失败保留堆栈、不跳过其余站点）无法从公开面触发
// —— 连接池与 Handler 的释放路径都刻意吞掉关闭期异常，holder 是内部类型且无注入点。
// 只对测试程序集开放内部可见性，不影响公开 API 面。
[assembly: InternalsVisibleTo("AxiosNet.DependencyInjection.Tests")]
