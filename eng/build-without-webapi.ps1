# 只编译内核（WebAPI 目录整体排除），用来证明「WebAPI 可整体删除」。
# 任何内核文件引用 WebAPI 类型时，这条构建都会失败——这是比注释和清单更硬的实证。
#
# 用法：pwsh eng/build-without-webapi.ps1 [-Configuration Release]

param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/AxiosNet/AxiosNet.csproj'
# 独立产物目录：这次构建出来的 AxiosNet.dll 不含 WebAPI，不能覆盖 bin/ 里正常构建的产物
# （否则下一次 dotnet test / pack 可能捡到「内核版」程序集）。artifacts/ 已在 .gitignore 里。
$artifactsRoot = Join-Path $repoRoot 'artifacts/build-without-webapi'

if (-not (Test-Path $project)) {
    Write-Error "找不到内核项目：$project"
    exit 1
}

Write-Host "只编译内核（AxiosNetOmitWebApi=true）：$project"
& dotnet build $project --configuration $Configuration `
    -p:AxiosNetOmitWebApi=true `
    --artifacts-path $artifactsRoot
if ($LASTEXITCODE -ne 0) {
    Write-Error "内核独立构建失败（退出码 $LASTEXITCODE）：说明有内核代码依赖了 WebAPI。"
    exit $LASTEXITCODE
}

Write-Host '内核独立构建通过：WebAPI 目录可整体删除。'
