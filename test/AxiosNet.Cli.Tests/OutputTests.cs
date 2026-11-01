using System.Text;
using System.Text.Json;

namespace AxiosNet.Cli.Tests;

/// <summary>落盘语义：确定性、--check、覆盖保护、失联文件清理与清单。</summary>
public sealed class OutputTests
{
    /// <summary>同一输入连跑两次，文件名与每个文件的字节内容都必须完全一致。</summary>
    [Fact]
    public void GenerationIsByteDeterministic()
    {
        using var first = CliHarness.Run("petstore.json");
        using var second = CliHarness.Run("petstore.json");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.FileNames, second.FileNames);
        foreach (var name in first.FileNames)
        {
            Assert.Equal(first.Read(name), second.Read(name));
        }
    }

    /// <summary>产物未被改动时 --check 返回 0；任何一个文件被追加内容后返回 1。</summary>
    [Fact]
    public void CheckPassesWhenUpToDateAndFailsWhenTampered()
    {
        using var run = CliHarness.Run("petstore.json");

        var fresh = CliHarness.RunSourceInto(CliHarness.Fixture("petstore.json"), run.Directory, "--check");
        Assert.Equal(0, fresh);

        File.AppendAllText(run.PathOf("Models.g.cs"), "// tampered\n");
        var tampered = CliHarness.RunSourceInto(CliHarness.Fixture("petstore.json"), run.Directory, "--check");
        Assert.Equal(1, tampered);
    }

    /// <summary>--check 只读不写：执行后除报告外的生成文件内容一字不变。</summary>
    [Fact]
    public void CheckDoesNotChangeGeneratedFiles()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        var before = run.FileNames
            .Where(name => name != CliRun.ReportName)
            .ToDictionary(name => name, run.Read);

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--check"));

        foreach (var (name, content) in before)
        {
            Assert.Equal(content, run.Read(name));
        }
    }

    /// <summary>不加 --force 时旧文件保留但清单仍追踪；--check 报 stale，--force 才真正删除。</summary>
    [Fact]
    public void ForceRemovesStaleFilesAndCheckTracksThem()
    {
        using var run = CliHarness.Run("petstore.json");
        Assert.True(run.Exists("PetsRequests.g.cs"));

        // 换成另一份文档：旧请求模型文件本次不再生成。
        var withoutForce = CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory);
        Assert.Equal(0, withoutForce);
        Assert.True(run.Exists("PetsRequests.g.cs"));

        // 计划失败后被删的文件仍在清单里，--check 必须继续报 stale。
        var manifest = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        var tracked = manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("PetsRequests.g.cs", tracked);

        Assert.Equal(1, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--check"));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--force"));
        Assert.False(run.Exists("PetsRequests.g.cs"));
        Assert.True(run.Exists("DiagnosticsRequests.g.cs"));
        Assert.Contains("class PingAsyncResponse", run.Read("Models.g.cs"));
        Assert.DoesNotContain("class Pet", run.Read("Models.g.cs"));
        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--check"));
    }

    /// <summary>已有文件被人工改写（无生成标记）时拒绝覆盖，加 --force 才允许。</summary>
    [Fact]
    public void OverwriteProtectionRequiresForce()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        File.WriteAllText(run.PathOf("Models.g.cs"), "// user content\n");

        Assert.Throws<GenerationException>(
            () => CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--force"));
        Assert.Contains("class PingAsyncResponse", run.Read("Models.g.cs"));
    }

    /// <summary>清单记录生成器版本与按序数排序的文件列表，且不把自己算进文件列表。</summary>
    [Fact]
    public void ManifestListsGeneratedFilesSorted()
    {
        using var run = CliHarness.Run("petstore.json");

        using var manifest = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        Assert.StartsWith("axiosnet-gen/", manifest.RootElement.GetProperty("generator").GetString());

        var files = manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(files.OrderBy(name => name, StringComparer.Ordinal), files);
        Assert.Contains("PetsRequests.g.cs", files);
        Assert.Contains("AuthRequests.g.cs", files);
        Assert.Contains("Models.g.cs", files);
        Assert.DoesNotContain("axiosnet-gen.manifest.json", files);
    }

    /// <summary>--dry-run 只打印，输出目录里不应新增或改动任何文件。</summary>
    [Fact]
    public void DryRunPrintsWithoutWriting()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        var before = run.FileNames;

        var exit = CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--dry-run");
        Assert.Equal(0, exit);
        Assert.Equal(before, run.FileNames);
    }

    /// <summary>
    /// 上次清单损坏时不能要求 --force：记 MANIFEST_INVALID 后按首次生成处理，并允许直接覆盖坏清单。
    /// </summary>
    [Fact]
    public void CorruptedManifestIsReportedAndRewritten()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        File.WriteAllText(run.PathOf("axiosnet-gen.manifest.json"), "{ broken");

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));
        Assert.Contains("MANIFEST_INVALID", run.Codes);

        using var manifest = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        Assert.StartsWith("axiosnet-gen/", manifest.RootElement.GetProperty("generator").GetString());
    }

    /// <summary>清单里重复的文件名必须去重，否则 stale 记录与新清单都会出现重复条目。</summary>
    [Fact]
    public void DuplicateManifestEntriesAreDeduplicated()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        File.WriteAllText(
            run.PathOf("axiosnet-gen.manifest.json"),
            "{\n  \"generator\": \"axiosnet-gen/1.0.0\",\n  \"files\": [\"Ghost.g.cs\", \"Ghost.g.cs\"]\n}\n");
        File.WriteAllText(run.PathOf("Ghost.g.cs"), "// <auto-generated />\n// Generated by axiosnet-gen\n");

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));

        using var manifest = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        var files = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "DiagnosticsRequests.g.cs", "Ghost.g.cs", "Models.g.cs" }, files);
    }

    /// <summary>--check / --dry-run 不写任何文件：连 --report 指定的报告也不能落盘。</summary>
    [Fact]
    public void CheckAndDryRunDoNotWriteReport()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        File.Delete(run.PathOf(CliRun.ReportName));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--check"));
        Assert.False(run.Exists(CliRun.ReportName));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--dry-run"));
        Assert.False(run.Exists(CliRun.ReportName));
    }

    /// <summary>清单是合法 JSON 但缺少 files 数组时按损坏处理：报 MANIFEST_INVALID 并重写成带 files 的清单。</summary>
    [Fact]
    public void ManifestWithoutFilesIsReportedAsInvalid()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        File.WriteAllText(
            run.PathOf("axiosnet-gen.manifest.json"),
            "{\n  \"generator\": \"axiosnet-gen/1.0.0\"\n}\n");

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));
        Assert.Contains("MANIFEST_INVALID", run.Codes);

        using var manifest = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        Assert.Equal(JsonValueKind.Array, manifest.RootElement.GetProperty("files").ValueKind);
    }

    /// <summary>紧凑 JSON（无空格）的清单也必须被识别为本工具所有，否则会被误判成用户文件而要求 --force。</summary>
    [Fact]
    public void CompactManifestIsRecognizedAsOwned()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        using var original = JsonDocument.Parse(run.Read("axiosnet-gen.manifest.json"));
        var files = original.RootElement.GetProperty("files").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        File.WriteAllText(
            run.PathOf("axiosnet-gen.manifest.json"),
            JsonSerializer.Serialize(new { generator = "axiosnet-gen/1.0.0", files }));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));
        Assert.DoesNotContain("MANIFEST_INVALID", run.Codes);
    }

    /// <summary>生成文件被加过 BOM 时必须判为过期（--check 返回 1），正常写入还要把它重写成无 BOM。</summary>
    [Fact]
    public void BomInGeneratedFileIsDetectedAndRewritten()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        var path = run.PathOf("Models.g.cs");
        File.WriteAllText(path, run.Read("Models.g.cs"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(1, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--check"));

        Assert.Equal(0, CliHarness.RunSourceInto(CliHarness.Fixture("mini-3.1.yaml"), run.Directory));
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    /// <summary>--report 指向生成产物时必须报用法错误，不能把同名生成文件覆盖成 JSON。</summary>
    [Fact]
    public void ReportPathCannotTargetGeneratedFile()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");
        var target = run.PathOf("DiagnosticsRequests.g.cs");
        var before = run.Read("DiagnosticsRequests.g.cs");

        Assert.Throws<CliUsageException>(() => CliHarness.RunSourceInto(
            CliHarness.Fixture("mini-3.1.yaml"), run.Directory, "--report", target));

        Assert.Equal(before, run.Read("DiagnosticsRequests.g.cs"));
    }
}
