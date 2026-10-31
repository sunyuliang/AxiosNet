using Microsoft.CodeAnalysis;

namespace AxiosNet.SourceGen
{
    /// <summary>
    /// AXA 诊断：把「生成器无法静态表达」的事实提前到编译期；其余声明合法性仍由运行时共享构建器校验。
    /// 一个编号只对应一个语义（模型侧与接口侧共用同一个 descriptor，位置信息由 <c>{0}</c> 参数传入），
    /// 这样 <c>#pragma warning disable AXA00x</c> 关掉的正好是一类问题。
    /// </summary>
    internal static class Diagnostics
    {
        private const string Category = "AxiosNet.SourceGen";
        private const string HelpLink = "https://github.com/sunyuliang/AxiosNet#native-aot";

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

        /// <summary>路由特性个数不对：模型 / 接口方法都走这一条（<c>{0}</c> 是定位串，<c>{1}</c> 是具体原因）。</summary>
        public static readonly DiagnosticDescriptor RouteCountInvalid = new DiagnosticDescriptor(
            "AXA003",
            "必须且只能标注一个路由特性",
            "{0} {1}：每份声明必须且只能标注一个 HttpApiAttribute 或其子类（[Get] / [Post] / ...）。",
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

        public static readonly DiagnosticDescriptor IgnoreConflict = new DiagnosticDescriptor(
            "AXA006",
            "[ApiIgnore] 与参数特性同用",
            "{0}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        /// <summary>路由特性存在但参数为空 / 无法在编译期求值（与「个数不对」分开：修法不同）。</summary>
        public static readonly DiagnosticDescriptor RouteValueInvalid = new DiagnosticDescriptor(
            "AXA007",
            "路由特性参数无效",
            "{0} 的路由特性参数为空白或无法在编译期求值：请写成 [Get(\"...\")] / [HttpApi(\"GET\", \"...\")]。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        /// <summary>授权与匿名互斥：模型、接口类型、接口方法三层共用（<c>{0}</c> 是完整定位串）。</summary>
        public static readonly DiagnosticDescriptor AuthorizationConflict = new DiagnosticDescriptor(
            "AXA008",
            "[Authorize] 与 [AllowAnonymous] 同层",
            "{0} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        /// <summary>无法生成元数据：模型与接口共用（<c>{0}</c> 是定位串）。</summary>
        public static readonly DiagnosticDescriptor UnsupportedType = new DiagnosticDescriptor(
            "AXA009",
            "无法生成元数据",
            "{0} 无法生成元数据：{1}。请改用可访问的非泛型类型，或保留 JIT 反射路径（Native AOT 下必须能生成）。",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        /// <summary>自定义 HttpApiAttribute 子类无法静态求值：模型与接口共用（<c>{0}</c> 是定位串）。</summary>
        public static readonly DiagnosticDescriptor CustomRouteAttribute = new DiagnosticDescriptor(
            "AXA010",
            "自定义 HttpApiAttribute 子类无法静态求值",
            "{0} 使用了自定义路由特性 {1}：源生成器无法静态执行自定义构造器，已跳过生成。"
            + "请改用 [HttpApi(\"METHOD\", \"path\")] 或内置的 [Get]/[Post] 等（JIT 下仍可走反射）。",
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

        public static readonly DiagnosticDescriptor GeneratorDisabledWithAot = new DiagnosticDescriptor(
            "AXA012",
            "AOT 项目关闭了 AxiosNet 源生成器",
            "项目启用了 PublishAot 但 AxiosNetSourceGen=false：Native AOT 下请求模型 / 接口将缺少生成元数据。请删除该属性，或改用 JIT 运行。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor ShapeTypeInaccessible = new DiagnosticDescriptor(
            "AXA013",
            "对象形状类型无法生成",
            "类型 {0} 无法生成对象形状：{1}。Native AOT 下展平到该类型会显式失败；请改成生成代码可见的类型（public，或同一程序集内的 internal），或改用 JIT 反射。",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);
    }
}
