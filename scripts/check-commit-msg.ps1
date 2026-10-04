#Requires -Version 7.0
<#
.SYNOPSIS
    提交信息门禁：拦「只有本次会话才看得懂」的内容——对话上下文、任务条目、验证流水。

.DESCRIPTION
    权威规则见 AGENTS.md 第四节「提交信息」。提交信息的读者是后来翻 log 的人，
    而人在会话里写信息时最容易顺手带上会话上下文（对话轮次、"按用户要求"、"验证全绿"），
    这些内容跳出本会话即失去含义，只污染历史。规则写在文档里挡不住，故把**可机械判定**的
    那部分做成门禁；文风好坏仍由人判断，本脚本不评。

    FAIL（确定是会话产物或格式违规）：
      1. 标题不符合 `<type>(<scope 可选>): <描述>`，或 type 不在仓库白名单内；
      2. 会话身份与授权：`按用户要求` / `用户拍板` / `本次任务` / `本会话` 等，以及 `①②③` 式任务条目、"第 N 步"；
      3. 验证流水：`验证：…` / `门禁通过` / `测试全绿` / `用例 N 条` / `实测通过`；
      4. 过程叙述与情绪化措辞：`顺带` / `经过排查` / `早就` / `如实回报`。

    WARN（只提示，不阻断——可能有正当用法，由人判断）：
      标题超长、标题内出现逗号分号并列、`并记下` / `另外，` 式第二件事措辞。

    只读 git 与传入文本，不连库、不发网络请求、不改写历史提交；命中后请改措辞再提交，
    不要求回改历史（历史提交按既有拍板保持原样）——用 `-Range` 抽查时请把范围限定在本次新增的提交上。

    **已知局限**：它按词形匹配，分不清「使用」与「引用」。正文里为了举例而字面写出这些词
    （例如说明「哪些写法不该出现」）同样会被判失败——改写措辞（用「会话身份词」这类指代）即可绕过。

.EXAMPLE
    pwsh scripts/check-commit-msg.ps1 -Message 'fix(workbench): 明细录入不再逐字失焦'
    pwsh scripts/check-commit-msg.ps1 -MessageFile .git/COMMIT_EDITMSG
    pwsh scripts/check-commit-msg.ps1                              # 默认查 HEAD 一条
    pwsh scripts/check-commit-msg.ps1 -Range origin/main..HEAD     # 只抽查本次新增的提交
#>
param(
    [string] $Message,
    [string] $MessageFile,
    # 留空 = 只查 HEAD 一条；给区间（如 origin/main..HEAD）则逐条查
    [string] $Range,
    # 单行标题的字符数上限（中文按字符计）。超长只提示：拆分与否由人判断。
    [int] $MaxSubjectLength = 72,
    # 报告条数上限，超出只计数不逐条打印（抽查大区间时用）
    [int] $MaxReport = 40
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Message -and $MessageFile) {
    Write-Host 'FAIL: -Message 与 -MessageFile 只能给一个' -ForegroundColor Red
    exit 1
}

# ---- 规则表 ----
$subjectPattern = '^(feat|fix|refactor|perf|test|docs|style|chore|build|ci|revert)(\([^)]+\))?!?: \S'

$errorPatterns = [ordered]@{
    '会话身份或授权' = '按用户要求|按用户指示|按用户意见|用户拍板|用户要求|用户指定|用户确认|与用户|本次|本任务|本会话'
    '任务条目或步骤序号' = '[①②③④⑤⑥⑦⑧⑨⑩]|第\s*[一二三四五六七八九十百\d]+\s*步'
    '验证流水'       = '验证[:：]|门禁(通过|全绿)|测试全绿|全绿|用例\s*\d+\s*条|实测通过|已验证|全部通过'
    '过程叙述或情绪化措辞' = '顺带|经过排查|早就|如实回报'
}

$warningPatterns = [ordered]@{
    '第二件事措辞' = '并记下|同时记下|并立|并顺便'
    '口语连接词'   = '另：|另外，|顺便'
}

# ---- 取待检查的消息 ----
function Get-CheckTargets {
    <#
      返回 @{ Label; Text } 列表。Label 用于输出定位（提交短哈希或文件名）。
      git 无输出时命令结果是 $null，统一走 Out-String，避免"对 Null 调方法"把门禁自己搞崩。
    #>
    if ($Message) {
        return @([pscustomobject]@{ Label = '(命令行传入)'; Text = $Message.Trim() })
    }
    if ($MessageFile) {
        if (-not (Test-Path -LiteralPath $MessageFile)) {
            throw "消息文件不存在：$MessageFile"
        }
        # commit-msg hook 传来的草稿：去掉注释行（模板说明）后再校验
        $lines = @(Get-Content -LiteralPath $MessageFile -Encoding UTF8 |
            Where-Object { $_ -notmatch '^\s*#' })
        return @([pscustomobject]@{ Label = $MessageFile; Text = ($lines -join "`n").Trim() })
    }

    # 留空时只取 HEAD 一条——注意 `git log HEAD` 会列出**全部历史**，那是抽查历史，不是查当前提交
    $logArgs = if ([string]::IsNullOrWhiteSpace($Range)) {
        @('log', '-1', '--format=%h%x1f%B%x1e')
    } else {
        @('log', '--format=%h%x1f%B%x1e', $Range)
    }
    $raw = (& git -C $root @logArgs 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "git log 取提交失败（区间：$(if ($Range) { $Range } else { 'HEAD' })）——请检查区间写法，例如 origin/main..HEAD"
    }
    $targets = [System.Collections.Generic.List[object]]::new()
    foreach ($chunk in ($raw -split [char]0x1e)) {
        if ([string]::IsNullOrWhiteSpace($chunk)) { continue }
        $parts = $chunk.Trim("`r", "`n") -split [char]0x1f, 2
        if ($parts.Count -lt 2) { continue }
        $targets.Add([pscustomobject]@{ Label = $parts[0].Trim(); Text = $parts[1].Trim() })
    }
    if ($targets.Count -eq 0) {
        throw "区间 $Range 内没有提交"
    }    return $targets
}

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

try {
    $targets = Get-CheckTargets
} catch {
    Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

foreach ($target in $targets) {
    $lines = @($target.Text -split "`r?`n")
    $subject = @($lines | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
    $subject = if ($subject.Count -gt 0) { $subject[0].Trim() } else { '' }
    $bodyLines = @($lines | Select-Object -Skip 1)
    $body = ($bodyLines -join "`n")

    # 1. 标题格式
    if ($subject -notmatch $subjectPattern) {
        $errors.Add("$($target.Label) 标题不符合 <type>(<scope 可选>): <描述>：$subject")
    }

    # 2. 标题长度与并列（警告）
    if ($subject.Length -gt $MaxSubjectLength) {
        $warnings.Add("$($target.Label) 标题 $($subject.Length) 字，超过 $MaxSubjectLength ——请确认是否该拆提交或换更短的写法")
    }
    if ($subject -match '[，；]') {
        $warnings.Add("$($target.Label) 标题里出现逗号/分号并列，疑似塞了第二件事：$subject")
    }

    # 3. 硬信号：标题与正文一起查
    foreach ($name in $errorPatterns.Keys) {
        foreach ($scope in @(@{ N = '标题'; T = $subject }, @{ N = '正文'; T = $body })) {
            if ([string]::IsNullOrWhiteSpace($scope.T)) { continue }
            $hit = [regex]::Match($scope.T, $errorPatterns[$name])
            if ($hit.Success) {
                $errors.Add("$($target.Label) $($scope.N)含$name「$($hit.Value)」——" +
                            '这是本次会话的产物，跳出会话即无价值；请改写成改动本身，或删除')
            }
        }
    }

    # 4. 软信号：只提示
    foreach ($name in $warningPatterns.Keys) {
        foreach ($scope in @(@{ N = '标题'; T = $subject }, @{ N = '正文'; T = $body })) {
            if ([string]::IsNullOrWhiteSpace($scope.T)) { continue }
            $hit = [regex]::Match($scope.T, $warningPatterns[$name])
            if ($hit.Success) {
                $warnings.Add("$($target.Label) $($scope.N)疑似$name「$($hit.Value)」——请确认是否只讲了这一件事")
            }
        }
    }
}

# ---- 汇总 ----
function Write-Report {
    param([string] $Tag, $Items, [string] $Color)
    if ($Items.Count -eq 0) { return }
    $shown = 0
    foreach ($item in $Items) {
        if ($shown -ge $MaxReport) { break }
        Write-Host "  [$Tag] $item" -ForegroundColor $Color
        $shown++
    }
    if ($Items.Count -gt $shown) {
        Write-Host "  [$Tag] …另有 $($Items.Count - $shown) 条，调大 -MaxReport 可看全" -ForegroundColor $Color
    }
}

Write-Host "== 提交信息检查（$($targets.Count) 条）==" -ForegroundColor Cyan
Write-Report -Tag 'WARN' -Items $warnings -Color DarkYellow
Write-Report -Tag 'ERROR' -Items $errors -Color Red

if ($errors.Count -gt 0) {
    Write-Host "提交信息门禁不通过：$($errors.Count) 个错误、$($warnings.Count) 条警告。" -ForegroundColor Red
    Write-Host '规则见 AGENTS.md 第四节「提交信息」；措辞改了再提交，历史提交不回改。' -ForegroundColor Red
    exit 1
}
if ($warnings.Count -gt 0) {
    Write-Host "提交信息门禁通过：$($warnings.Count) 条警告（仅提示，请自行确认）。" -ForegroundColor Yellow
} else {
    Write-Host '提交信息门禁通过：无会话产物、格式合规。' -ForegroundColor Green
}
exit 0
