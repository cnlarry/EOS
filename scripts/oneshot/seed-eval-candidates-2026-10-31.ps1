<#
.SYNOPSIS
    一次性｜锚点：模块 3106（助手评估用例）——导入 2026-10-31 的三批候选。

.DESCRIPTION
    把三批候选写进 dbo.ASSISTANT_EVAL_CASE（全部 REVIEW_STATUS='DRAFT'，等用户在界面上审）：
      1) 负样本 N-01..N-06：应当**拒答**的诊断场景（证据冲突 / 同键多规则 / 无规则 /
         规则已停用 / 阶段不符 / 证据不足），expect=deny；
      2) 真实失败样本 OBS-001..OBS-012：由 AUDIT_EVENT（RESULT=0）按
         (模块, 动作, 摘要) 归并，ORIGIN='observed'；
      3) 配置面样本 CFG-01..CFG-05：只用已实现的三条原因码（reverse-note-not-read /
         reverse-kind-unsupported / effect-key-singleton），其余标"触发数据待构造"。
         CFG-06（模块级配置面）**不导入**：该面的归因类别未定，见文件末尾说明。

    幂等：按 CASE_ID 判重，已存在则跳过（不覆盖用户在界面上的改动）。
    不改仓库里的 jsonl——那是离线门禁的输入（见 ADR-045 §2.4）。
#>
param(
    [string] $ConnectionString = $env:MSSQL_ERP_CONN
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($ConnectionString)) { throw '需要 MSSQL_ERP_CONN（或 -ConnectionString）。' }

$cases = @()

# ---------- 1. 负样本：应当拒答 ----------
$cases += @{ id='N-01'; layer='diagnosis'; expected='应当拒答：证据冲突（命中事实与登记规则的阶段/序号不一致），不得套用相似规则'; origin='draft'
    payload=@{ expect='deny'; reasonKind='evidence-conflict'
               registered=@{ moduleId=1407; stage='APPROVE'; seq=1; validationKey='line-require'; message='以下序号项请输入不良品存放仓库' }
               hitFact=@{ moduleId=1407; stage='SAVE'; seq=1; validationKey='line-require' }
               note='服务端已有对应用例（判据身份对不上时报证据冲突而不是套用相似规则）' } }
$cases += @{ id='N-02'; layer='diagnosis'; expected='应当拒答：同一阶段同一键下有多条规则（seq 1/3/4），只给键与部分证据时不得任选一条当原因'; origin='draft'
    payload=@{ expect='deny'; reasonKind='ambiguous-key'
               registered=@{ moduleId=1407; stage='DEAPPROVE'; validationKey='qty-not-exceed'; seqs=@(1,3,4) }
               hitFact=@{ moduleId=1407; stage='DEAPPROVE'; validationKey='qty-not-exceed'; seq=2 }
               note='seq=2 在登记里不存在：应报证据冲突，不能就近匹配到 1/3/4' } }
$cases += @{ id='N-03'; layer='diagnosis'; expected='应当拒答：该模块没有任何校验规则可归因，不得编造原因'; origin='draft'
    payload=@{ expect='deny'; reasonKind='no-rule'; registered=@{ moduleId=110105; stage='SAVE' }; hitFact=@{ moduleId=110105; stage='SAVE'; validationKey='reference-exists' }
               note='110105 银行帐号资料在 MODULE_VALIDATION_RULE 里没有规则' } }
$cases += @{ id='N-04'; layer='diagnosis'; expected='应当拒答：命中的规则在库里已停用（ENABLED=0），不能把它当成用户看到的原因'; origin='draft'
    payload=@{ expect='deny'; reasonKind='disabled-rule'; registered=@{ moduleId=180310; stage='SAVE'; seq=1; validationKey='duplicate-check'; enabled=$false }
               hitFact=@{ moduleId=180310; stage='SAVE'; seq=1; validationKey='duplicate-check' }; note='180310 该规则 ENABLED=0' } }
$cases += @{ id='N-05'; layer='diagnosis'; expected='应当拒答：命中事实的阶段与该模块登记的规则阶段不符（APPROVE vs SAVE）'; origin='draft'
    payload=@{ expect='deny'; reasonKind='stage-mismatch'; registered=@{ moduleId=1413; stage='SAVE'; seq=1; validationKey='reference-exists' }
               hitFact=@{ moduleId=1413; stage='APPROVE'; seq=1; validationKey='reference-exists' } } }
$cases += @{ id='N-06'; layer='diagnosis'; expected='应当拒答：证据不足（只有键名、没有阶段/序号），不得凭键名猜规则'; origin='draft'
    payload=@{ expect='deny'; reasonKind='insufficient-evidence'; registered=@{ moduleId=1204; stage='SAVE'; seq=1; validationKey='reference-exists' }
               hitFact=@{ moduleId=1204; validationKey='reference-exists' } } }

# ---------- 2. 真实失败样本：AUDIT_EVENT(RESULT=0) 归并 ----------
$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @'
SELECT TOP 14 M_IDX, ACTION, LEFT(SUMMARY, 200) AS S, COUNT(*) AS N,
       MIN(CONVERT(varchar(19), OCCURRED_AT, 120)) AS FIRST_AT,
       MAX(CONVERT(varchar(19), OCCURRED_AT, 120)) AS LAST_AT
FROM dbo.AUDIT_EVENT WITH (NOLOCK)
WHERE RESULT = 0 AND ACTION NOT IN ('pipeline-probe', 'live-test-failing')
GROUP BY M_IDX, ACTION, LEFT(SUMMARY, 200)
ORDER BY COUNT(*) DESC;
'@
$reader = $cmd.ExecuteReader()
$i = 0
while ($reader.Read()) {
    $i++
    $moduleId = $reader.GetInt32(0)
    $action = $reader.GetString(1)
    $summary = $reader.GetString(2)
    $cases += @{ id = ('OBS-{0:D3}' -f $i); layer='diagnosis'; expected=$summary; origin='observed'
        payload = @{ expect='observed-rejection'; moduleId=$moduleId; action=$action; summary=$summary
                     occurrences=$reader.GetInt32(3); firstAt=$reader.GetString(4); lastAt=$reader.GetString(5); source='AUDIT_EVENT.RESULT=0' } }
}
$reader.Close()
$conn.Close()

# ---------- 3. 配置面样本：只用已实现的三条原因码 ----------
$cases += @{ id='CFG-01'; layer='config'; expected='应当检出 effect-key-singleton：该效果键全库只用过一次，与同类键相比更像重复造键'; origin='observed'
    payload=@{ surface='effect'; effectKey='bom-size-backfill'; expectCode='effect-key-singleton'; observed=@{ effectKeyUseCount=1 } } }
$cases += @{ id='CFG-02'; layer='config'; expected='应当检出 effect-key-singleton：client-price-sync 仅被一个模块使用'; origin='observed'
    payload=@{ surface='effect'; effectKey='client-price-sync'; expectCode='effect-key-singleton'; observed=@{ effectKeyUseCount=1 } } }
$cases += @{ id='CFG-03'; layer='config'; expected='应当检出 reverse-note-not-read：反向说明写在 note 里，代码不读它'; origin='draft'
    payload=@{ surface='effect'; effectKey='field-accumulate'; expectCode='reverse-note-not-read'; observed=@{ reverseStruct='{"kind":"restore-previous","note":"说明写在 note 里"}' }
               note='触发数据待构造：需要一条把反向说明写进 note 的真实配置' } }
$cases += @{ id='CFG-04'; layer='config'; expected='应当检出 reverse-kind-unsupported：反向 kind 不在该效果键的支持集内'; origin='draft'
    payload=@{ surface='effect'; effectKey='car-filloil-sync'; expectCode='reverse-kind-unsupported'; observed=@{ reverseKind='change-apply' }
               note='触发数据待构造：需要一条反向 kind 越界的真实配置' } }
$cases += @{ id='CFG-05'; layer='config'; expected='不应有检出：虚拟字段带表达式、非虚拟字段空表达式都合法'; origin='draft'
    payload=@{ surface='fields'; isVirtual=$true; virtualExp='COP_ORDER_M.ORDER_DATE'; expectCode=$null } }

# CFG-06（配置面「module」）不导入：配置面的四个面是 fields/datasource/buttons/effect，模块级
# 归因类别尚未定义（ADR-016 已知缺口 #6）。只登记场景、不含判据的样本会被确定性层判成恒失败，
# 等于给运行器塞一条永远红的用例；要它先定归因类别，再按判据重写。

# ---------- 写库（按 CASE_ID 判重，不覆盖既有行） ----------
$insert = @'
INSERT INTO dbo.ASSISTANT_EVAL_CASE
    (CASE_ID, LAYER, QUESTION, EXPECTED, EXPECT_TOOLS, PAGE_TYPE, PAYLOAD_JSON, REVIEW_STATUS, ORIGIN, CASE_VERSION, REMARK, CREATED_BY, CREATED_TIME)
SELECT @Id, @Layer, NULL, @Expected, NULL, NULL, @Payload, N'DRAFT', @Origin, 1, @Remark, N'seed-3106', GETDATE()
WHERE NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_EVAL_CASE WHERE CASE_ID = @Id);
'@

$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
$inserted = 0
foreach ($case in $cases) {
    $payload = ($case.payload | ConvertTo-Json -Compress -Depth 10)
    $c = $conn.CreateCommand()
    $c.CommandText = $insert
    [void]$c.Parameters.AddWithValue('@Id', $case.id)
    [void]$c.Parameters.AddWithValue('@Layer', $case.layer)
    [void]$c.Parameters.AddWithValue('@Expected', [string]$case.expected)
    [void]$c.Parameters.AddWithValue('@Payload', $payload)
    [void]$c.Parameters.AddWithValue('@Origin', $case.origin)
    [void]$c.Parameters.AddWithValue('@Remark', [string]($case.payload.note))
    $inserted += [int]$c.ExecuteNonQuery()
}
$check = $conn.CreateCommand()
$check.CommandText = "SELECT COUNT(*) FROM dbo.ASSISTANT_EVAL_CASE"
$total = [int]$check.ExecuteScalar()
$conn.Close()

"candidates=$($cases.Count) inserted=$inserted totalInTable=$total"
