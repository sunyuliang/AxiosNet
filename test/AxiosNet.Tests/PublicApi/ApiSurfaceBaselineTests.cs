using System.Reflection;
using System.Text;

namespace AxiosNet.Tests.PublicApi;

/// <summary>
/// 公共 API 面的二进制基线：把程序集里所有对外可见的类型与成员拍平成文本，与仓库里签入的基线逐行比对。
/// </summary>
/// <remarks>
/// 为什么用反射而不是 <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c>：本项目的 restore 不可用
/// （所有构建都带 <c>--no-restore</c>），加包引用这条路走不通；反射版能达到同样的目的 ——
/// 任何「删成员 / 改签名 / 改可见性」都会让基线对不上，而**纯新增**同样会报出来，逼你显式确认这是有意的 API 扩张。
/// <para>
/// 基线按 TFM 分文件（<c>PublicAPI.net6.0.txt</c> / <c>PublicAPI.net8.0.txt</c>）：
/// 两个目标的公共面可能不同（<c>NET*_OR_GREATER</c> 分支里的 API 只存在于较新目标），
/// 合成一份就会把「net8 独有的成员」误判成删除。
/// </para>
/// <para>
/// 更新基线的方式：设环境变量 <c>AXIOSNET_REFRESH_API_BASELINE=1</c> 再跑这一条用例，它会重写文件并失败一次
/// （失败是故意的：让你在 diff 里看见自己动了什么，而不是悄悄刷新）。
/// </para>
/// <para>
/// 基线文本里的类型名<b>不带程序集限定</b>（泛型实参递归展开，见 <see cref="ApiSurface"/> 的 <c>TypeName</c>）：
/// 否则 <c>System.Private.CoreLib, Version=…</c> 会进基线，换个运行时或改 <c>rollForward</c> 就整份对不上。
/// </para>
/// </remarks>
public sealed class ApiSurfaceBaselineTests
{
    /// <summary>基线目录名（源码树与输出目录同名）。</summary>
    private const string BaselineFolder = "PublicApi";

    /// <summary>当前 TFM 的基线文件名：用编译期常量而不是运行时探测，基线必须跟着「这份资产」走。</summary>
    private static string BaselineFileName
#if NET8_0_OR_GREATER
        => "PublicAPI.net8.0.txt";
#else
        => "PublicAPI.net6.0.txt";
#endif

    /// <summary>输出目录里的基线（由 csproj 的 <c>CopyToOutputDirectory</c> 复制过去）：比对首选它，不依赖源码树。</summary>
    private static string OutputBaselinePath
        => Path.Combine(AppContext.BaseDirectory, BaselineFolder, BaselineFileName);

    /// <summary>是否有意刷新基线：由环境变量控制，避免日常跑测试顺手改掉基线。</summary>
    private static bool RefreshRequested
        => string.Equals(Environment.GetEnvironmentVariable("AXIOSNET_REFRESH_API_BASELINE"), "1", StringComparison.Ordinal);

    /// <summary>源码树里的基线：只有它会因刷新被重写；不在检出里（例如只跑发布产物）时为 <c>null</c>。</summary>
    private static string? SourceBaselinePath()
        => RepositoryRoot() is { } root
            ? Path.Combine(root, "test", "AxiosNet.Tests", BaselineFolder, BaselineFileName)
            : null;

    /// <summary>
    /// 取一份可读的基线：<b>先源码树</b>（签入的那份才是权威，避免和 bin/ 里的旧副本比对），
    /// 没有源码树时退回输出目录（打包产物里跑测试）；都没有时抛带指引的异常，而不是裸 IOException。
    /// </summary>
    /// <returns>可读的基线文件路径。</returns>
    private static string ExistingBaselinePath()
    {
        if (SourceBaselinePath() is { } source && File.Exists(source))
        {
            return source;
        }

        if (File.Exists(OutputBaselinePath))
        {
            return OutputBaselinePath;
        }

        throw new FileNotFoundException(
            $"找不到公共 API 基线 '{BaselineFileName}'（查过源码树与输出目录）。"
            + "基线随构建复制到输出目录；若在打包产物里跑测试，请确认 csproj 的 "
            + "None Update=\"PublicApi\\PublicAPI.*.txt\" CopyToOutputDirectory 仍生效。",
            OutputBaselinePath);
    }

    /// <summary>
    /// 主用例：实际公共面必须与基线逐行一致。
    /// </summary>
    [Fact]
    public void PublicApiSurface_MatchesTheCheckedInBaseline()
    {
        var actual = ApiSurface.Describe(typeof(AxiosConfig).Assembly);

        if (RefreshRequested)
        {
            // 刷新只能写源码树：写进 bin/ 下次构建就被覆盖，等于骗过了自己。
            var target = SourceBaselinePath()
                ?? throw new InvalidOperationException(
                    $"AXIOSNET_REFRESH_API_BASELINE=1 需要源码树（找不到 AxiosNet.sln）：请在仓库检出里重跑，"
                    + $"或手工把输出目录的 {BaselineFileName} 拷回 test/AxiosNet.Tests/{BaselineFolder}/。");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.Fail(
                $"已写入公共 API 基线：{target}\n"
                + "请用 git diff 逐条确认新增 / 删除的成员都是有意的，然后把 AXIOSNET_REFRESH_API_BASELINE 去掉重跑。");
        }

        var path = ExistingBaselinePath();
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        Assert.Fail(
            $"公共 API 面与基线不一致：{path}\n"
            + "差异（< 基线独有 = 被删 / 改了签名；> 实际独有 = 新增）：\n"
            + ApiSurface.Diff(expected, actual));
    }

    /// <summary>基线不得为空：空文件会让「删光公共 API」这种灾难也变绿。</summary>
    [Fact]
    public void Baseline_IsNotEmptyAndCoversTheKnownEntryPoints()
    {
        var path = ExistingBaselinePath();
        Assert.True(File.Exists(path), $"基线文件不存在：{path}");

        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length > 500, $"基线只有 {lines.Length} 行，疑似生成失败。");
        Assert.Contains(lines, line => line.StartsWith("T:AxiosNet.AxiosClient ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("T:AxiosNet.AxiosConfig ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("T:AxiosNet.AxiosException ", StringComparison.Ordinal));
    }

    /// <summary>从测试程序集位置回溯到仓库根（凭 <c>AxiosNet.sln</c> 认）；不在检出里返回 <c>null</c>。</summary>
    /// <returns>仓库根目录，或 <c>null</c>。</returns>
    private static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AxiosNet.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}

/// <summary>把程序集的公共面拍平成稳定的文本行集合（排序后逐行比对）。</summary>
internal static class ApiSurface
{
    /// <summary>拍平整个程序集的公共面。</summary>
    /// <param name="assembly">要检查的程序集。</param>
    /// <returns>按序号排序、以 <c>\n</c> 连接的文本。</returns>
    public static string Describe(Assembly assembly)
    {
        var lines = new List<string>();

        foreach (var type in assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            lines.Add(DescribeType(type));

            const BindingFlags Visible = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var constructor in type.GetConstructors(Visible).Where(IsVisible))
            {
                lines.Add($"  C:{type.FullName}({Parameters(constructor.GetParameters())})");
            }

            foreach (var property in type.GetProperties(Visible).Where(IsVisible).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                lines.Add($"  P:{type.FullName}.{property.Name}:{Accessors(property)}");
            }

            foreach (var field in type.GetFields(Visible).Where(IsVisible).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                lines.Add($"  F:{type.FullName}.{field.Name}");
            }

            foreach (var @event in type.GetEvents(Visible).Where(IsVisible).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                lines.Add($"  E:{type.FullName}.{@event.Name}");
            }

            foreach (var method in type.GetMethods(Visible)
                         .Where(IsVisible)
                         .Where(IsNotAccessor)
                         .OrderBy(m => m.Name, StringComparer.Ordinal)
                         .ThenBy(m => Parameters(m.GetParameters()), StringComparer.Ordinal))
            {
                lines.Add($"  M:{type.FullName}.{Name(method)}({Parameters(method.GetParameters())})");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>逐行差异（只输出前 80 行，避免失败信息淹没终端）。</summary>
    /// <param name="expected">基线文本。</param>
    /// <param name="actual">实际文本。</param>
    /// <returns>可读的差异摘要。</returns>
    public static string Diff(string expected, string actual)
    {
        var left = new HashSet<string>(expected.Split('\n'), StringComparer.Ordinal);
        var right = new HashSet<string>(actual.Split('\n'), StringComparer.Ordinal);

        var removed = left.Except(right).Where(line => line.Length > 0).OrderBy(line => line, StringComparer.Ordinal).ToList();
        var added = right.Except(left).Where(line => line.Length > 0).OrderBy(line => line, StringComparer.Ordinal).ToList();

        var builder = new StringBuilder();
        foreach (var line in removed.Take(40))
        {
            builder.Append("< ").Append(line).Append('\n');
        }

        foreach (var line in added.Take(40))
        {
            builder.Append("> ").Append(line).Append('\n');
        }

        if (removed.Count + added.Count > 80)
        {
            builder.Append($"… 另有 {removed.Count + added.Count - 80} 行未显示\n");
        }

        return builder.ToString();
    }

    /// <summary>描述一个导出类型：种类 + 全名 + 基类 + 实现的接口（排序）。</summary>
    /// <param name="type">导出类型。</param>
    /// <returns>一行描述。</returns>
    private static string DescribeType(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type.IsAbstract && type.IsSealed ? "static"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";

        // 枚举不带基类 / 接口：`Enum` 自己实现了什么属于 BCL 内部细节（net8 的 Enum 多实现 ISpanFormattable），
        // 两份基线会因此凭噪声分叉；枚举的成员值由下面的 F: 行逐个记录，这里只留种类。
        if (type.IsEnum)
        {
            return $"T:{type.FullName} [enum]";
        }

        var interfaces = type.GetInterfaces()
            .Select(TypeName)
            .OrderBy(name => name, StringComparer.Ordinal);

        var baseType = type.BaseType is { } parent && parent != typeof(object) && parent != typeof(ValueType)
            ? $" : {TypeName(parent)}"
            : string.Empty;

        return $"T:{type.FullName} [{kind}]{baseType} implements {string.Join(",", interfaces)}";
    }

    /// <summary>属性访问器形态：<c>get</c> / <c>get;set</c>（<c>init</c> 也算 set，二进制上只差 modreq）。</summary>
    /// <param name="property">属性。</param>
    /// <returns>访问器串。</returns>
    private static string Accessors(PropertyInfo property)
    {
        var getter = property.GetGetMethod(nonPublic: true);
        var setter = property.GetSetMethod(nonPublic: true);

        var parts = new List<string>(2);
        if (getter is not null && IsVisible(getter))
        {
            parts.Add("get");
        }

        if (setter is not null && IsVisible(setter))
        {
            parts.Add("set");
        }

        return string.Join(";", parts);
    }

    /// <summary>方法名（泛型方法带上元数，避免 <c>Foo</c> 与 <c>Foo&lt;T&gt;</c> 撞名）。</summary>
    /// <param name="method">方法。</param>
    /// <returns>稳定的名字。</returns>
    private static string Name(MethodInfo method)
        => method.IsGenericMethodDefinition ? $"{method.Name}`{method.GetGenericArguments().Length}" : method.Name;

    /// <summary>参数列表（按位置，含 <c>ref</c> / <c>out</c> 前缀）。</summary>
    /// <param name="parameters">参数。</param>
    /// <returns>逗号分隔的类型名。</returns>
    private static string Parameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(parameter =>
        {
            var prefix = parameter.IsOut ? "out "
                : parameter.ParameterType.IsByRef ? "ref "
                : string.Empty;

            var type = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;

            return prefix + TypeName(type);
        }));

    /// <summary>
    /// 稳定类型名：<b>不带程序集限定</b>，泛型实参递归展开。
    /// </summary>
    /// <remarks>
    /// 不能直接拼 <see cref="Type.FullName"/>：构造泛型的 <c>FullName</c> 会把实参写成
    /// <c>System.String, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, …</c>，
    /// 于是程序集版本进了基线 —— 换运行时 / 改 <c>rollForward</c> 整份对不上，
    /// 而且 net6 / net8 两份基线看起来「差很多」，真正的 TFM 差异被版本噪声淹没。
    /// </remarks>
    /// <param name="type">类型。</param>
    /// <returns>形如 <c>System.Collections.Generic.IEnumerable`1&lt;System.String&gt;</c> 的文本。</returns>
    private static string TypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return "!" + type.Name;
        }

        // 数组也必须递归：`KeyValuePair<String,Object>[]` 的 FullName 一样会把实参写成程序集限定名。
        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        // 泛型定义自身的 FullName 不含实参、也不含程序集限定，正是要的那一截。
        var definition = type.GetGenericTypeDefinition();
        var name = definition.FullName ?? definition.Name;
        return type.IsGenericTypeDefinition
            ? name
            : name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    /// <summary>是否对外可见（<c>public</c>；<c>protected</c> 也算，派生类能看见）。</summary>
    /// <param name="method">成员。</param>
    /// <returns>可见返回 <c>true</c>。</returns>
    private static bool IsVisible(MethodBase method)
        => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    /// <summary>字段 / 属性 / 事件的可见性判断。</summary>
    /// <param name="member">成员。</param>
    /// <returns>可见返回 <c>true</c>。</returns>
    private static bool IsVisible(FieldInfo member)
        => member.IsPublic || member.IsFamily || member.IsFamilyOrAssembly;

    /// <summary>属性的可见性（任一访问器可见即算）。</summary>
    /// <param name="property">属性。</param>
    /// <returns>可见返回 <c>true</c>。</returns>
    private static bool IsVisible(PropertyInfo property)
        => (property.GetGetMethod(nonPublic: true) is { } getter && IsVisible(getter))
           || (property.GetSetMethod(nonPublic: true) is { } setter && IsVisible(setter));

    /// <summary>事件可见性。</summary>
    /// <param name="event">事件。</param>
    /// <returns>可见返回 <c>true</c>。</returns>
    private static bool IsVisible(EventInfo @event)
        => (@event.GetAddMethod(nonPublic: true) is { } adder && IsVisible(adder))
           || (@event.GetRemoveMethod(nonPublic: true) is { } remover && IsVisible(remover));

    /// <summary>排除 <c>get_X</c> / <c>set_X</c> / <c>add_X</c> / <c>remove_X</c>：它们已由属性 / 事件行表达。</summary>
    /// <param name="method">方法。</param>
    /// <returns>是访问器返回 <c>false</c>。</returns>
    private static bool IsNotAccessor(MethodInfo method)
        => !method.IsSpecialName;
}
