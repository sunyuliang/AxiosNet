using System.Text;
using System.Text.RegularExpressions;

namespace AxiosNet.Tests.Governance;

/// <summary>
/// 注释规范护栏：把「注释只说明这是什么」里可机检的形态固化成用例，<c>dotnet test</c> 直接跑，
/// 不需要 Python 或任何本机脚本。扫描 <c>src/</c>、<c>test/</c>、<c>samples/</c>；
/// 测试夹具工程与元数据载体豁免，见 <see cref="CommentAudit"/>。
/// </summary>
public sealed class CommentAuditTests
{
    private static readonly Lazy<CommentAuditReport> Cached = new(CommentAudit.Scan, isThreadSafe: true);

    /// <summary>可见类型与有返回类型的成员（public / internal）必须有 XML 文档。</summary>
    [Fact]
    public void VisibleTypesAndMembersHaveXmlDocumentation() =>
        AssertClean(Cached.Value.UndocumentedMembers, "缺 XML 文档的可见成员");

    /// <summary>每个方法都要有一行注释，私有方法也不例外。</summary>
    [Fact]
    public void EveryMethodHasLeadingComment() =>
        AssertClean(Cached.Value.UndocumentedMethods, "缺前置注释的方法");

    /// <summary>位置记录的每个参数都要有对应的 <c>&lt;param&gt;</c>。</summary>
    [Fact]
    public void PositionalRecordParametersAreDocumented() =>
        AssertClean(Cached.Value.UndocumentedRecordParameters, "位置记录参数缺 <param>");

    /// <summary>可变状态与 static 字段要有说明。</summary>
    [Fact]
    public void MutableAndStaticFieldsAreDocumented() =>
        AssertClean(Cached.Value.UndocumentedFields, "缺说明的全局状态字段");

    /// <summary>每段 <c>///</c> 块都要有 summary 之类的文档标签。</summary>
    [Fact]
    public void DocBlocksDeclareATag() =>
        AssertClean(Cached.Value.BlocksWithoutSummary, "没有 summary 的文档块");

    /// <summary>文档标签不能是空标签。</summary>
    [Fact]
    public void DocTagsAreNotEmpty() =>
        AssertClean(Cached.Value.EmptyTags, "空标签");

    /// <summary>summary 不能只有一两个字、不能指向上文、不能复述成员名。</summary>
    [Fact]
    public void SummariesCarryInformation() =>
        AssertClean(Cached.Value.HollowSummaries, "无信息注释");

    /// <summary>summary / remarks / 块行数都在上限内。</summary>
    [Fact]
    public void DocBlocksStayWithinLengthLimits() =>
        AssertClean(Cached.Value.VerboseBlocks, "超长注释");

    /// <summary>注释里不留 TODO / FIXME / HACK / XXX。</summary>
    [Fact]
    public void CommentsHaveNoTodoMarkers() =>
        AssertClean(Cached.Value.TodoMarkers, "TODO / FIXME 标记");

    /// <summary>带行内注释的行不超过 140 字符；纯代码长行不在本条范围内。</summary>
    [Fact]
    public void AnnotatedLinesStayWithinColumnLimit() =>
        AssertClean(Cached.Value.LongLines, "带注释的超长行");

    /// <summary>注释不能是纯符号（分隔线除外）。</summary>
    [Fact]
    public void CommentsAreNotPunctuationOnly() =>
        AssertClean(Cached.Value.SymbolOnlyComments, "疑似纯符号注释");

    /// <summary>注释里用反引号引用的标识符必须真的存在于源码中。</summary>
    [Fact]
    public void BacktickedSymbolsExistInSource() =>
        AssertClean(Cached.Value.UnknownBacktickedSymbols, "注释引用了不存在的符号");

    private static void AssertClean(IReadOnlyCollection<string> hits, string what)
    {
        if (hits.Count == 0)
        {
            return;
        }

        var detail = string.Join("\n", hits.Take(20));
        Assert.Fail(what + "：" + hits.Count + " 处（最多列 20 条）\n" + detail);
    }
}

/// <summary>一次注释扫描的命中集合，每个列表对应一条机检规则。定位格式为 <c>相对路径:行号</c>。</summary>
internal sealed class CommentAuditReport
{
    internal List<string> UndocumentedMembers { get; } = new();

    internal List<string> UndocumentedMethods { get; } = new();

    internal List<string> UndocumentedRecordParameters { get; } = new();

    internal List<string> UndocumentedFields { get; } = new();

    internal List<string> BlocksWithoutSummary { get; } = new();

    internal List<string> EmptyTags { get; } = new();

    internal List<string> HollowSummaries { get; } = new();

    internal List<string> VerboseBlocks { get; } = new();

    internal List<string> TodoMarkers { get; } = new();

    internal List<string> LongLines { get; } = new();

    internal List<string> SymbolOnlyComments { get; } = new();

    internal List<string> UnknownBacktickedSymbols { get; } = new();
}

/// <summary>按 12 条规则扫描源码里的 XML 文档与行内注释形态。</summary>
internal static class CommentAudit
{
    /// <summary>单行注释的最大列数。</summary>
    internal const int MaxLineChars = 140;

    /// <summary>summary 的文字上限（去标签、去空白后的字符数）。</summary>
    internal const int MaxSummaryChars = 150;

    /// <summary>remarks 的文字上限（去标签、去空白后的字符数）。</summary>
    internal const int MaxRemarksChars = 250;

    /// <summary>单个 <c>///</c> 块的行数上限；块内含 <c>&lt;example&gt;</c> 时放宽 30 行。</summary>
    internal const int MaxBlockLines = 20;

    private const int DocSearchBudget = 80;

    private static readonly string[] SourceRoots = { "src", "test", "samples" };

    /// <summary>断言脚手架工程：类型是请求模型、假服务与桩处理器，逐属性写 summary 只会复述属性名。</summary>
    private static readonly string[] ExemptPrefixes =
    {
        "test/AxiosNet.Tests/",
        "test/AxiosNet.DependencyInjection.Tests/",
    };

    /// <summary>源生成器烘焙进程序集的元数据载体，属性名即语义。</summary>
    private static readonly HashSet<string> ExemptFiles = new(StringComparer.Ordinal)
    {
        "src/AxiosNet/WebAPI/Internal/MetadataDeclarations.cs",
    };

    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "static", "sealed", "abstract", "partial",
        "readonly", "ref", "unsafe", "new", "file",
    };

    private static readonly string[] SkipPrefixes =
    {
        "#if", "#endif", "#else", "#elif", "#pragma", "#region", "#endregion",
    };

    private static readonly Regex TypeKeyword =
        new(@"\b(class|struct|interface|record|enum|delegate)\b", RegexOptions.CultureInvariant);

    private static readonly Regex TestAttribute =
        new(@"^\[(Fact|Theory|InlineData|MemberData|ClassData|Trait)\b", RegexOptions.CultureInvariant);

    private static readonly Regex MemberDecl = new(
        @"^\s*(?:public|internal)\s+((?:static|readonly|sealed|abstract|partial|new|unsafe|required|async)\s+)*"
        + @"(?:[\w<>\[\],\.\?]+\s+)+\w+\s*(?:\(|=>|\{)",
        RegexOptions.CultureInvariant);

    private static readonly Regex FieldDecl = new(
        @"^(?:private|internal|protected)\s+((?:static\s+|readonly\s+|const\s+|volatile\s+|unsafe\s+)*)"
        + @"([\w<>\[\],\.\? ]+?)\s+(\w+)\s*(?:=(?![=>])|;)",
        RegexOptions.CultureInvariant);

    private static readonly Regex MethodDecl = new(
        @"^\s*(?:public|private|protected|internal)\s+"
        + @"(?:(?:static|async|override|virtual|sealed|abstract|partial|unsafe|extern|new|readonly)\s+)*"
        + @"(?:[\w<>\[\],\.\?]+\s+)+\w+\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex PositionalRecord = new(
        @"^(?:public|private|protected|internal|file)\s+"
        + @"(?:(?:static|sealed|abstract|partial|readonly|unsafe|new)\s+)*"
        + @"record\s+(?:struct\s+|class\s+)?(\w+)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex VisibleMemberDecl =
        new(@"^\s*(?:public|internal)\s", RegexOptions.CultureInvariant);

    private static readonly Regex OverrideWord = new(@"\boverride\b", RegexOptions.CultureInvariant);

    private static readonly Regex PreprocessorBranch =
        new(@"^(?:#if|#else|#elif|#endif)", RegexOptions.CultureInvariant);

    private static readonly Regex InterfaceImpl =
        new(@":\s*[\w\s,<>\.]*\bI[A-Z]\w*", RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredNamePattern =
        new(@"([A-Za-z_]\w*)\s*(?:\{|=>|;|\(|=|$)", RegexOptions.CultureInvariant);

    private static readonly Regex SupportFieldName = new(@"^_[a-z]\w*$", RegexOptions.CultureInvariant);

    private static readonly Regex GroupedField =
        new(@"^(?:private|internal|protected)\s", RegexOptions.CultureInvariant);

    private static readonly Regex StringLiteralDefault = new(@"=\s*[$@]*""", RegexOptions.CultureInvariant);

    private static readonly Regex OpeningDocTag =
        new(@"<(inheritdoc|param|returns|remarks|typeparam|exception)", RegexOptions.CultureInvariant);

    private static readonly Regex EmptyTagPair =
        new(@"<(summary|param|returns|remarks)>\s*</\1>", RegexOptions.CultureInvariant);

    private static readonly Regex SummaryBlock =
        new(@"<summary>(.*?)</summary>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex RemarksBlock =
        new(@"<remarks>(.*?)</remarks>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly Regex PointerWord =
        new(@"^(同上|同前|见上|如前|参见上|同下)", RegexOptions.CultureInvariant);

    private static readonly Regex TodoMarker =
        new(@"\b(TODO|FIXME|HACK|XXX)\b", RegexOptions.CultureInvariant);

    private static readonly Regex DividerLine = new(@"^[-=─═*~_]{6,}$", RegexOptions.CultureInvariant);

    private static readonly Regex WordOrChinese =
        new(@"[A-Za-z]{3,}|[\u4e00-\u9fff]", RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierWord =
        new(@"\b[A-Za-z_][A-Za-z0-9_]{2,}\b", RegexOptions.CultureInvariant);

    private static readonly Regex BacktickedSymbol =
        new(@"`([A-Za-z_][A-Za-z0-9_.<>]{2,})`", RegexOptions.CultureInvariant);

    private static readonly Regex ParamAttribute = new(@"^\[[^\]]*\]\s*", RegexOptions.CultureInvariant);

    private static readonly Regex DefaultValueTail = new(@"=[^,]*$", RegexOptions.CultureInvariant);

    private static readonly Regex TrailingWord = new(@"(\w+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>扫描仓库源码树，返回 12 条规则各自的命中列表。</summary>
    /// <returns>命中集合；全部为空表示通过。</returns>
    internal static CommentAuditReport Scan()
    {
        var files = LoadFiles(RepositoryRoot());
        if (files.Count == 0)
        {
            throw new InvalidOperationException("没有扫到任何源文件，检查仓库根与 src / test / samples 目录。");
        }

        var report = new CommentAuditReport();

        ScanMissingDocs(files, report.UndocumentedMembers);
        ScanUndocumentedMethods(files, report.UndocumentedMethods);
        ScanRecordParameters(files, report.UndocumentedRecordParameters);
        ScanUndocumentedFields(files, report.UndocumentedFields);
        ScanDocBlocks(files, report);
        ScanInlineComments(files, report);
        ScanBacktickedSymbols(files, report.UnknownBacktickedSymbols);

        return report;
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

        throw new InvalidOperationException("找不到仓库根（AxiosNet.sln），无法扫描源码注释。");
    }

    private static List<SourceFile> LoadFiles(string root)
    {
        var files = new List<SourceFile>();

        foreach (var name in SourceRoots)
        {
            var baseDirectory = Path.Combine(root, name);
            if (!Directory.Exists(baseDirectory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(baseDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Contains("obj") || segments.Contains("bin"))
                {
                    continue;
                }

                var relative = Relative(root, path);
                if (relative.EndsWith(".g.cs", StringComparison.Ordinal)
                    || ExemptPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal))
                    || ExemptFiles.Contains(relative))
                {
                    continue;
                }

                files.Add(new SourceFile(relative, File.ReadAllLines(path)));
            }
        }

        return files.OrderBy(static file => file.Relative, StringComparer.Ordinal).ToList();
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void ScanMissingDocs(List<SourceFile> files, List<string> hits)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;
            var attributes = AttributeLineMask(lines);
            var depth = 0;
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var stack = new List<(int Depth, string? Access, bool Impl)>();

            for (var i = 0; i < lines.Length; i++)
            {
                var code = SplitComment(lines[i]).Code;
                var visible = stack.All(static frame => frame.Access is not null);
                var inInterfaceImpl = stack.Any(static frame => frame.Impl);
                var declaration = TypeDecl(code);
                var need = false;

                if (declaration is not null)
                {
                    need = declaration.Value.Access is not null && !declaration.Value.Partial && visible;
                }
                else if (VisibleMemberDecl.IsMatch(code))
                {
                    need = visible
                        && !OverrideWord.IsMatch(code)
                        && DeclaresMember(code)
                        && !(inInterfaceImpl && code.TrimStart().StartsWith("public", StringComparison.Ordinal))
                        && !IsTestMember(lines, i);
                }

                if (need)
                {
                    var (hasDoc, twin, name) = DocumentationState(lines, attributes, i);
                    if (!hasDoc && !(twin && name is not null && reported.Contains(name)))
                    {
                        hits.Add(Location(file.Relative, i) + "  " + Head(lines[i].Trim(), 90));
                        if (name is not null)
                        {
                            reported.Add(name);
                        }
                    }
                }

                var opens = code.Count(static c => c == '{');
                var closes = code.Count(static c => c == '}');
                if (declaration is not null)
                {
                    stack.Add((depth, declaration.Value.Access, InterfaceImpl.IsMatch(code)));
                }

                depth += opens - closes;

                if (declaration is null)
                {
                    while (stack.Count > 0 && depth <= stack[^1].Depth)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
        }
    }

    /// <summary>向上找文档：跳过空行、特性行与预处理指令，返回是否命中、是否同属一个 <c>#if/#else</c> 分支的兄弟声明。</summary>
    private static (bool HasDoc, bool Twin, string? Name) DocumentationState(string[] lines, bool[] attributes, int index)
    {
        var name = DeclaredName(SplitComment(lines[index]).Code);
        var crossed = 0;
        var branch = false;
        var twin = false;
        var budget = DocSearchBudget;

        for (var j = index - 1; j >= 0 && budget > 0; j--)
        {
            var previous = lines[j].Trim();
            budget--;

            if (previous.Length == 0)
            {
                continue;
            }

            if (previous.StartsWith("///", StringComparison.Ordinal))
            {
                return (crossed == 0, twin, name);
            }

            if (previous.StartsWith("//", StringComparison.Ordinal))
            {
                return (true, twin, name);
            }

            if (PreprocessorBranch.IsMatch(previous))
            {
                branch = true;
                continue;
            }

            if (attributes[j] || SkipPrefixes.Any(prefix => previous.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (MemberDecl.IsMatch(previous) || FieldDecl.IsMatch(previous))
            {
                if (branch && name is not null && DeclaredName(previous) == name)
                {
                    twin = true;
                    continue;
                }

                branch = false;
                crossed++;
                continue;
            }

            return (false, twin, name);
        }

        return (false, twin, name);
    }

    private static void ScanUndocumentedMethods(List<SourceFile> files, List<string> hits)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;
            var attributes = AttributeLineMask(lines);

            for (var i = 0; i < lines.Length; i++)
            {
                var (code, comment) = SplitComment(lines[i]);
                if (comment is not null || !MethodDecl.IsMatch(code) || TypeKeyword.IsMatch(code))
                {
                    continue;
                }

                if (IsTestMember(lines, i) || CommentAbove(lines, attributes, i))
                {
                    continue;
                }

                hits.Add(Location(file.Relative, i) + "  " + Head(lines[i].Trim(), 90));
            }
        }
    }

    private static bool CommentAbove(string[] lines, bool[] attributes, int index)
    {
        var budget = DocSearchBudget;

        for (var j = index - 1; j >= 0 && budget > 0; j--)
        {
            var previous = lines[j].Trim();
            budget--;

            if (previous.Length == 0)
            {
                continue;
            }

            if (previous.StartsWith("///", StringComparison.Ordinal) || previous.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }

            if (attributes[j] || SkipPrefixes.Any(prefix => previous.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            return false;
        }

        return false;
    }

    private static void ScanRecordParameters(List<SourceFile> files, List<string> hits)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;

            for (var i = 0; i < lines.Length; i++)
            {
                var match = PositionalRecord.Match(lines[i].Trim());
                if (!match.Success)
                {
                    continue;
                }

                var names = RecordParameterNames(lines, i);
                if (names.Count == 0)
                {
                    continue;
                }

                var documentation = new StringBuilder();
                var collected = false;
                for (var j = i - 1; j >= 0; j--)
                {
                    var previous = lines[j].Trim();
                    if (!previous.StartsWith("///", StringComparison.Ordinal))
                    {
                        if (previous.Length == 0 && collected)
                        {
                            continue;
                        }

                        break;
                    }

                    documentation.Append(previous).Append('\n');
                    collected = true;
                }

                var joined = documentation.ToString();
                foreach (var name in names)
                {
                    if (joined.Contains("name=\"" + name + "\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hits.Add(Location(file.Relative, i) + "  " + match.Groups[1].Value + "(" + name + ")");
                }
            }
        }
    }

    private static List<string> RecordParameterNames(string[] lines, int index)
    {
        var text = new StringBuilder();
        var depth = 0;

        for (var k = index; k < Math.Min(index + 60, lines.Length); k++)
        {
            text.Append('\n').Append(lines[k]);
            depth += lines[k].Count(static c => c == '(') - lines[k].Count(static c => c == ')');
            if (depth <= 0)
            {
                break;
            }
        }

        var source = text.ToString();
        var start = source.IndexOf('(', StringComparison.Ordinal);
        if (start < 0)
        {
            return new List<string>();
        }

        depth = 0;
        var angle = 0;
        var previousChar = '\0';
        var inner = new StringBuilder();

        for (var k = start; k < source.Length; k++)
        {
            var ch = source[k];

            if (ch == '(')
            {
                depth++;
                if (depth == 1)
                {
                    previousChar = ch;
                    continue;
                }
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            else if (ch == '<')
            {
                angle++;
            }
            else if (ch == '>')
            {
                if (previousChar != '=')
                {
                    angle = Math.Max(0, angle - 1);
                }
            }
            else if (ch == ',' && depth == 1 && angle == 0)
            {
                inner.Append('\0');
                previousChar = ch;
                continue;
            }

            if (depth >= 1)
            {
                inner.Append(ch);
            }

            previousChar = ch;
        }

        var names = new List<string>();
        foreach (var rawPart in inner.ToString().Split('\0'))
        {
            var part = ParamAttribute.Replace(rawPart.Trim(), string.Empty);
            part = DefaultValueTail.Replace(part, string.Empty).Trim();
            var match = TrailingWord.Match(part);
            if (match.Success)
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    private static void ScanUndocumentedFields(List<SourceFile> files, List<string> hits)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;

            for (var i = 0; i < lines.Length; i++)
            {
                var code = SplitComment(lines[i]).Code.Trim();
                var match = FieldDecl.Match(code);
                if (!match.Success)
                {
                    continue;
                }

                var modifiers = match.Groups[1].Value;
                var name = match.Groups[3].Value;
                var isStatic = modifiers.Contains("static", StringComparison.Ordinal);

                if (modifiers.Contains("readonly", StringComparison.Ordinal) && !isStatic)
                {
                    continue;
                }

                if (modifiers.Contains("const", StringComparison.Ordinal) && StringLiteralDefault.IsMatch(lines[i]))
                {
                    continue;
                }

                if (IsSupportField(name, lines))
                {
                    continue;
                }

                var documented = false;
                for (var j = i - 1; j >= 0; j--)
                {
                    var previous = lines[j].Trim();
                    if (previous.Length == 0 || previous.StartsWith("[", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (previous.StartsWith("//", StringComparison.Ordinal))
                    {
                        documented = true;
                        break;
                    }

                    if (GroupedField.IsMatch(previous) || previous.EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    break;
                }

                if (!documented)
                {
                    hits.Add(Location(file.Relative, i) + "  " + Head(lines[i].Trim(), 90));
                }
            }
        }
    }

    private static bool IsSupportField(string name, string[] lines)
    {
        if (!SupportFieldName.IsMatch(name))
        {
            return false;
        }

        var property = char.ToUpperInvariant(name[1]) + name[2..];
        var pattern = new Regex(
            @"^\s*(?:public|internal)\s[^=;]*\b" + Regex.Escape(property) + @"\b\s*(?:\{|=>|$)",
            RegexOptions.CultureInvariant);

        return lines.Any(line => pattern.IsMatch(line));
    }

    private static void ScanDocBlocks(List<SourceFile> files, CommentAuditReport report)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;
            var i = 0;

            while (i < lines.Length)
            {
                if (!lines[i].Trim().StartsWith("///", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                var start = i;
                var block = new List<string>();

                while (i < lines.Length && lines[i].Trim().StartsWith("///", StringComparison.Ordinal))
                {
                    block.Add(lines[i].Trim()[3..].Trim());
                    i++;
                }

                var joined = string.Join(" ", block.Where(static line => line.Length > 0));
                var location = file.Relative + ":" + (start + 1);

                if (!joined.Contains("<summary>", StringComparison.Ordinal) && !OpeningDocTag.IsMatch(joined))
                {
                    report.BlocksWithoutSummary.Add(location + "  " + Head(joined, 80));
                }

                if (EmptyTagPair.IsMatch(joined))
                {
                    report.EmptyTags.Add(location);
                }

                var summary = SummaryBlock.Match(joined);
                var remarks = RemarksBlock.Match(joined);

                if (summary.Success)
                {
                    report.HollowSummaries.AddRange(HollowSummary(lines, location, summary.Groups[1].Value, i));
                }

                var parts = new List<string>();
                if (summary.Success && PlainDoc(summary.Groups[1].Value).Length > MaxSummaryChars)
                {
                    parts.Add("summary " + PlainDoc(summary.Groups[1].Value).Length + " 字");
                }

                if (remarks.Success && PlainDoc(remarks.Groups[1].Value).Length > MaxRemarksChars)
                {
                    parts.Add("remarks " + PlainDoc(remarks.Groups[1].Value).Length + " 字");
                }

                var limit = MaxBlockLines + (joined.Contains("<example>", StringComparison.Ordinal) ? 30 : 0);
                if (block.Count > limit)
                {
                    parts.Add(block.Count + " 行");
                }

                if (parts.Count > 0)
                {
                    report.VerboseBlocks.Add(location + "  " + string.Join(" / ", parts));
                }
            }
        }
    }

    private static IEnumerable<string> HollowSummary(string[] lines, string location, string body, int index)
    {
        var text = PlainDoc(body);
        var member = MemberAfter(lines, index);

        if (text.Length <= 3)
        {
            return new[] { location + "  summary「" + text + "」" };
        }

        if (PointerWord.IsMatch(text))
        {
            return new[] { location + "  summary 指向上文「" + Head(text, 30) + "」" };
        }

        if (member is not null && TrimTrailingPunctuation(text) == member)
        {
            return new[] { location + "  summary 与成员名重复「" + member + "」" };
        }

        return Array.Empty<string>();
    }

    private static string? MemberAfter(string[] lines, int index)
    {
        for (var k = index; k < Math.Min(index + 6, lines.Length); k++)
        {
            var candidate = lines[k].Trim();
            if (candidate.Length == 0
                || candidate.StartsWith("#", StringComparison.Ordinal)
                || candidate.StartsWith("[", StringComparison.Ordinal)
                || candidate.StartsWith("//", StringComparison.Ordinal)
                || candidate.StartsWith("}", StringComparison.Ordinal))
            {
                continue;
            }

            return DeclaredName(candidate);
        }

        return null;
    }

    private static void ScanInlineComments(List<SourceFile> files, CommentAuditReport report)
    {
        foreach (var file in files)
        {
            var lines = file.Lines;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                var comment = SplitComment(line).Comment;
                if (comment is null)
                {
                    continue;
                }

                var location = Location(file.Relative, i);

                if (TodoMarker.IsMatch(comment))
                {
                    report.TodoMarkers.Add(location + "  " + Head(comment, 80));
                }

                if (line.Length > MaxLineChars)
                {
                    report.LongLines.Add(location + "  len=" + line.Length);
                }

                if (!DividerLine.IsMatch(comment) && !WordOrChinese.IsMatch(comment))
                {
                    report.SymbolOnlyComments.Add(location + "  " + Head(comment, 60));
                }
            }
        }
    }

    private static void ScanBacktickedSymbols(List<SourceFile> files, List<string> hits)
    {
        var corpus = new StringBuilder();
        foreach (var file in files)
        {
            foreach (var line in file.Lines)
            {
                corpus.Append(SplitComment(line).Code).Append('\n');
            }
        }

        var defined = IdentifierWord.Matches(corpus.ToString())
            .Select(static match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var file in files)
        {
            for (var i = 0; i < file.Lines.Length; i++)
            {
                var comment = SplitComment(file.Lines[i]).Comment;
                if (comment is null)
                {
                    continue;
                }

                foreach (Match match in BacktickedSymbol.Matches(comment))
                {
                    var symbol = match.Groups[1].Value;
                    var root = symbol.Split('.')[0].Split('<')[0];
                    if (!defined.Contains(root))
                    {
                        hits.Add("`" + symbol + "` @ " + Location(file.Relative, i));
                    }
                }
            }
        }
    }

    private static (string? Access, bool Partial)? TypeDecl(string code)
    {
        var match = TypeKeyword.Match(code);
        if (!match.Success)
        {
            return null;
        }

        var words = code[..match.Index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(static word => !ModifierWords.Contains(word)))
        {
            return null;
        }

        var access = words.Contains("public") ? "public" : words.Contains("internal") ? "internal" : null;
        return (access, words.Contains("partial"));
    }

    private static bool DeclaresMember(string code) => MemberDecl.IsMatch(code);

    private static bool IsTestMember(string[] lines, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            var previous = lines[j].Trim();
            if (previous.Length == 0)
            {
                continue;
            }

            if (!previous.StartsWith("[", StringComparison.Ordinal))
            {
                return false;
            }

            if (TestAttribute.IsMatch(previous))
            {
                return true;
            }
        }

        return false;
    }

    private static bool[] AttributeLineMask(string[] lines)
    {
        var mask = new bool[lines.Length];
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (depth > 0)
            {
                mask[i] = true;
                depth = Math.Max(0, depth + BracketBalance(line));
            }
            else if (line.StartsWith("[", StringComparison.Ordinal))
            {
                mask[i] = true;
                depth = Math.Max(0, BracketBalance(line));
            }
        }

        return mask;
    }

    private static int BracketBalance(string line) =>
        line.Count(static c => c == '[') + line.Count(static c => c == '(')
        - line.Count(static c => c == ']') - line.Count(static c => c == ')');

    private static string? DeclaredName(string line)
    {
        var match = DeclaredNamePattern.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>把一行拆成「抹掉字符串字面量的代码」与「行内注释正文」；字符串内的 <c>//</c> 不算注释起点。</summary>
    private static (string Code, string? Comment) SplitComment(string line)
    {
        var code = new StringBuilder(line.Length);
        var quote = '\0';
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];

            if (quote != '\0')
            {
                if (ch == '\\' && quote == '"')
                {
                    i += 2;
                    continue;
                }

                if (ch == quote)
                {
                    quote = '\0';
                }

                i++;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                code.Append(' ');
                i++;
                continue;
            }

            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return (code.ToString(), line[(i + 2)..].Trim());
            }

            code.Append(ch);
            i++;
        }

        return (code.ToString(), null);
    }

    /// <summary>去掉 XML 标签与空白，只留文字，用于判空与量长度。</summary>
    private static string PlainDoc(string text) => Whitespace.Replace(Tag.Replace(text, string.Empty), string.Empty);

    private static string TrimTrailingPunctuation(string text) => text.TrimEnd('。', '.', '(', ')', '（', '）');

    private static string Location(string relative, int index) => relative + ":" + (index + 1);

    private static string Head(string text, int limit) => text.Length <= limit ? text : text[..limit];

    private readonly record struct SourceFile(string Relative, string[] Lines);
}
