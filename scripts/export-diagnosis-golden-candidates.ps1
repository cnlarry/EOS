<#
.SYNOPSIS
    Export diagnosis golden-set candidates from MODULE_VALIDATION_RULE (read-only).

.DESCRIPTION
    Enumerates validation rules as diagnosis samples: each row of
    MODULE_VALIDATION_RULE is a case the diagnosis should be able to explain.
    Sampling is deduplicated by VALIDATION_KEY with SAVE as the majority and at
    least one sample from each of the four stages. Two field-guard samples
    (engine-maintained columns rejected by master-field-write) and two
    record-state samples (approved / finished records reject edits) are
    appended from code facts. Nothing is written to the database.

    Trigger facts are extracted inside SQL as short scalars (JSON_VALUE on
    single paths, STRING_AGG over join targets). Scalar results are short by
    construction, so the fixed-width text path never truncates them. The full
    PARAM_STRUCT text is never pulled to the client. Rows in the
    MODULE_VALIDATION_RULE pool are ENABLED=1 with a non-empty MESSAGE, except
    custom-validation whose answer lives in PARAM_STRUCT.check.message /
    check.deleteMessage (its MESSAGE column is empty for all 7 rows).

    Outputs (both deterministic, re-runnable):
      - a human-readable candidate list (Markdown)
      - a machine-readable skeleton (JSONL, every row marked not reviewed)

    Standard answers come from stored facts only (MESSAGE plus module name
    plus the triggering key inside PARAM_STRUCT). No business meaning is
    inferred or rewritten.

.EXAMPLE
    pwsh scripts/export-diagnosis-golden-candidates.ps1
    pwsh scripts/export-diagnosis-golden-candidates.ps1 -MarkdownPath docs/plans/ADR-016-S2黄金集候选.md -JsonlPath EOS.API.Tests/AssistantEval/diagnosis/golden.jsonl
#>
[CmdletBinding()]
param(
    [string] $MarkdownPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'docs/plans/ADR-016-S2黄金集候选.md'),
    [string] $JsonlPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'EOS.API.Tests/AssistantEval/diagnosis/golden.jsonl')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8

# Sampling quotas: ordered list of (key, stage, take). Deterministic by
# (M_IDX, SEQ) within each group. The pool holds ENABLED=1 rows with a
# non-empty MESSAGE, except custom-validation (see Get-CustomPool).
$quotas = @(
    @{ Key = 'reference-exists';  Stage = 'SAVE';      Take = 6 },
    @{ Key = 'qty-not-exceed';    Stage = 'SAVE';      Take = 4 },
    @{ Key = 'line-require';      Stage = 'SAVE';      Take = 4 },
    @{ Key = 'duplicate-check';   Stage = 'SAVE';      Take = 3 },
    @{ Key = 'qty-not-exceed';    Stage = 'APPROVE';   Take = 2 },
    @{ Key = 'qty-not-exceed';    Stage = 'DEAPPROVE'; Take = 2 },
    @{ Key = 'line-require';      Stage = 'APPROVE';   Take = 1 },
    @{ Key = 'custom-validation'; Stage = 'SAVE';      Take = 1 },
    @{ Key = 'custom-validation'; Stage = 'DELETE';    Take = 1 },
    @{ Key = 'period-overlap';    Stage = 'SAVE';      Take = 1 },
    @{ Key = 'no-cycle';          Stage = 'SAVE';      Take = 1 }
)

function ConvertTo-Scalar {
    # Normalizes one text-path cell: SQL NULL arrives as empty (managed path)
    # or as the literal NULL (sqlcmd path). Both mean "path absent".
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $t = $Value.Trim()
    if ($t -eq 'NULL') { return '' }
    return $t
}

function Get-RowFacts {
    # One round trip per rule row. Every selected column is a short scalar
    # (identifier, mode name, single message), so fixed-width truncation
    # cannot cut them. Returns a hashtable ofFacts.
    param([string] $ModuleId, [string] $Stage, [int] $Seq)
    $w = "M_IDX=" + $ModuleId + " AND STAGE=N'" + $Stage + "' AND SEQ=" + $Seq
    $sql = "SELECT " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].refTable'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].refKey.field'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].refKey.scope'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].lineField'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.mode'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].targetTable'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.table'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.handler'), " +
        "REPLACE(REPLACE(REPLACE(ISNULL(JSON_VALUE(PARAM_STRUCT,'$.check.message'),''), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '), " +
        "REPLACE(REPLACE(REPLACE(ISNULL(JSON_VALUE(PARAM_STRUCT,'$.check.deleteMessage'),''), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].scope'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].field'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.detailTable'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].table'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].parentField'), " +
        "JSON_VALUE(PARAM_STRUCT,'$.checks[0].childField'), " +
        "(SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE r2 WITH (NOLOCK) CROSS APPLY OPENJSON(r2.PARAM_STRUCT,'$.checks') WHERE r2.M_IDX=" + $ModuleId + " AND r2.STAGE=N'" + $Stage + "' AND r2.SEQ=" + $Seq + "), " +
        "(SELECT STRING_AGG(j.target, ',') FROM dbo.MODULE_VALIDATION_RULE r3 WITH (NOLOCK) CROSS APPLY OPENJSON(r3.PARAM_STRUCT,'$.checks[0].join') WITH (target nvarchar(100) '$.target') j WHERE r3.M_IDX=" + $ModuleId + " AND r3.STAGE=N'" + $Stage + "' AND r3.SEQ=" + $Seq + ") " +
        "FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK) WHERE " + $w + ";"
    $rows = @(Invoke-DbQuery -Sql $sql)
    $line = if ($rows.Count -gt 0) { [string]$rows[0] } else { '' }
    $cells = $line -split "`t"
    while ($cells.Count -lt 18) { $cells += '' }
    return @{
        RefTable      = ConvertTo-Scalar $cells[0]
        RefKeyField   = ConvertTo-Scalar $cells[1]
        RefKeyScope   = ConvertTo-Scalar $cells[2]
        LineField     = ConvertTo-Scalar $cells[3]
        Mode          = ConvertTo-Scalar $cells[4]
        TargetTable   = ConvertTo-Scalar $cells[5]
        Table         = ConvertTo-Scalar $cells[6]
        Handler       = ConvertTo-Scalar $cells[7]
        CheckMessage  = ConvertTo-Scalar $cells[8]
        DeleteMessage = ConvertTo-Scalar $cells[9]
        Scope         = ConvertTo-Scalar $cells[10]
        Field         = ConvertTo-Scalar $cells[11]
        DetailTable   = ConvertTo-Scalar $cells[12]
        CycleTable    = ConvertTo-Scalar $cells[13]
        ParentField   = ConvertTo-Scalar $cells[14]
        ChildField    = ConvertTo-Scalar $cells[15]
        ChecksCount   = ConvertTo-Scalar $cells[16]
        JoinTargets   = ConvertTo-Scalar $cells[17]
    }
}

function Get-TriggerText {
    # Builds the trigger sentence purely from the fetched scalars. Every
    # table/field/mode name it prints was read back from PARAM_STRUCT, so a
    # reviewer can find each one there. Returns @{ Summary; Evidence }.
    param([string] $ValidationKey, [hashtable] $F)
    # Only rules with a checks[] array (qty / reference / line-require /
    # no-cycle) report a group count, and only when there is more than one.
    # Other templates carry no checks array: their count query yields 0,
    # which means "not applicable", not "zero groups".
    $multi = ''
    $groupCount = 0
    if (-not [int]::TryParse($F.ChecksCount, [ref] $groupCount)) { $groupCount = 0 }
    if ($groupCount -gt 1) { $multi = '（共' + $groupCount + '组判据，首组如下）' }
    switch ($ValidationKey.ToLowerInvariant()) {
        'reference-exists' {
            if ($F.RefTable -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            $assoc = ''
            if ($F.JoinTargets -ne '') { $assoc = '（关联键 ' + $F.JoinTargets + '）' }
            elseif ($F.RefKeyField -ne '') {
                $assoc = '（关联键 ' + $F.RefKeyField
                if ($F.RefKeyScope -ne '') { $assoc += '，' + $F.RefKeyScope + '域' }
                $assoc += '）'
            }
            $diag = ''
            if ($F.LineField -ne '') { $diag = '，命中行报 ' + $F.LineField }
            return @{ Summary = '被引用表 ' + $F.RefTable + ' 找不到匹配行' + $assoc + $diag + $multi; Evidence = $F.RefTable + '（被引用表）+ MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
        'qty-not-exceed' {
            if ($F.Mode -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            $target = $F.TargetTable
            if ($target -eq '') { $target = '未指定目标表' }
            return @{ Summary = '模式 ' + $F.Mode + '，目标 ' + $target + ' 上的数量比较不成立' + $multi; Evidence = $target + ' + MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
        'duplicate-check' {
            $mode = $F.Mode
            if ($mode -eq '') { $mode = '未指定模式' }
            $table = $F.Table
            if ($table -eq '') { $table = '未指定表' }
            return @{ Summary = '模式 ' + $mode + '，表 ' + $table + ' 出现重复键' + $multi; Evidence = $table + ' + MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
        'line-require' {
            if ($F.Field -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            $scope = $F.Scope
            if ($scope -eq '') { $scope = '未指定范围' }
            return @{ Summary = '范围 ' + $scope + ' 的字段 ' + $F.Field + ' 未填或断言不成立' + $multi; Evidence = '本单明细/主表行 + MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
        'custom-validation' {
            if ($F.Handler -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            $answer = $F.CheckMessage
            if ($answer -eq '') { $answer = $F.DeleteMessage }
            if ($answer -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            return @{ Summary = '定制校验 ' + $F.Handler + ' 命中：' + $answer + $multi; Evidence = '注册实现 ' + $F.Handler + ' + MODULE_VALIDATION_RULE.PARAM_STRUCT.check' }
        }
        'period-overlap' {
            if ($F.DetailTable -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            return @{ Summary = '明细表 ' + $F.DetailTable + ' 的期间与他单相交' + $multi; Evidence = $F.DetailTable + ' + MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
        'no-cycle' {
            if ($F.CycleTable -eq '') {
                return @{ Summary = ''; Evidence = '' }
            }
            $edge = ''
            if ($F.ParentField -ne '' -and $F.ChildField -ne '') { $edge = '（' + $F.ParentField + '→' + $F.ChildField + '）' }
            return @{ Summary = '表 ' + $F.CycleTable + ' 的引用链' + $edge + '回到起点' + $multi; Evidence = $F.CycleTable + ' + MODULE_VALIDATION_RULE.PARAM_STRUCT' }
        }
    }
    return @{ Summary = ''; Evidence = '' }
}

try {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    . (Join-Path $root 'scripts/dev/eos-sql.ps1')
    # All text reads go through the managed client, never sqlcmd: sqlcmd
    # -h -1 -W truncates wide columns at 256 chars, and its byte output is
    # decoded with the process console encoding (CJK text corrupts when that
    # is not UTF-8). The managed client returns full .NET strings with NULL
    # as empty, so reads are identical on every run regardless of console.
    $dbConnectionString = (Get-EosSqlTarget).AdoConnectionString
    # NOTE: rows are returned verbatim (no Trim). Leading/trailing empty
    # cells are significant: trimming the whole line would eat the boundary
    # tabs and shift every column. Callers trim per cell instead.
    function Invoke-DbQuery {
        param([string] $Sql)
        @(Invoke-EosManagedQuery -ConnectionString $dbConnectionString -Sql $Sql |
            Where-Object { $_ -ne $null } | ForEach-Object { "$_" })
    }

    function Invoke-Rows {
        param([string] $Query)
        # Drop truly blank lines, but keep the surviving lines verbatim so
        # tab-separated columns stay aligned (see Invoke-DbQuery note).
        @(Invoke-DbQuery -Sql $Query | Where-Object { "$_".Trim() -ne '' } | ForEach-Object { "$_" })
    }

    # Candidate pool: ENABLED=1 rows with a non-empty MESSAGE, except
    # custom-validation (all 7 rows have an empty MESSAGE column; their
    # answer is fetched from PARAM_STRUCT.check, see below). MESSAGE and
    # module name are flattened so the tab split is stable.
    $poolLines = Invoke-Rows @'
SELECT CONCAT(r.M_IDX, CHAR(9), RTRIM(r.STAGE), CHAR(9), r.SEQ, CHAR(9),
       RTRIM(r.VALIDATION_KEY), CHAR(9),
       REPLACE(REPLACE(REPLACE(RTRIM(r.MESSAGE), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '), CHAR(9),
       REPLACE(REPLACE(REPLACE(RTRIM(ISNULL(m.M_DESC, '')), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '))
FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.M_IDX
WHERE r.ENABLED=1 AND r.MESSAGE IS NOT NULL AND LTRIM(RTRIM(r.MESSAGE))<>''
  AND r.VALIDATION_KEY<>N'custom-validation'
ORDER BY r.VALIDATION_KEY, r.STAGE, r.M_IDX, r.SEQ;
'@
    $pool = @()
    foreach ($line in $poolLines) {
        $parts = $line -split "`t"
        if ($parts.Count -lt 6) { continue }
        $pool += [pscustomobject]@{
            ModuleId   = $parts[0].Trim()
            Stage      = $parts[1].Trim()
            Seq        = [int]$parts[2].Trim()
            Key        = $parts[3].Trim()
            Message    = $parts[4]
            ModuleName = $parts[5]
        }
    }
    if ($pool.Count -eq 0) { Write-Output 'FAIL 规则池为空，检查数据库连接'; exit 1 }

    # custom-validation pool: same ordering, answer-bearing rows only
    # (check.message or check.deleteMessage non-empty).
    $customLines = Invoke-Rows @'
SELECT CONCAT(r.M_IDX, CHAR(9), RTRIM(r.STAGE), CHAR(9), r.SEQ, CHAR(9),
       REPLACE(REPLACE(REPLACE(RTRIM(ISNULL(m.M_DESC, '')), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '))
FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.M_IDX
WHERE r.ENABLED=1 AND r.VALIDATION_KEY=N'custom-validation'
ORDER BY r.STAGE, r.M_IDX, r.SEQ;
'@
    $customPool = @()
    foreach ($line in $customLines) {
        $parts = $line -split "`t"
        if ($parts.Count -lt 4) { continue }
        $facts = Get-RowFacts -ModuleId $parts[0].Trim() -Stage $parts[1].Trim() -Seq ([int]$parts[2].Trim())
        if ($facts.CheckMessage -eq '' -and $facts.DeleteMessage -eq '') { continue }
        $customPool += [pscustomobject]@{
            ModuleId = $parts[0].Trim(); Stage = $parts[1].Trim(); Seq = [int]$parts[2].Trim()
            ModuleName = $parts[3]; Handler = $facts.Handler
            CheckMessage = $facts.CheckMessage; DeleteMessage = $facts.DeleteMessage
        }
    }

    $picked = @()
    foreach ($q in $quotas) {
        if ($q.Key -eq 'custom-validation') {
            $group = @($customPool | Where-Object { $_.Stage -eq $q.Stage })
        } else {
            $group = @($pool | Where-Object { $_.Key -eq $q.Key -and $_.Stage -eq $q.Stage } |
                Sort-Object { [int]$_.ModuleId }, Seq)
        }
        $take = [Math]::Min($q.Take, $group.Count)
        for ($i = 0; $i -lt $take; $i++) { $picked += $group[$i] }
        if ($group.Count -lt $q.Take) {
            Write-Output ("SKIP {0}|{1}: 配额 {2}，实际 {3}" -f $q.Key, $q.Stage, $q.Take, $group.Count)
        }
    }

    $records = @()
    $seq = 0
    foreach ($row in $picked) {
        $seq++
        $isCustom = ($row.PSObject.Properties.Name -contains 'Handler')
        $facts = Get-RowFacts -ModuleId $row.ModuleId -Stage $row.Stage -Seq ([int]$row.Seq)
        if ($isCustom) {
            $t = Get-TriggerText -ValidationKey 'custom-validation' -F $facts
            # Stage-appropriate answer: a DELETE rule answers with
            # deleteMessage, a SAVE rule with message; fall back either way.
            if ($row.Stage -eq 'DELETE') {
                $message = $row.DeleteMessage
                if ($message -eq '') { $message = $row.CheckMessage }
            } else {
                $message = $row.CheckMessage
                if ($message -eq '') { $message = $row.DeleteMessage }
            }
            $messageSource = 'param'
            $key = 'custom-validation'
        } else {
            $t = Get-TriggerText -ValidationKey $row.Key -F $facts
            $message = $row.Message
            $messageSource = 'column'
            $key = $row.Key
        }
        $records += [pscustomobject]@{
            Id             = ('V-{0:00}' -f $seq)
            ModuleId       = $row.ModuleId
            ModuleName     = $row.ModuleName
            Stage          = $row.Stage
            SeqNo          = [int]$row.Seq
            Key            = $key
            Enabled        = $true
            Message        = $message
            MessageSource  = $messageSource
            TriggerSummary = $t.Summary
            EvidenceSource = $t.Evidence
            SuggestedClass = 'validation'
        }
    }

    # Two field-guard samples: engine-maintained columns are rejected by the
    # master-field-write action even when declared as parameters. Source is
    # code (MasterFieldWriteHandler.EngineMaintainedColumns) plus the audit
    # trail (8 failed IN_SUM rows); the FIELDS read-only bit guards the form.
    $fieldGuards = @(
        [pscustomobject]@{
            Id = 'FG-01'; ModuleId = '1302'; ModuleName = '料件批号资料'; Stage = 'MANUAL'; SeqNo = 1
            Key = 'master-field-write'; Enabled = $true
            Message = '字段“IN_SUM”由引擎维护，不允许人工修改。'; MessageSource = 'code'
            TriggerSummary = '参数折算列名命中引擎维护列集合（IN_SUM）即拒'
            EvidenceSource = 'MasterFieldWriteHandler.EngineMaintainedColumns + AUDIT_EVENT（8 条 IN_SUM 失败行）'
            SuggestedClass = 'fieldGuard'
        },
        [pscustomobject]@{
            Id = 'FG-02'; ModuleId = '1302'; ModuleName = '料件批号资料'; Stage = 'MANUAL'; SeqNo = 1
            Key = 'master-field-write'; Enabled = $true
            Message = '字段“OUT_SUM”由引擎维护，不允许人工修改。'; MessageSource = 'code'
            TriggerSummary = '同 FG-01，列为 OUT_SUM（同属累计列，判据与 IN_SUM 同一条）'
            EvidenceSource = 'MasterFieldWriteHandler.EngineMaintainedColumns + FIELDS.IS_READONLY（引擎维护列只读）'
            SuggestedClass = 'fieldGuard'
        }
    )

    # Two record-state samples: lifecycle columns block edits. Source is code
    # (WorkbenchCommandHandler returns FINISHED_EDIT_FORBIDDEN /
    # CONFIRMED_EDIT_FORBIDDEN before any write).
    $blockers = @(
        [pscustomobject]@{
            Id = 'BL-01'; ModuleId = '-'; ModuleName = '（任意带 FINISHED_TAG 的单据模块）'; Stage = 'EDIT'; SeqNo = 0
            Key = 'record-state'; Enabled = $true
            Message = '记录已结案，禁止编辑（请先取消结案）。'; MessageSource = 'code'
            TriggerSummary = '主表 FINISHED_TAG=1 的记录提交修改即拒'
            EvidenceSource = 'WorkbenchCommandHandler（FINISHED_EDIT_FORBIDDEN）+ 单据生命周期列 FINISHED_TAG'
            SuggestedClass = 'blockers'
        },
        [pscustomobject]@{
            Id = 'BL-02'; ModuleId = '-'; ModuleName = '（任意带 CONFIRM_TAG 的单据模块）'; Stage = 'EDIT'; SeqNo = 0
            Key = 'record-state'; Enabled = $true
            Message = '记录已批核，禁止编辑（请先解批）。'; MessageSource = 'code'
            TriggerSummary = '主表 CONFIRM_TAG=1 的记录提交修改即拒'
            EvidenceSource = 'WorkbenchCommandHandler（CONFIRMED_EDIT_FORBIDDEN）+ 单据生命周期列 CONFIRM_TAG'
            SuggestedClass = 'blockers'
        }
    )

    $all = @($records) + @($fieldGuards) + @($blockers)

    # Self-check: a broken trigger must fail the run instead of shipping.
    $bad = @($all | Where-Object { [string]::IsNullOrWhiteSpace($_.TriggerSummary) -or $_.TriggerSummary -match '解析失败' -or [string]::IsNullOrWhiteSpace($_.EvidenceSource) })
    if ($bad.Count -gt 0) {
        Write-Output ("FAIL 触发条件缺失 " + $bad.Count + " 条：" + (($bad | ForEach-Object { $_.Id }) -join ','))
        exit 1
    }

    # Markdown: review guide first, then one section per candidate.
    $md = @()
    $md += '# 诊断黄金集候选（待审阅）'
    $md += ''
    $md += '本清单是机器从配置事实枚举的候选，不是黄金集。审阅时每条只需回答一句话：'
    $md += '“这条规则在该模块下是否真的会拦人”。会拦就保留，不会拦就标掉。'
    $md += '不需要从零写答案，不需要解释业务背景。'
    $md += ''
    $md += '判据门槛：错误归因 = 0（硬门槛，可以答“证据不足”但绝不能答错）；'
    $md += '拒答率 <= 10%（硬门槛）；命中率 >= 95% 为目标值。'
    $md += ''
    $md += ('候选共 {0} 条：校验规则 {1} 条，字段保护 2 条，状态限制 2 条。' -f $all.Count, $records.Count)
    $md += '标准答案取自库内事实（MESSAGE + 模块名 + PARAM_STRUCT 的触发条件），未改写业务语义。'
    $md += 'custom-validation 族的 MESSAGE 列全为空，其答案取自 PARAM_STRUCT.check.message / check.deleteMessage，已逐条注明。'
    $md += '校验规则样本限定 ENABLED=1 且 MESSAGE 非空（停用或无消息的规则拦不住人，不进候选）。'
    $md += ''
    $md += '## 分布（ENABLED=1 且 MESSAGE 非空；custom-validation 答案在参数内）'
    $md += ''
    $groups = $pool | Group-Object { $_.Key + '|' + $_.Stage } | Sort-Object Name
    foreach ($g in $groups) { $md += ('- {0}：{1} 条' -f $g.Name, $g.Count) }
    $md += ('- custom-validation：答案在参数内（SAVE {0} 条有消息，DELETE {1} 条有消息）' -f @($customPool | Where-Object { $_.Stage -eq 'SAVE' }).Count, @($customPool | Where-Object { $_.Stage -eq 'DELETE' }).Count)
    $md += ''
    $md += '## 候选逐条'
    $md += ''
    foreach ($r in $all) {
        $md += ('### {0}（{1}）' -f $r.Id, $r.SuggestedClass)
        $md += ''
        $md += ('- 模块：{0} {1}' -f $r.ModuleId, $r.ModuleName)
        $md += ('- 阶段 / 序号：{0} / {1}' -f $r.Stage, $r.SeqNo)
        $md += ('- 规则键：{0}' -f $r.Key)
        $md += ('- 启用：{0}' -f ($r.Enabled ? '是' : '否'))
        if ($r.MessageSource -eq 'param') {
            $md += ('- 规则消息（取自 PARAM_STRUCT.check，非 MESSAGE 列，该族 MESSAGE 列为空）：{0}' -f $r.Message)
        } else {
            $md += ('- 规则消息：{0}' -f $r.Message)
        }
        $md += ('- 触发条件：{0}' -f $r.TriggerSummary)
        $md += ('- 证据来源：{0}' -f $r.EvidenceSource)
        $md += ('- 为什么它会拦人：命中上述触发条件时，保存（或对应阶段）被服务端拒绝并返回该消息。')
        $md += ''
    }
    $mdDir = Split-Path -Parent $MarkdownPath
    if ($mdDir -and -not (Test-Path -LiteralPath $mdDir)) { New-Item -ItemType Directory -Path $mdDir | Out-Null }
    $md -join "`r`n" | Set-Content -LiteralPath $MarkdownPath -Encoding utf8

    # JSONL skeleton: reviewed is always false here. Review happens later.
    # source marks db rows vs code-fact rows so no consumer mistakes FG/BL
    # for MODULE_VALIDATION_RULE rows.
    $jsonDir = Split-Path -Parent $JsonlPath
    if ($jsonDir -and -not (Test-Path -LiteralPath $jsonDir)) { New-Item -ItemType Directory -Path $jsonDir | Out-Null }
    $lines = @()
    foreach ($r in $all) {
        $source = 'db'
        if ($r.SuggestedClass -in @('fieldGuard', 'blockers')) { $source = 'code' }
        $obj = [ordered]@{
            id = $r.Id; moduleId = $r.ModuleId; moduleName = $r.ModuleName
            stage = $r.Stage; seq = $r.SeqNo; validationKey = $r.Key
            enabled = [bool]$r.Enabled; message = $r.Message; messageSource = $r.MessageSource
            triggerSummary = $r.TriggerSummary; evidenceSource = $r.EvidenceSource
            suggestedClass = $r.SuggestedClass; source = $source; reviewed = $false
        }
        $lines += ($obj | ConvertTo-Json -Compress -Depth 5)
    }
    $lines -join "`n" | Set-Content -LiteralPath $JsonlPath -Encoding utf8 -NoNewline

    Write-Output ("PASS 候选导出：validation={0} fieldGuard=2 blockers=2 total={1}" -f $records.Count, $all.Count)
    Write-Output ("PASS 产物：{0} / {1}" -f $MarkdownPath, $JsonlPath)
    exit 0
} catch {
    Write-Output ("FAIL 候选导出执行失败：{0}" -f (($_.Exception.Message -replace "`r?`n", ' ').Trim()))
    exit 1
}
