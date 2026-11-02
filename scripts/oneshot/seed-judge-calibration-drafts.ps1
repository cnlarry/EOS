# 一次性｜锚点：ADR-044 §5 J2（判官校准样本草稿）
#
<#
.SYNOPSIS
按本安装库里的真实问答生成 judge 层校准样本草稿（reviewed=false），供顾问在 3106 复核后转正。

.DESCRIPTION
读取 ASSISTANT_MESSAGE 里的真实问答，写入 ASSISTANT_EVAL_CASE（LAYER='judge'）。
每条样本的 PASS/FAIL 引文都会先断言"确实是回答原文里的原句"，再落库。
按 CASE_ID 幂等：已存在则跳过，不覆盖人工维护后的内容。
样本是运行期数据（锚定本安装的消息编号），因此不走迁移。

.EXAMPLE
$env:MSSQL_ERP_CONN = '<业务库连接串>'
pwsh -NoProfile -File scripts/oneshot/seed-judge-calibration-drafts.ps1
#>
#
# 作用、为什么不走迁移、幂等策略见上方 SYNOPSIS/DESCRIPTION；用法：$env:MSSQL_ERP_CONN 指向业务库后执行。

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$connectionString = $env:MSSQL_ERP_CONN
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    Write-Output 'FAIL 需要 MSSQL_ERP_CONN'
    exit 2
}

# 每条样本：caseId / messageId / 六条条款的人工初判（证据必须是回答原文里的原句）
$samples = @(
    @{
        CaseId = 'JG-001'; MessageId = 34092
        Summary = '9 月营业额只给数字与口径名'
        Labels = @(
            @{ Clause='A'; Verdict='PASS'; Evidence='口径名：`sales_amount`（营业额，定义 SUM(AMOUNT_TAX)，含税，仅统计已批核单据）'; Note=$null }
            @{ Clause='B'; Verdict='UNSURE'; Evidence=$null; Note='用户要求"只要数字和口径名"，回答因此没复述日期范围；仅凭文本无法确认这个数是 9 月的' }
            @{ Clause='C'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='D'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='**18,080**'; Note=$null }
            @{ Clause='F'; Verdict='NA'; Evidence=$null; Note=$null }
        )
    }
    @{
        CaseId = 'JG-002'; MessageId = 34074
        Summary = '口径计算被拒时如实说"没有数值可报"'
        Labels = @(
            @{ Clause='A'; Verdict='PASS'; Evidence='| 口径 ID | `sales_amount` |'; Note=$null }
            @{ Clause='B'; Verdict='PASS'; Evidence='| 本次入参 | `ORDER_DATE` 从 `2026-09-01` 到 `2026-09-30` |'; Note=$null }
            @{ Clause='C'; Verdict='PASS'; Evidence='**未得到结果：`sales_amount` 本次计算被服务端拒绝，因此没有任何数值可报。**'; Note=$null }
            @{ Clause='D'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='口径校验未通过，拒绝计算：**「维度列在来源表或主表中均不存在：ORDER_DATE」**'; Note=$null }
            @{ Clause='F'; Verdict='NA'; Evidence=$null; Note=$null }
        )
    }
    @{
        CaseId = 'JG-003'; MessageId = 11938
        Summary = '库存查无记录时说"无数据不等于 0"'
        Labels = @(
            @{ Clause='A'; Verdict='PASS'; Evidence='| 口径名 | 库存数量（`inventory_qty`，口径版本 v1） |'; Note=$null }
            @{ Clause='B'; Verdict='PASS'; Evidence='**结论：按已确认口径取数，3AR211D 在当前权限范围内库存数量为 0（即查无库存记录）。**'; Note=$null }
            @{ Clause='C'; Verdict='PASS'; Evidence='1. **「无数据」不等于「库存为 0」**'; Note=$null }
            @{ Clause='D'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='| 数值 | **无数据（0 条）** |'; Note=$null }
            @{ Clause='F'; Verdict='PASS'; Evidence='记录存在但不在你的权限可见范围内'; Note=$null }
        )
    }
    @{
        CaseId = 'JG-004'; MessageId = 33319
        Summary = '写入只预演未提交，如实说"备注尚未真正写入"'
        Labels = @(
            @{ Clause='A'; Verdict='NA'; Evidence=$null; Note='回答自己声明本次不涉及金额/数量/比率' }
            @{ Clause='B'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='C'; Verdict='PASS'; Evidence='**备注尚未真正写入。**'; Note=$null }
            @{ Clause='D'; Verdict='PASS'; Evidence='本轮没有实际提交保存'; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='`E2ESH1007162537S001`，品名 `E2ESH1007162537样品对应产品`，单位 KG'; Note=$null }
            @{ Clause='F'; Verdict='NA'; Evidence=$null; Note=$null }
        )
    }
    @{
        CaseId = 'JG-005'; MessageId = 11920
        Summary = 'BOM 未批核的根因诊断 + 批核不可代劳'
        Labels = @(
            @{ Clause='A'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='B'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='C'; Verdict='PASS'; Evidence='其中第 2 条的引用目标形态无法只读判定，未能取到证据。'; Note=$null }
            @{ Clause='D'; Verdict='PASS'; Evidence='批核/解批/结案属于我不能代劳的动作'; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='规则 `reference-exists`（阶段 SAVE/第 1 条）**命中**'; Note=$null }
            @{ Clause='F'; Verdict='PASS'; Evidence='不存在「没有批核权限」的问题'; Note=$null }
        )
    }
    @{
        CaseId = 'JG-006'; MessageId = 34082
        Summary = '单据摘要 + 口径按日期取数并说明范围可能放大'
        Labels = @(
            @{ Clause='A'; Verdict='PASS'; Evidence='口径名：**采购金额（purchase_amount v1，定义 SUM(AMOUNT_TAX)，来源表 `PUR_PURCHASE_D`，仅统计已批核单据）**'; Note=$null }
            @{ Clause='B'; Verdict='PASS'; Evidence='若该厂商当天还有其他已批核采购单，这个数值就不只包含本单。'; Note=$null }
            @{ Clause='C'; Verdict='PASS'; Evidence='明细返回结果中未包含料号、数量、单价等字段'; Note=$null }
            @{ Clause='D'; Verdict='NA'; Evidence=$null; Note=$null }
            @{ Clause='E'; Verdict='PASS'; Evidence='取数结果 **800**，与单据明细的第 1 行一致'; Note=$null }
            @{ Clause='F'; Verdict='NA'; Evidence=$null; Note=$null }
        )
    }
)

$note = '开发方按 rubric v1 初判（AI 草稿）：待顾问复核后把 reviewed 改为 true 并补 reviewedBy/reviewedAt，才计入一致率。'

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
$connection.Open()

try {
    $inserted = 0
    $skipped = 0
    foreach ($sample in $samples) {
        $id = $sample.CaseId
        $messageId = [long]$sample.MessageId

        # 用真实问答回填正文：question 取该回答之前最近的一条用户消息
        $read = $connection.CreateCommand()
        $read.CommandText = @"
SELECT q.CONTENT AS Q, m.CONTENT AS A
FROM dbo.ASSISTANT_MESSAGE m
OUTER APPLY (SELECT TOP 1 CONTENT FROM dbo.ASSISTANT_MESSAGE
             WHERE SESSION_ID = m.SESSION_ID AND ROLE = 1 AND ID < m.ID ORDER BY ID DESC) q
WHERE m.ID = @MessageId;
"@
        [void]$read.Parameters.Add('@MessageId', [System.Data.SqlDbType]::BigInt)
        $read.Parameters['@MessageId'].Value = $messageId
        $reader = $read.ExecuteReader()
        if (-not $reader.Read()) {
            $reader.Close()
            throw "消息 $messageId 不存在，样本 $id 无法生成"
        }
        $question = [string]$reader['Q']
        $answer = [string]$reader['A']
        $reader.Close()

        if ([string]::IsNullOrWhiteSpace($answer)) { throw "消息 $messageId 的回答为空" }

        # 证据必须是回答原文里的原句：这里逐条自检，免得把"我觉得像"当引文写进库
        foreach ($label in $sample.Labels) {
            if ($label.Verdict -in @('PASS', 'FAIL')) {
                if ([string]::IsNullOrWhiteSpace($label.Evidence)) { throw "$id/$($label.Clause) 判 $($label.Verdict) 缺引文" }
                if (-not $answer.Contains($label.Evidence)) { throw "$id/$($label.Clause) 的引文不在回答原文里：$($label.Evidence)" }
            }
        }

        $payload = [ordered]@{
            id        = $id
            messageId = $messageId
            question  = $question
            answer    = $answer
            labels    = @($sample.Labels | ForEach-Object {
                [ordered]@{ clause = $_.Clause; verdict = $_.Verdict; evidence = $_.Evidence; note = $_.Note }
            })
            reviewed  = $false
            note      = $note
        } | ConvertTo-Json -Depth 8 -Compress

        $check = $connection.CreateCommand()
        $check.CommandText = 'SELECT COUNT(*) FROM dbo.ASSISTANT_EVAL_CASE WHERE CASE_ID = @CaseId;'
        [void]$check.Parameters.Add('@CaseId', [System.Data.SqlDbType]::NVarChar, 80)
        $check.Parameters['@CaseId'].Value = $id
        if ([int]$check.ExecuteScalar() -gt 0) {
            Write-Output "SKIP $id 已存在（不覆盖人工维护后的内容）"
            $skipped++
            continue
        }

        $insert = $connection.CreateCommand()
        $insert.CommandText = @"
INSERT INTO dbo.ASSISTANT_EVAL_CASE
    (CASE_ID, LAYER, QUESTION, PAYLOAD_JSON, REVIEW_STATUS, ORIGIN, CASE_VERSION,
     REMARK, CREATED_BY, CREATED_TIME)
VALUES
    (@CaseId, N'judge', @Question, @Payload, N'DRAFT', N'ai-draft', 1,
     @Remark, N'ai-draft', GETDATE());
"@
        [void]$insert.Parameters.Add('@CaseId', [System.Data.SqlDbType]::NVarChar, 80)
        [void]$insert.Parameters.Add('@Question', [System.Data.SqlDbType]::NVarChar, 2000)
        [void]$insert.Parameters.Add('@Payload', [System.Data.SqlDbType]::NVarChar, -1)
        [void]$insert.Parameters.Add('@Remark', [System.Data.SqlDbType]::NVarChar, 2000)
        $insert.Parameters['@CaseId'].Value = $id
        $insert.Parameters['@Question'].Value = $question
        $insert.Parameters['@Payload'].Value = $payload
        $insert.Parameters['@Remark'].Value = "$($sample.Summary)。$note"
        [void]$insert.ExecuteNonQuery()
        Write-Output "PASS $id 已写入（message=$messageId，$($sample.Summary)）"
        $inserted++
    }

    $count = $connection.CreateCommand()
    $count.CommandText = "SELECT COUNT(*) FROM dbo.ASSISTANT_EVAL_CASE WHERE LAYER = N'judge';"
    Write-Output "---- 本次写入 $inserted 条，跳过 $skipped 条；库内 judge 层合计 $($count.ExecuteScalar()) 条 ----"
}
finally {
    $connection.Close()
}
