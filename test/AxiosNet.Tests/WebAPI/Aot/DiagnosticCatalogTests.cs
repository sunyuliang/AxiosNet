using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// AXA 诊断目录的护栏：一个编号只对应一个语义、编号连续。
/// 编号重复会让 <c>#pragma warning disable AXA00x</c> 连带关掉不该关的一类问题；缺号会让 README 表格与实现漂移。
/// </summary>
public sealed class DiagnosticCatalogTests
{
    private static readonly IReadOnlyList<DiagnosticDescriptor> Catalog = typeof(AxiosNet.SourceGen.RequestModelGenerator)
        .Assembly
        .GetTypes()
        .Where(static type => type.Name == "Diagnostics")
        .SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Select(static field => field.GetValue(null))
        .OfType<DiagnosticDescriptor>()
        .ToList();

    [Fact]
    public void EveryDiagnosticIdMapsToExactlyOneDescriptor()
    {
        Assert.NotEmpty(Catalog);

        var duplicates = Catalog
            .GroupBy(static descriptor => descriptor.Id)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key + " x" + group.Count().ToString(CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(duplicates.Count == 0, "重复的诊断编号：" + string.Join("、", duplicates));
    }

    [Fact]
    public void DiagnosticIdsAreContiguous()
    {
        var numbers = Catalog
            .Select(static descriptor => int.Parse(descriptor.Id[3..], CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(static number => number)
            .ToList();

        var missing = Enumerable.Range(numbers[0], numbers[^1] - numbers[0] + 1).Except(numbers).ToList();

        Assert.True(
            missing.Count == 0,
            "缺号：" + string.Join("、", missing.Select(static number => "AXA" + number.ToString("000", CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void EveryDescriptorIsComplete()
    {
        foreach (var descriptor in Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Title.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.MessageFormat.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.HelpLinkUri));
            Assert.Equal("AxiosNet.SourceGen", descriptor.Category);
        }
    }

    /// <summary>
    /// 占位符必须是 <c>{0}..{n-1}</c> 连续：中间缺号（例如把 <c>{1}</c> 改成 <c>{2}</c> 却没补 <c>{1}</c>）会让
    /// <c>string.Format</c> 抛 <see cref="FormatException"/>，Roslyn 兜底后把原文 <c>{2}</c> 直接显示给用户。
    /// 同一个编号重复出现（<c>{0} … {0}</c>）是合法的。
    /// </summary>
    [Fact]
    public void MessagePlaceholdersAreContiguous()
    {
        foreach (var descriptor in Catalog)
        {
            var indices = PlaceholderPattern
                .Matches(descriptor.MessageFormat.ToString())
                .Select(static match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(static index => index)
                .ToList();

            if (indices.Count == 0)
            {
                continue;
            }

            Assert.True(
                indices.SequenceEqual(Enumerable.Range(0, indices.Count)),
                descriptor.Id + " 的占位符不连续：" + string.Join("、", indices.Select(static index => "{" + index + "}")));
        }
    }

    /// <summary>
    /// 真实产生的诊断消息里不能残留 <c>{n}</c>：调用点少传一个实参就是这样暴露的
    /// （Roslyn 在格式化失败时原样返回 MessageFormat，用户看到的是「接口 {1} 无法…」）。
    /// </summary>
    internal static void AssertNoUnrenderedPlaceholders(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = diagnostic.GetMessage();
            Assert.True(
                !PlaceholderPattern.IsMatch(message),
                diagnostic.Id + " 的消息里有未展开的占位符：" + message);
        }
    }

    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// 双语 README 的诊断表是对外契约：编号少一行、级别写错一档，用户就会按错误的方式关警告或排查。
    /// 表里每一行的「编号 / 级别」必须与 <see cref="Diagnostics"/> 完全一致。
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("README.en.md")]
    public void ReadmeDiagnosticTableMatchesDescriptors(string fileName)
    {
        var documented = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), fileName)))
        {
            if (!line.StartsWith("| `AXA", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|');
            documented[cells[1].Trim().Trim('`')] = cells[2].Trim();
        }

        Assert.Equal(Catalog.Count, documented.Count);

        foreach (var descriptor in Catalog)
        {
            Assert.True(documented.TryGetValue(descriptor.Id, out var severity), fileName + " 缺少 " + descriptor.Id);
            Assert.Equal(descriptor.DefaultSeverity.ToString(), severity);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AxiosNet.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根（AxiosNet.sln），无法定位 README。");
    }
}
