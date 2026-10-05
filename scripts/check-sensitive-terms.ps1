<#
.SYNOPSIS
    扫描文本文件中的敏感信息（真实企业名、客户名、个人身份信息），拦截其随代码提交扩散。
.DESCRIPTION
    检查分两类：
    - 通用模式：手机号、身份证号等可直接识别的个人身份信息；
    - 私有词表：真实企业名 / 客户名 / 人名等无法用模式表达的词，从版本库之外的词表文件逐行读取。

    词表刻意不入库：词表本身若进了版本库，它就替代被清理的对象成为新的泄露源。

    词表来源（按优先级）：
      1. -TermsFile 参数（指定的文件不存在时报 FAIL，不静默降级）；
      2. 环境变量 EOS_SENSITIVE_TERMS；
      3. $HOME/.eos/sensitive-terms.txt。
    三者皆无时词表项记 SKIP，通用模式检查照常执行。

    扫描面取自 git 跟踪文件清单，天然排除 node_modules、构建产物与日志。
.PARAMETER TermsFile
    私有词表路径，每行一个词，'#' 起首的行视为注释。
.PARAMETER Paths
    只扫描指定路径（相对仓库根或绝对）。缺省扫描 git 跟踪的全部文本文件。
.PARAMETER SelfTest
    用内置样本验证检查逻辑本身，不依赖词表。
.EXAMPLE
    pwsh scripts/check-sensitive-terms.ps1
.EXAMPLE
    pwsh scripts/check-sensitive-terms.ps1 -TermsFile D:\private\terms.txt
#>
[CmdletBinding()]
param(
    [string]$TermsFile,
    [string[]]$Paths,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 参与扫描的文本扩展名；未列出的按二进制处理，不读内容
$TextExtensions = @(
    '.cs', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs',
    '.sql', '.md', '.json', '.ps1', '.psm1', '.py',
    '.css', '.scss', '.html', '.htm', '.yml', '.yaml',
    '.txt', '.csproj', '.props', '.targets', '.sh', '.bat', '.cmd', '.xml'
)

# 超过该体积的文件跳过：第三方锁文件与元数据导出体积大、命中噪音多
$MaxFileBytes = 2MB

# 第三方生成物，命中无处置价值
$ExcludedNames = @('package-lock.json')

# 通用模式：可直接识别的个人身份信息
$GenericPatterns = @(
    @{ Name = '手机号'; Regex = '(?<![\d])1[3-9]\d{9}(?![\d])' },
    @{ Name = '身份证号'; Regex = '(?<![\d])\d{17}[\dXx](?![\d])' }
)

# 公认的测试用假值：脱敏能力的测试夹具需要正例样本，命中这些不算泄露
$KnownTestValues = @('13800138000', '110101199001011234')

function Test-ScannableFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ($ExcludedNames -contains [IO.Path]::GetFileName($Path)) { return $false }
    $ext = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($TextExtensions -notcontains $ext) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -gt $MaxFileBytes) { return $false }
    return $true
}

function Get-TermList {
    param([string]$Path)

    return @(
        [IO.File]::ReadLines($Path) |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') } |
            Sort-Object -Unique
    )
}

function Invoke-Scan {
    param(
        [string[]]$Files,
        [string[]]$Terms,
        [string]$RepoRoot
    )

    $hits = New-Object System.Collections.ArrayList
    $scanned = 0

    foreach ($file in $Files) {
        if (-not (Test-ScannableFile $file)) { continue }
        $scanned++
        $lineNo = 0
        foreach ($line in [IO.File]::ReadLines($file)) {
            $lineNo++
            foreach ($pattern in $GenericPatterns) {
                if ($line -match $pattern.Regex) {
                    if ($KnownTestValues -contains $Matches[0]) { continue }
                    [void]$hits.Add([pscustomobject]@{ File = $file; Line = $lineNo; Term = $pattern.Name })
                }
            }
            foreach ($term in $Terms) {
                if ($line.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    [void]$hits.Add([pscustomobject]@{ File = $file; Line = $lineNo; Term = "词表:$term" })
                }
            }
        }
    }

    return [pscustomobject]@{ Hits = $hits; Scanned = $scanned }
}

try {
    if ($SelfTest) {
        $probe = Join-Path ([IO.Path]::GetTempPath()) ("eos-terms-selftest-{0}.txt" -f [Guid]::NewGuid())
        try {
            [IO.File]::WriteAllLines($probe, @(
                '这是一行普通内容',
                '联系人手机 13912345678 请勿外传',
                '公认测试假号 13800138000 不计入',
                '# 词表注释行'
            ))
            $result = Invoke-Scan -Files @($probe) -Terms @('请勿外传') -RepoRoot ([IO.Path]::GetTempPath())
            if ($result.Hits.Count -eq 2) {
                Write-Output 'PASS  自检通过：通用模式与词表各命中 1 处'
                exit 0
            }
            Write-Output ("FAIL  自检未通过：预期命中 2 处，实际 {0} 处" -f $result.Hits.Count)
            exit 1
        }
        finally {
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        }
    }

    $repoRoot = (& git rev-parse --show-toplevel 2>$null)
    if (-not $repoRoot) { throw '当前目录不在 git 仓库内' }
    $repoRoot = (Resolve-Path -LiteralPath $repoRoot).Path

    if ($Paths) {
        $files = @(
            $Paths | ForEach-Object {
                if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $repoRoot $_ }
            }
        )
        $missing = @($files | Where-Object { -not (Test-Path -LiteralPath $_) })
        if ($missing.Count -gt 0) { throw ("指定的路径不存在：{0}" -f ($missing -join '、')) }
    }
    else {
        $files = @((& git -C $repoRoot ls-files) | ForEach-Object { Join-Path $repoRoot $_ })
    }

    $termsPath = $null
    $termsRequired = $false
    if ($TermsFile) {
        $termsPath = $TermsFile
        $termsRequired = $true
    }
    elseif ($env:EOS_SENSITIVE_TERMS) {
        $termsPath = $env:EOS_SENSITIVE_TERMS
        $termsRequired = $true
    }
    else {
        $candidate = Join-Path $HOME '.eos/sensitive-terms.txt'
        if (Test-Path -LiteralPath $candidate) { $termsPath = $candidate }
    }

    if ($termsPath -and -not (Test-Path -LiteralPath $termsPath -PathType Leaf)) {
        if ($termsRequired) { throw ("词表文件不存在：{0}" -f $termsPath) }
        $termsPath = $null
    }

    $terms = @()
    if ($termsPath) { $terms = Get-TermList -Path $termsPath }

    $result = Invoke-Scan -Files $files -Terms $terms -RepoRoot $repoRoot

    if ($result.Hits.Count -gt 0) {
        foreach ($hit in $result.Hits) {
            $rel = $hit.File.Substring($repoRoot.Length).TrimStart('\', '/')
            Write-Output ("FAIL  {0}:{1}  命中 {2}" -f $rel, $hit.Line, $hit.Term)
        }
        Write-Output ("FAIL  敏感信息检查未通过：{0} 处命中" -f $result.Hits.Count)
        exit 1
    }

    if ($termsPath) {
        Write-Output ("PASS  敏感信息检查通过（扫描 {0} 个文件，词表 {1} 词，来源 {2}）" -f `
            $result.Scanned, $terms.Count, $termsPath)
    }
    else {
        Write-Output ("SKIP  词表未配置，仅执行通用模式检查（扫描 {0} 个文件）" -f $result.Scanned)
        Write-Output ("SKIP  词表可放 {0}，或用 -TermsFile / 环境变量 EOS_SENSITIVE_TERMS 指定" -f `
            (Join-Path $HOME '.eos/sensitive-terms.txt'))
    }
    exit 0
}
catch {
    Write-Output ("FAIL  敏感信息检查异常终止：{0}" -f $_.Exception.Message)
    exit 1
}
