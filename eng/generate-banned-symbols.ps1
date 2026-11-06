# 从 src/AxiosNet/WebAPI/ 的公共类型声明生成 BannedSymbols.txt（BannedApiAnalyzers 的禁止清单）。
#
# 为什么不用手写枚举：
#   · WebAPI 的公共类型会随功能增删变化，手写清单必然漏项；
#   · BannedApiAnalyzers 只认「逐符号一条」，不支持按命名空间禁止，所以必须把每个公共类型各列一条。
#
# 用法：
#   pwsh eng/generate-banned-symbols.ps1          # 重新生成 src/AxiosNet/BannedSymbols.txt
#   pwsh eng/generate-banned-symbols.ps1 -Verify # 只校验清单是否与源码一致（CI 用，不一致则退出码 1）
#
# 扫描规则：只认「缩进任意、public 修饰、顶层类型声明」；嵌套公共类型（当前为 0 个）不参与，
# 因为引用嵌套类型必然先引用外层类型，外层那条已经足够拦住。泛型类型按文档注释 ID 追加 `N 元数。

param(
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webApiRoot = Join-Path $repoRoot 'src/AxiosNet/WebAPI'
$outputPath = Join-Path $repoRoot 'src/AxiosNet/BannedSymbols.txt'

if (-not (Test-Path $webApiRoot)) {
    Write-Error "找不到 WebAPI 目录：$webApiRoot"
    exit 1
}

# 类型关键字后的名字；修饰符顺序与组合（static / sealed / abstract / partial / readonly / ref / unsafe）全部覆盖。
$declarationPattern = [regex]::new(
    '^\s*public\s+(?:(?:static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*(class|interface|enum|struct|record(?:\s+struct)?)\s+([A-Za-z_][A-Za-z0-9_]*)',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

$namespacePattern = [regex]::new(
    '^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

$message = 'WebAPI 可整体删除：内核目录不得引用（见 eng/build-without-webapi.ps1）'
$entries = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)

foreach ($file in Get-ChildItem -Path $webApiRoot -Recurse -Filter '*.cs' -File) {
    $namespace = $null
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        if ($null -eq $namespace) {
            $namespaceMatch = $namespacePattern.Match($line)
            if ($namespaceMatch.Success) {
                $namespace = $namespaceMatch.Groups[1].Value
                continue
            }
        }

        $declaration = $declarationPattern.Match($line)
        if (-not $declaration.Success) {
            continue
        }

        # 类型名后紧跟的 <...> 决定泛型元数；文档注释 ID 用 `N 表示。
        $arity = 0
        $tailStart = $declaration.Groups[2].Index + $declaration.Groups[2].Length
        $tail = $line.Substring($tailStart)
        $arityMatch = [regex]::Match($tail, '^\s*<([^>]*)>')
        if ($arityMatch.Success) {
            $arity = $arityMatch.Groups[1].Value.Split(',').Length
        }

        $typeName = $declaration.Groups[2].Value
        $fullName = if ($namespace) { "$namespace.$typeName" } else { $typeName }
        $symbolId = if ($arity -gt 0) { 'T:{0}`{1}' -f $fullName, $arity } else { "T:$fullName" }

        [void]$entries.Add("$symbolId;$message")
    }
}

if ($entries.Count -eq 0) {
    Write-Error '没有从 WebAPI 目录扫描到任何公共类型，禁止清单会空转，终止。'
    exit 1
}

$expected = [string[]]$entries

if ($Verify) {
    if (-not (Test-Path $outputPath)) {
        Write-Error "缺少 $outputPath，请先运行 eng/generate-banned-symbols.ps1 生成。"
        exit 1
    }

    $actual = [System.IO.File]::ReadAllLines($outputPath)
    # 只比较有效行（去掉空行），换行风格由平台决定，不参与比较。
    $actualEntries = @($actual | Where-Object { $_.Trim().Length -gt 0 })
    $expectedEntries = @($expected | Where-Object { $_.Trim().Length -gt 0 })

    $missing = @($expectedEntries | Where-Object { $actualEntries -notcontains $_ })
    $extra = @($actualEntries | Where-Object { $expectedEntries -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        if ($missing.Count -gt 0) {
            Write-Host '清单缺少以下条目：'
            $missing | ForEach-Object { Write-Host "  - $_" }
        }

        if ($extra.Count -gt 0) {
            Write-Host '清单包含源码里已不存在的条目：'
            $extra | ForEach-Object { Write-Host "  + $_" }
        }

        Write-Error 'BannedSymbols.txt 与 WebAPI 公共类型清单不一致，请运行 eng/generate-banned-symbols.ps1 重新生成。'
        exit 1
    }

    Write-Host "BannedSymbols.txt 已是最新（$($expectedEntries.Count) 条）。"
    exit 0
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllLines($outputPath, $expected, $utf8NoBom)
Write-Host "已生成 $outputPath（$($expected.Count) 条）。"
