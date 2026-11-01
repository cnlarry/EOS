# 一次性｜锚点：迁移 458 / 模块 3106（助手评估用例的种子导出，随迁移 458 落库即失效）
<#
.SYNOPSIS
    一次性｜锚点：迁移 458（助手评估用例模块）——把现有种子文件导出成迁移的种子数据段。

.DESCRIPTION
    读取 EOS.API.Tests/AssistantEval 下的 8 个种子文件，生成 458 的 INSERT 段。
    表结构、模块行、字段元数据由迁移正文手写，本脚本只生成数据行。
    生成物一次性使用；此后用例的增删改都在库里（模块 3106）进行，不回灌本脚本。

.PARAMETER OutFile
    生成结果写入的路径（通常先写临时文件，再拼进迁移）。

.EXAMPLE
    pwsh scripts/oneshot/gen-458-eval-case-seed.ps1 -OutFile $env:TEMP\seed458.sql
#>
param(
    [Parameter(Mandatory = $true)][string] $OutFile
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evalRoot = Join-Path $repoRoot 'EOS.API.Tests/AssistantEval'

function Q([string] $s) {
    if ($null -eq $s) { return 'NULL' }
    return "N'" + $s.Replace("'", "''") + "'"
}
function QJson($obj) {
    if ($null -eq $obj) { return 'NULL' }
    return Q ($obj | ConvertTo-Json -Compress -Depth 10)
}

$layers = [ordered]@{
    'action'     = 'ACT'
    'authz'      = 'AUTHZ'
    'business'   = 'BUSI'
    'config'     = 'CONF'
    'diagnosis'  = 'DIAG'
    'functional' = 'FUNC'
    'inference'  = 'INFR'
    'situation'  = 'SITU'
}

$rows = New-Object System.Collections.Generic.List[string]
foreach ($layer in $layers.Keys) {
    $file = Get-ChildItem (Join-Path $evalRoot $layer) -Filter '*.jsonl' | Select-Object -First 1
    if (-not $file) { continue }
    $index = 0
    foreach ($line in (Get-Content $file.FullName -Encoding utf8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $index++
        $o = $line | ConvertFrom-Json

        # 稳定业务标识：文件里自带 id 就沿用（诊断/config/action/situation），否则按层顺序编号。
        $caseId = if ($o.PSObject.Properties['id'] -and $o.id) { [string]$o.id } else { '{0}-{1:D3}' -f $layers[$layer], $index }
        if ($caseId.Length -gt 40) { $caseId = $caseId.Substring(0, 40) }

        $question = if ($o.PSObject.Properties['question']) { [string]$o.question } else { $null }
        $expected = if ($o.PSObject.Properties['expected']) { [string]$o.expected }
                    elseif ($o.PSObject.Properties['message']) { [string]$o.message }
                    else { $null }
        $tools = if ($o.PSObject.Properties['expect_tools'] -and $o.expect_tools) { ($o.expect_tools -join ',') } else { $null }
        $page = if ($o.PSObject.Properties['page']) { [string]$o.page } else { $null }
        $note = if ($o.PSObject.Properties['note']) { [string]$o.note } else { $null }

        # 诊断层已逐条审阅（2026-10-31）；其余层仍是代码 grounded 的候选。
        $reviewed = ($o.PSObject.Properties['reviewed'] -and $o.reviewed -eq $true)
        $status = if ($reviewed) { 'APPROVED' } else { 'DRAFT' }

        # 载荷：整行原样存 JSON，保证各层字段不丢（列只承载通用面）。
        $payload = $o | ConvertTo-Json -Compress -Depth 10

        $rows.Add(("    ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, N'official', 1, {8})" -f `
            (Q $caseId), (Q $layer), (Q $question), (Q $expected), (Q $tools), (Q $page), (Q $payload), (Q $status), (Q $note)))
    }
}

$header = @"
-- ============================================================================
-- 迁移 458 的种子数据部分（由 scripts/oneshot/gen-458-eval-case-seed.ps1 生成）
-- 来源：EOS.API.Tests/AssistantEval 下 8 个种子文件；诊断层 30 条已审阅（APPROVED），
--       其余层为代码 grounded 的候选（DRAFT）。
-- 只在表为空时导入一次：此后用例的增删改都在库里改（模块 3106），不回灌本脚本。
-- ============================================================================
SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_EVAL_CASE)
BEGIN
    INSERT INTO dbo.ASSISTANT_EVAL_CASE
        (CASE_ID, LAYER, QUESTION, EXPECTED, EXPECT_TOOLS, PAGE_TYPE, PAYLOAD_JSON, REVIEW_STATUS, ORIGIN, CASE_VERSION, REMARK)
    VALUES
"@

$footer = @"
    ;
    PRINT N'== 评估用例种子已导入 ==';
END
ELSE
    PRINT N'== 评估用例表非空，跳过种子导入 ==';
"@

$content = $header + "`n" + ($rows -join ",`n") + "`n" + $footer
Set-Content -LiteralPath $OutFile -Value $content -Encoding utf8
"rows=$($rows.Count) -> $OutFile"
