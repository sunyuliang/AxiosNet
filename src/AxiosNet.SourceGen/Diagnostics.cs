using Microsoft.CodeAnalysis;

namespace AxiosNet.SourceGen
{
    /// <summary>AXA 诊断：把「生成器无法静态表达」的事实提前到编译期；其余声明合法性仍由运行时共享构建器校验。</summary>
    internal static class Diagnostics
    {
        private const string Category = "AxiosNet.SourceGen";
        private const string HelpLink = "https://github.com/sunyuliang/AxiosNet#native-aot";

        public static readonly DiagnosticDescriptor MultipleRoutes = new DiagnosticDescriptor(
            "AXA003",
            "请求模型必须且只能标注一个路由特性",
            "请求模型 {0} 标注了多个路由特性（{1}）：每份声明必须且只能标注一个 HttpApiAttribute 或其子类。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor RouteNotConstant = new DiagnosticDescriptor(
            "AXA003",
            "路由特性参数无效",
            "请求模型 {0} 的路由特性参数为空白或无法在编译期求值：请写成 [Get(\"...\")] / [HttpApi(\"GET\", \"...\")]。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor IgnoreConflict = new DiagnosticDescriptor(
            "AXA006",
            "[ApiIgnore] 与参数特性同用",
            "{0}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor AuthorizationConflict = new DiagnosticDescriptor(
            "AXA008",
            "[Authorize] 与 [AllowAnonymous] 同层",
            "请求模型 {0} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnsupportedType = new DiagnosticDescriptor(
            "AXA009",
            "请求模型无法生成元数据",
            "请求模型 {0} 无法生成元数据：{1}。请改用可访问的非泛型类型，或保留 JIT 反射路径（Native AOT 下必须能生成）。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor CustomRouteAttribute = new DiagnosticDescriptor(
            "AXA010",
            "自定义 HttpApiAttribute 子类无法静态求值",
            "请求模型 {0} 使用了自定义路由特性 {1}：源生成器无法静态执行自定义构造器，已跳过生成。请改用 [HttpApi(\"METHOD\", \"path\")] 或内置的 [Get]/[Post] 等（JIT 下仍可走反射）。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor GeneratorDisabledWithAot = new DiagnosticDescriptor(
            "AXA012",
            "AOT 项目关闭了 AxiosNet 源生成器",
            "项目启用了 PublishAot 但 AxiosNetSourceGen=false：Native AOT 下请求模型 / 接口将缺少生成元数据。请删除该属性，或改用 JIT 运行。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);
    }
}
