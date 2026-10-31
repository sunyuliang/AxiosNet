#!/usr/bin/env bash
#
# 包消费 smoke：证明「只引用 AxiosNet NuGet 包、不额外引用生成器」的全新项目
# 也能在 Native AOT 下拿到元数据（模型绑定 + 接口代理 + STJ 源生成）。
#
# 用法：
#   eng/aot-package-smoke.sh                # 按当前 OS/架构推断 RID
#   RID=linux-x64 eng/aot-package-smoke.sh  # 指定 RID
#
# 每次运行落在 artifacts/aot-package-smoke-<时间戳>-<pid>/（已被 .gitignore 忽略）；
# 结束不删，方便回看失败现场。
#
# 依赖：dotnet 8 SDK、bash、python3（解压 nupkg 断言内容）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
CONFIG="${CONFIG:-Release}"

# Git Bash 下把 MSYS 路径转成 Windows 原生路径，dotnet / msbuild 才认。
if command -v cygpath >/dev/null 2>&1; then
    ROOT="$(cygpath -w "$ROOT")"
fi

# 每次用独立目录：不删旧目录，避免在受限环境里触发删除保护，也方便回看失败现场。
WORK="$ROOT/artifacts/aot-package-smoke-$(date +%s)-$$"

if [ -z "${RID:-}" ]; then
    case "$(uname -s)" in
        Darwin) RID="osx-$([ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64)" ;;
        Linux) RID="linux-$([ "$(uname -m)" = "aarch64" ] && echo arm64 || echo x64)" ;;
        *) RID="win-x64" ;;
    esac
fi

# Windows：dotnet 的 ILC 靠注册表探测 Windows SDK，沙箱/精简环境里会退化成
# LNK1181「无法打开 advapi32.lib」。LIB 已有值（VS 开发环境）时不动它；
# 自动探测时要求 um\x64 与 ucrt\x64 都真实存在，否则宁可不设——设错了同样链接失败，
# 只是错误信息更难懂。
if [ -z "${LIB:-}" ] && [[ "$RID" == win-* ]]; then
    # 注意用 glob 而不是 $(ls ...)：路径里有空格（Program Files (x86)），命令替换会按空格拆成多个词，
    # 每个片段都不是目录，探测永远失败（踩过：LNK1181 advapi32.lib 重现了一次）。
    kits=( "/c/Program Files (x86)/Windows Kits/10/Lib/"*/ )
    for ((i = ${#kits[@]} - 1; i >= 0; i--)); do
        kit="${kits[$i]}"
        if [ -d "$kit/um/x64" ] && [ -d "$kit/ucrt/x64" ]; then
            KIT_WIN="$(cygpath -w "$kit")"
            export LIB="$KIT_WIN\\um\\x64;$KIT_WIN\\ucrt\\x64"
            echo "   LIB ← $KIT_WIN"
            break
        fi
    done
fi

FEED="$WORK/feed"
PROJ="$WORK/app"
mkdir -p "$FEED" "$PROJ"

echo "== 1/5 打包 AxiosNet =="
# 必须用唯一版本号：NuGet 按「id/版本」缓存到全局包目录，版本不变时会继续复用旧 dll，
# smoke 就变成在验证上一次的包（本次排查里真实踩到过）。
BASE_VERSION="$("$DOTNET" msbuild "$ROOT/src/AxiosNet/AxiosNet.csproj" -getProperty:Version -nologo | tr -d '\r')"
SMOKE_VERSION="${BASE_VERSION}-smoke.$(date +%s)"
echo "   包版本 $SMOKE_VERSION"
"$DOTNET" pack "$ROOT/src/AxiosNet/AxiosNet.csproj" -c "$CONFIG" -o "$FEED" --nologo -p:Version="$SMOKE_VERSION"

NUPKG="$(ls "$FEED"/AxiosNet.*.nupkg | grep -v -i symbols | head -1)"
echo "   $NUPKG"

echo "== 2/5 断言包内带分析器与 props =="
PYTHON="${PYTHON:-}"
if [ -z "$PYTHON" ]; then
    if command -v python3 >/dev/null 2>&1; then PYTHON=python3; else PYTHON=python; fi
fi
"$PYTHON" - "$NUPKG" <<'PY'
import sys, zipfile

required = [
    "analyzers/dotnet/cs/AxiosNet.SourceGen.dll",
    "buildTransitive/AxiosNet.props",
]
with zipfile.ZipFile(sys.argv[1]) as package:
    names = set(package.namelist())

missing = [name for name in required if name not in names]
if missing:
    raise SystemExit("包内缺少：" + ", ".join(missing))
print("   OK：" + " / ".join(required))
PY

echo "== 3/5 生成全新消费方项目（只引用包） =="
# MSBuild 向上找 Directory.Build.props / .targets 时只取最近的一个：这里放空的把继承掐断，
# 否则将来仓库根一旦加上这类文件，这个「全新项目」就悄悄继承了仓库设置，验证的就不是消费方视角。
printf '<Project />\n' > "$PROJ/Directory.Build.props"
printf '<Project />\n' > "$PROJ/Directory.Build.targets"

cat > "$PROJ/SmokeApp.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AxiosNet" Version="$SMOKE_VERSION" />
  </ItemGroup>
</Project>
XML

cat > "$PROJ/Program.cs" <<'CS'
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet;
using AxiosNet.Serialization;
using AxiosNet.WebAPI;

using var client = new ApiClient("https://example.com/");

// 1) 请求模型：AOT 下没有生成元数据会直接抛 AxiosAotMetadataException。
var url = client.GetUri(new GetItemRequest { Id = 7, Fields = "name" });
if (!url.Contains("items/7", StringComparison.Ordinal) || !url.Contains("Fields=name", StringComparison.Ordinal))
{
    throw new InvalidOperationException("请求模型绑定不正确：" + url);
}

// 2) 接口代理：Create<T> 会校验声明，AOT 下只能靠生成代理 + 注册表。
var api = client.Create<ISmokeApi>();
if (api is null)
{
    throw new InvalidOperationException("接口代理未创建");
}

// 3) System.Text.Json 源生成元数据（不经库，先证明 STJ 自身在 AOT 下可用）。
var json = JsonSerializer.Serialize(new Item { Name = "n" }, SmokeJsonContext.Default.Item);
if (json != "{\"name\":\"n\"}")
{
    throw new InvalidOperationException("JSON 与预期不符：" + json);
}

// 4) 包内 dll 的 net8 序列化路径：JsonSerializerOptions.GetTypeInfo + 源生成解析器。
//    配置写法逐字照 README「运行时与 AOT」的示例——顺带验证那段文档抄下来能跑。
//    （选项必须带 JsonSerializerDefaults.Web：直接 new JsonSerializerOptions() 会丢掉 camelCase，
//     包内序列化结果就变成 PascalCase，与库默认值不一致。）
var config = new AxiosConfig
{
    Serialization = new()
    {
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = SmokeJsonContext.Default
        }
    }
};
var serializer = SystemTextJsonSerializer.Instance;
var item = new Item { Name = "n" };
var text = serializer.Serialize(item, typeof(Item), config);
var back = serializer.Deserialize(text, typeof(Item), config) as Item;
if (text != "{\"name\":\"n\"}" || back?.Name != "n")
{
    throw new InvalidOperationException("库序列化往返不一致：" + text + " / " + back?.Name);
}

Console.WriteLine("package-smoke ok | url=" + url + " | json=" + json + " | roundtrip=" + back!.Name);

[Get("items/{id}")]
public sealed class GetItemRequest : IApiRequest<Item>
{
    [Path]
    public int Id { get; set; }

    [Query]
    public string Fields { get; set; } = string.Empty;
}

public interface ISmokeApi
{
    [Get("items/{id}")]
    Task<AxiosResponse<Item>> GetAsync([Path] int id, CancellationToken cancellationToken);
}

public sealed class Item
{
    public string Name { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Item))]
internal partial class SmokeJsonContext : JsonSerializerContext;
CS

cat > "$PROJ/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local-smoke" value="$FEED" />
    <!-- 运行时包（Microsoft.NETCore.App.Runtime.*）得从 nuget.org 来：不写死就会继承构建机上的用户级配置，
         换台机器行为就变。 -->
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

echo "== 4/5 AOT 发布（$RID） =="
"$DOTNET" publish "$PROJ/SmokeApp.csproj" -c "$CONFIG" -r "$RID" --nologo \
    -p:TrimmerSingleWarn=false \
    -p:WarningsAsErrors=IL2026%3BIL3050%3BIL3051

BIN="$PROJ/bin/$CONFIG/net8.0/$RID/publish/SmokeApp"
[ -f "$BIN" ] || BIN="$BIN.exe"
if [ ! -f "$BIN" ]; then
    echo "找不到发布产物：$BIN" >&2
    exit 1
fi

echo "== 5/5 运行发布的二进制 =="
OUTPUT="$("$BIN")"
echo "   $OUTPUT"
case "$OUTPUT" in
    *"package-smoke ok"*) ;;
    *) echo "smoke 输出不符合预期" >&2; exit 1 ;;
esac

SIZE="$(wc -c < "$BIN")"
echo "全部通过：$BIN（$((SIZE / 1024)) KB）"
