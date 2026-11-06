using System.Reflection;
using System.Text.RegularExpressions;

namespace AxiosNet.Tests.Architecture;

/// <summary>
/// 架构护栏：<c>WebAPI/</c> 是可整体删除的扩展（删掉它，核心与原始 HTTP 照样能用），
/// 依赖方向只能是 <c>WebAPI → 内核</c>，内核不得反向引用它。
/// <para>
/// 这里压三层证据：
/// ① 核心源码不出现 <c>AxiosNet.WebAPI</c> 命名空间；
/// ② 核心源码不出现 WebAPI 概念词（防「把类型整族搬进内核」绕过命名空间检查）；
/// ③ 核心 csproj 保留「整体排除 WebAPI」的构建开关，且不引用 WebAPI 项目。
/// 真正以元数据引用为准的主力护栏在 <see cref="WebApiReferenceGraphTests"/>；
/// 「WebAPI 可整体删除」的实证由 <c>eng/build-without-webapi.ps1</c> 与 CI 的独立构建负责。
/// </para>
/// </summary>
public class WebApiRemovabilityTests
{
    // 只匹配命名空间前缀不够：① 核心文件写在 namespace AxiosNet.* 里时可直接写 WebAPI.ApiClient
    // （父命名空间自动可见，无需 using）；② 块级命名空间里可以写 using WebAPI;（文件级 using 不行，
    // 它按全局命名空间解析，所以这个洞只出现在 namespace { ... } 块内，同样是可编译的真引用）。
    private static readonly Regex WebApiTypeReference = new(@"\bWebAPI\.[A-Za-z_]|\bWebAPI\s*;", RegexOptions.CultureInvariant);

    [Fact]
    public void CoreDoesNotReferenceTheWebApiExtension()
    {
        var libraryRoot = Path.Combine(RepositoryRoot(), "src", "AxiosNet");
        var extensionRoot = Path.Combine(libraryRoot, "WebAPI");

        // 扩展目录本身必须存在且非空，否则下面的排除集为空、断言会空转成假绿。
        var extensionFiles = EnumerateSources(extensionRoot).ToList();
        Assert.True(extensionFiles.Count >= 10, $"WebAPI/ 下只找到 {extensionFiles.Count} 个源文件，护栏失效");

        var offenders = new List<string>();
        foreach (var file in EnumerateSources(libraryRoot))
        {
            var relative = Path.GetRelativePath(libraryRoot, file).Replace('\\', '/');
            if (relative.StartsWith("WebAPI/", StringComparison.Ordinal))
            {
                continue;
            }

            if (ReferencesWebApi(file))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "核心不得引用 WebAPI 扩展（删掉 WebAPI/ 后核心必须仍能编译）：" + string.Join("、", offenders));
    }

    /// <summary>
    /// 核心代码里不得出现 WebAPI 概念词。这条防的是「字段或标识符」级回归——把类型整族搬进内核后，
    /// 命名空间扫描会漏，编译与既有测试也发现不了，但整体排除 WebAPI 目录后它会立刻变成硬依赖。
    /// <para>清单只列 WebAPI 独有的概念词，并按词边界精确匹配；<c>Client</c>、<c>Error</c>、<c>Credential</c> 这类
    /// 中性同形词不列，避免误伤内核自己的类型。注释与字符串字面量里的说明文字不计。</para>
    /// </summary>
    [Fact]
    public void CoreDoesNotCarryWebApiOnlyConcepts()
    {
        var libraryRoot = Path.Combine(RepositoryRoot(), "src", "AxiosNet");
        var offenders = new List<string>();
        foreach (var file in EnumerateSources(libraryRoot))
        {
            var relative = Path.GetRelativePath(libraryRoot, file).Replace('\\', '/');
            if (relative.StartsWith("WebAPI/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in EnumerateCodeLines(file))
            {
                if (WebApiConcept.IsMatch(line))
                {
                    offenders.Add($"{relative}: {line}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "核心代码不得出现 WebAPI 专属概念词（" + WebApiConceptOnList + "）：" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>配置对象上也不得出现声明式专属成员：反射面比源码扫描更严，能拦住编译后新增的字段。</summary>
    [Fact]
    public void CoreConfigSurfaceHasNoDeclarativeMembers()
    {
        var offenders = typeof(AxiosNet.AxiosConfig)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(member => member.Name)
            .Where(name => WebApiConcept.IsMatch(name))
            .ToList();

        Assert.True(offenders.Count == 0, "AxiosConfig 不得承载 WebAPI 专属语义：" + string.Join("、", offenders));
    }

    /// <summary>
    /// 项目依赖方向：核心 csproj 不得引用 WebAPI 项目，也不得按文件把 WebAPI 源码编进核心程序集；
    /// 同时必须保留 <c>AxiosNetOmitWebApi</c> 开关与 <c>&lt;Compile Remove="WebAPI/**/*.cs"&gt;</c>，
    /// 否则「WebAPI 可整体删除」就失去可执行的实证入口。
    /// </summary>
    [Fact]
    public void CoreProjectKeepsOmitWebApiSwitchAndNoWebApiReference()
    {
        var csprojPath = Path.Combine(RepositoryRoot(), "src", "AxiosNet", "AxiosNet.csproj");
        Assert.True(File.Exists(csprojPath), $"找不到核心项目文件：{csprojPath}");

        var text = File.ReadAllText(csprojPath);
        var projectReferences = Regex
            .Matches(text, @"<ProjectReference\s+[^>]*Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToList();
        var compileIncludes = Regex
            .Matches(text, @"<Compile\s+[^>]*Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.DoesNotContain(projectReferences, path => path.Contains("WebAPI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(compileIncludes, path => path.Contains("WebAPI", StringComparison.OrdinalIgnoreCase));

        // 排除开关必须存在，且指向 WebAPI 源码目录（属性名变化时这条会红，提醒同步 eng 脚本）。
        Assert.Contains("AxiosNetOmitWebApi", text, StringComparison.Ordinal);
        Assert.Matches(@"Compile\s+Remove\s*=\s*""WebAPI/\*\*/\*\.cs""", text.Replace("\\", "/"));
    }

    // WebAPI 概念清单：整词 + 可选后缀（类型族），避免「ApiErrorContext」这类派生拼写漏网。
    private static readonly Regex WebApiConcept = new(
        @"\b(ApiClient|Declarative\w*|ApiCredential\w*|ApiError\w*|ApiInterfaceInvocation\w*|AllowAnonymous\w*|BindingPlan\w*)\b",
        RegexOptions.CultureInvariant);

    private const string WebApiConceptOnList =
        "ApiClient / Declarative* / ApiCredential* / ApiError* / ApiInterfaceInvocation* / AllowAnonymous* / BindingPlan*";

    /// <summary>代码行（去掉注释与字符串字面量）里出现 <c>AxiosNet.WebAPI</c> 即算引用。</summary>
    private static bool ReferencesWebApi(string file)
    {
        foreach (var line in EnumerateCodeLines(file))
        {
            if (line.Contains("AxiosNet.WebAPI", StringComparison.Ordinal)
                || WebApiTypeReference.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 枚举「代码部分」：跳过 <c>//</c> 与 <c>&lt;summary&gt;</c> 注释行，并去掉双引号内的字面量，
    /// 免得错误文案里出现的概念词被误判（字符串不构成对扩展的类型依赖）。
    /// </summary>
    private static IEnumerable<string> EnumerateCodeLines(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*')
                || trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            yield return StripStringLiterals(line);
        }
    }

    /// <summary>把 <c>"..."</c> 与 <c>$"..."</c> 的内容换成空串；转义引号 <c>\"</c> 不截断。</summary>
    private static string StripStringLiterals(string line)
    {
        var builder = new System.Text.StringBuilder(line.Length);
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (inString)
            {
                if (current == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateSources(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AxiosNet.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根（AxiosNet.sln），无法扫描源码目录。");
    }
}
