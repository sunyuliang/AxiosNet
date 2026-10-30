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

        public static readonly DiagnosticDescriptor InterfaceMemberNotSupported = new DiagnosticDescriptor(
            "AXA001",
            "接口含无法代理的成员",
            "接口 {0} 的 {1} 无法代理：接口级声明只支持实例方法（不支持属性、事件与静态成员）。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceShapeUnsupported = new DiagnosticDescriptor(
            "AXA002",
            "接口方法形态不支持",
            "接口 {0} 的 {1} 无法生成代理：{2}。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceRouteInvalid = new DiagnosticDescriptor(
            "AXA003",
            "接口方法必须且只能标注一个路由特性",
            "接口 {0} 的 {1} {2}：请在方法上标注一个 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceReturnUnsupported = new DiagnosticDescriptor(
            "AXA004",
            "接口方法返回形态不支持",
            "接口 {0} 的 {1} 返回 {2}：必须返回 Task<AxiosResponse<T>> / Task<T> / Task<AxiosResponse> / Task（不支持同步返回 / ValueTask）。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceStreamInvalid = new DiagnosticDescriptor(
            "AXA005",
            "接口方法的 [Stream] 声明无效",
            "接口 {0} 的 {1} 的 [Stream] 无效：{2}。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceAuthorizationConflictAtType = new DiagnosticDescriptor(
            "AXA008",
            "[Authorize] 与 [AllowAnonymous] 同层",
            "接口 {0} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceAuthorizationConflict = new DiagnosticDescriptor(
            "AXA008",
            "[Authorize] 与 [AllowAnonymous] 同层",
            "接口 {0} 的 {1} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceUnsupportedType = new DiagnosticDescriptor(
            "AXA009",
            "接口无法生成代理",
            "接口 {0} 无法生成代理：{1}。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InterfaceCustomRouteAttribute = new DiagnosticDescriptor(
            "AXA010",
            "自定义 HttpApiAttribute 子类无法静态求值",
            "接口 {0} 的 {1} 使用了自定义路由特性 {2}：源生成器无法静态执行自定义构造器，已跳过该接口的代理生成。"
            + "请改用 [HttpApi(\"METHOD\", \"path\")] 或内置的 [Get]/[Post] 等（JIT 下仍可走 DispatchProxy）。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor ClosedGenericInterfaceMissing = new DiagnosticDescriptor(
            "AXA011",
            "开放泛型接口需要闭合调用点",
            "接口 {0} 是开放泛型，无法直接生成代理：请在使用方的调用点写成闭合类型（如 Create<{0}<T>>() / AddApiInterface<...>()）由该程序集生成；"
            + "Native AOT 下若没有任何闭合调用点，Create 时会显式失败。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);
    }
}
