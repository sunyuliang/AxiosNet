using System.Text.RegularExpressions;

namespace AxiosNet.Tests.Architecture;

/// <summary>
/// 架构护栏：<c>WebAPI/</c> 是可整体删除的扩展（删掉它，核心与原始 HTTP 照样能用），所以核心不得反向引用它。
/// 这条不变量靠「目录约定 + 人工 review」守不住：任何人写一句 <c>using AxiosNet.WebAPI.Internal;</c> 就破坏了它，
/// 而编译、测试、AOT 都照样通过。这里把它变成可执行断言。
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
    /// 核心代码里不得出现 WebAPI 专属概念（声明式绑定 / 门面类型）。这条防的是「字段或标识符」级回归：
    /// 编译与既有测试都发现不了，但拆包时会立刻变成硬依赖。
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

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (DeclarativeIdentifier.IsMatch(trimmed) || ApiClientIdentifier.IsMatch(trimmed))
                {
                    offenders.Add($"{relative}: {trimmed}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "核心代码不得出现 WebAPI 专属标识符（Declarative* / ApiClient）：" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>配置对象上也不得出现声明式专属成员：反射面比源码扫描更严，能拦住编译后新增的字段。</summary>
    [Fact]
    public void CoreConfigSurfaceHasNoDeclarativeMembers()
    {
        var offenders = typeof(AxiosNet.AxiosConfig)
            .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(member => member.Name)
            .Where(name => DeclarativeIdentifier.IsMatch(name) || ApiClientIdentifier.IsMatch(name))
            .ToList();

        Assert.True(offenders.Count == 0, "AxiosConfig 不得承载 WebAPI 专属语义：" + string.Join("、", offenders));
    }

    private static readonly Regex DeclarativeIdentifier = new(@"\bDeclarative\w*", RegexOptions.CultureInvariant);
    private static readonly Regex ApiClientIdentifier = new(@"\bApiClient\b", RegexOptions.CultureInvariant);

    /// <summary>代码行（去掉注释）里出现 <c>AxiosNet.WebAPI</c> 即算引用；注释里的说明文字不算。</summary>
    private static bool ReferencesWebApi(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
            {
                continue;
            }

            if (trimmed.Contains("AxiosNet.WebAPI", StringComparison.Ordinal)
                || WebApiTypeReference.IsMatch(trimmed))
            {
                return true;
            }
        }

        return false;
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
