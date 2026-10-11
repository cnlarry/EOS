#Requires -Version 7.0
<#
.SYNOPSIS
    脚本台账：把 scripts/ 下每个 ps1 的作用、类别、依赖与引用点汇成一张表，并守住"不写说明、无人引用"的堆积。

.DESCRIPTION
    背景：scripts/ 下积了近百个脚本，多数由不同时期的 Agent 为一次性任务写下。人（与后来的 Agent）
    分不清哪些是**仍在生效的门禁**、哪些是**使命已完结的批次工具**、哪些是**没人再引用的孤岛**，
    于是三件事同时发生：不敢删（怕删掉防线）、误当门禁跑（浪费）、重复造轮子（第二个真源）。

    本脚本干两件事：

      1. 生成台账 `docs/guide/_index/scripts.md`——每行一个脚本：作用（从头注释抽取）、类别、依赖、引用点数、最近改动；
      2. `-Check` 校验三条确定性事实（CI 用这一条）：
           A. 每个脚本都有**可提取的说明**（`.SYNOPSIS`，或文件头块注释，或文件头 `#` 注释）；
           B. 每个脚本**被引用过**——CI、手册、技能或其它脚本提到过它的文件名；
              确实独立、不必被引用的，登记进本脚本的 `$StandaloneTools`（带理由，只减不增）；
           C. 台账与磁盘现状一致（新增/改名脚本后没重新生成即红）。

    只提示、不失败的三类：说明不是 `.SYNOPSIS`（工具取不到，但人读得到）、
    门禁类脚本既不在 CI 也没在 `docs/guide/06` 里登记（**待处置**：要么登记、要么退役，需人判断）、
    白名单里已可移除的条目。

    类别是**按证据推导**的（口径写在台账头部），不是人逐条标注：在 CI 里跑 → 门禁（CI）；
    `check-` / `verify-` 前缀 → 门禁（本地按需）；一次性前缀（`adr*` / `batch-` / `clean-` /
    `restore-` / `rollback-` / `migrate-` / `backfill-` / `materialize-` / `metadata-disposition` /
    `compare-` / `analyze-`）→ 一次性批次；其余 → 工具。推导不出来的少数几条由 `$KindOverrides` 覆盖。

    引用统计的**扫描范围刻意排除 `docs/plans/` 与生成的台账本身**：前者不随仓库分发（在别人克隆里
    不存在，不能当证据），后者会引用每一个脚本（否则 B 条会被自己的产物弄成恒真）。
    也不扫 `EOS.API.Tests/*.ps1`（本机资产，CI 上没有）。

.PARAMETER OutFile
    台账落盘路径（相对仓库根），默认 `docs/guide/_index/scripts.md`。

.PARAMETER Check
    只校验、不写文件；任一确定性事实不成立即退出码 1。

.PARAMETER Json
    打印结构化条目（供 AI 助手或其它脚本消费），不写台账。

.PARAMETER SelfTest
    正反自检：用合成文本驱动说明抽取、依赖识别与类别推导三个纯函数。

.EXAMPLE
    pwsh scripts/script-inventory.ps1              # 重新生成台账并打印总览
    pwsh scripts/script-inventory.ps1 -Check       # 校验台账与约束（CI 用这条）
    pwsh scripts/script-inventory.ps1 -Json        # 机器可读
    pwsh scripts/script-inventory.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $OutFile = 'docs/guide/_index/scripts.md',
    [switch] $Check,
    [switch] $Json,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 本脚本的文件名：引用扫描要把它自己排除掉（见 Get-ReferenceUniverse）。
$SelfName = Split-Path -Leaf $PSCommandPath

# 独立工具白名单：这些脚本**不**被 CI / 手册 / 技能 / 其它脚本引用是正常的（一次性批次，保留作留痕）。
# 只减不增：条目已被引用、或脚本已删除时提示移除；**新增条目要写清理由**。
$StandaloneTools = @{
    'analyze-button-coverage.ps1'      = '旧 Web Forms 页面按钮盘点（参考系统分析），一次性'
    'backfill-chooser.ps1'             = '选择器缺口回填（存量推进），一次性'
    'batch-validate-form-rework.ps1'   = 'ADR-006 步骤 5 存量表单批量校验，一次性'
    'compare-report-rowset-parity.ps1' = '报表归属迁移的取数对拍，迁移已完结'
    'export-config-snapshots.ps1'      = '统一表单配置快照导出（D6 固化），一次性'
    'init-im-db.ps1'                   = 'EOS.IM 开发库初始化（读本机 Codex 配置里的连接串），dev-only 辅助，非通用流程'
    'materialize-form-layouts.ps1'     = '把推导默认版式落成版式行，字段级排布列退役的前置，已完成'
    'metadata-disposition.ps1'         = '元数据异常清单的阶段 1 处置，一次性'
    'migrate-menu-filters.ps1'         = '存量菜单过滤规则语法归一，一次性'
    'rollback-report-attribution.ps1'  = '报表归属迁移的回滚脚本，保留作回退手段'
    'verify-adr024-remainder.ps1'      = 'ADR-024 收尾批的落库与运行态复验，使命已完结'
    'register-field-relation-edges.ps1' = '单据关系效果边登记器：与已退役的全量核验脚本成对，保留作重登记手段（保存 lint 只在保存时校验，不覆盖存量重登记）'
}

# 类别推导的人工例外（推导规则见文件头；这里只放规则判错的少数几条）。
$KindOverrides = @{
    'verify-adr024-remainder.ps1' = '一次性批次'   # 名字带 verify，实为某次收口的复验，不是常态门禁
}

$KindOrder = @('门禁（CI）', '门禁（本地按需）', '一次性批次', '工具')

# 一次性脚本的落点与棘轮（"用完即废"的机械部分）：
#   · 新的一次性脚本一律落 scripts/oneshot/——目录即状态，这里随时可以整目录清空，不影响任何门禁；
#   · 该目录下每个脚本头部必须写一行锚点，说明它随哪件收口完成而失效（机器只能校验"写了"，"用完了没有"要人判断）；
#   · scripts/ 根目录下的历史一次性脚本按数量棘轮执行（只减不增）：新增请落 oneshot/，或先删掉被它替代的那个。
# 存量**不搬迁**：ADR 与手册里引的是旧路径，搬动只会造出一批死链。
$OneshotPrefix = 'scripts/oneshot/'
$LegacyOneshotBaseline = 16
$AnchorPattern = '(?m)^\s*#\s*一次性\s*[｜|]\s*锚点[：:]\s*(?<anchor>\S+)'

# ---------- 纯函数：说明抽取 ----------
function Get-ScriptDescription {
    <#
      返回 @{ Text = 一句话说明; Source = SYNOPSIS | BLOCK | HASH | NONE }
      优先 `.SYNOPSIS`（工具可 Get-Help 取），其次文件头块注释，再其次文件头 `#` 注释。
      三种都没有时 Source = NONE —— 那才是真的"看不出它干什么"。
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $lines = $Text -split "`r?`n"

    # .SYNOPSIS 只会出现在帮助块里，直接扫；其后的续行拼到遇到下一个指令为止。
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\.SYNOPSIS\s*$') {
            $parts = [System.Collections.Generic.List[string]]::new()
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                $line = $lines[$j]
                if ($line -match '^\s*\.\w' -or $line -match '^\s*#>') { break }
                $t = $line.Trim()
                if ($t -eq '') { if ($parts.Count -gt 0) { break } else { continue } }
                $parts.Add($t)
            }
            $syn = ($parts -join ' ')
            if ($syn -ne '') { return @{ Text = $syn; Source = 'SYNOPSIS' } }
        }
    }

    # 头注释取"出现位置更靠前的那个"。块注释在代码里也可能出现（载荷说明、内联示例），
    # 只按"有没有"取会取到后文，把真正的头注释丢掉。
    $hashLine = -1
    $hashBuf = [System.Collections.Generic.List[string]]::new()
    $inHash = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].Trim()
        if ($t -eq '') { if ($inHash) { break } else { continue } }
        if ($t -match '^#!') { continue }
        if ($t -match '^#') {
            if (-not $inHash) { $inHash = $true; $hashLine = $i }
            $body = $t.TrimStart('#').Trim()
            if ($body -ne '') { $hashBuf.Add($body) }
            continue
        }
        break
    }

    $blockLine = -1
    $blockText = ''
    foreach ($m in [regex]::Matches($Text, '(?s)<#(.*?)#>')) {
        $first = ''
        foreach ($line in ($m.Groups[1].Value -split "`r?`n")) {
            $t = $line.Trim().TrimStart('.')
            if ($t -eq '' -or $t -match '^(SYNOPSIS|DESCRIPTION|PARAMETER|EXAMPLE|OUTPUTS|NOTES)\b') { continue }
            $first = $t
            break
        }
        if ($first -ne '') {
            $blockLine = @($Text.Substring(0, $m.Index) -split "`n").Count - 1
            $blockText = $first
            break
        }
    }

    if ($hashBuf.Count -gt 0 -and ($blockText -eq '' -or $hashLine -lt $blockLine)) {
        return @{ Text = ($hashBuf -join ' '); Source = 'HASH' }
    }
    if ($blockText -ne '') { return @{ Text = $blockText; Source = 'BLOCK' } }

    return @{ Text = ''; Source = 'NONE' }
}

# ---------- 纯函数：一次性脚本的锚点 ----------
function Get-OneshotAnchor {
    <#
      从脚本头部解析 `# 一次性｜锚点：<迁移 NNN / ADR-0xx / 模块号>`，取锚点值；没有则返回空串。
      锚点回答的是"它随哪件收口完成而失效"——门禁只能校验它写没写，"用完了没有"要人判断。
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $m = [regex]::Match($Text, $AnchorPattern)
    if ($m.Success) { return $m.Groups['anchor'].Value }
    return ''
}

# ---------- 纯函数：退役清单（scripts/README.md 的「已退役脚本」小节） ----------
function Get-RetiredScripts {
    <#
      从 scripts/README.md 的「已退役脚本」小节解析第一列里的脚本名。
      退役记录只有这一处（ADR 与已应用的迁移不追改），所以它必须可机检：
      "说好退役了" ⇒ 断言磁盘上确实没有它；"又冒出来了" ⇒ FAIL。
    #>
    param([string] $ReadmePath)

    $names = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $ReadmePath)) { return $names }
    $inSection = $false
    foreach ($line in (Get-Content -LiteralPath $ReadmePath -Encoding UTF8)) {
        if ($line -match '^##\s') {
            $inSection = ($line -match '已退役脚本')
            continue
        }
        if (-not $inSection -or $line -notmatch '^\s*\|') { continue }
        $cells = @($line.Trim().Trim('|') -split '\|')
        $m = [regex]::Match($cells[0], '`([A-Za-z0-9_.-]+\.ps1)`')
        if ($m.Success) { $names.Add($m.Groups[1].Value) }
    }
    return $names
}

# ---------- 纯函数：台账指纹 ----------
function Get-InventoryFingerprint {
    <#
      台账"造册语义"的指纹：脚本集合 + 类别 + 说明 + 锚点 + 独立工具状态 + 白名单 + 无人引用集合 + 退役集合。
      **特意不含**引用点数与最近改动——它们随无关改动（有人在别处提一句脚本名、一次提交）而变，
      拿它们比对会让 CI 在不相干的 PR 上乱红。指纹对不上才说明"该重新生成台账了"。
    #>
    param(
        [object[]] $Entries,
        [hashtable] $Standalone,
        [string[]] $Retired
    )

    $sb = [System.Text.StringBuilder]::new()
    foreach ($e in ($Entries | Sort-Object Name)) {
        $null = $sb.AppendLine(@($e.Name, $e.Kind, $e.DescSource, $e.Description, $e.Anchor, $e.Standalone, $e.IsOneshot) -join '|')
    }
    $null = $sb.AppendLine('WHITELIST:' + (@($Standalone.Keys | Sort-Object) -join ','))
    $null = $sb.AppendLine('ORPHANS:' + (@($Entries | Where-Object RefCount -eq 0 | Select-Object -ExpandProperty Name | Sort-Object) -join ','))
    $null = $sb.AppendLine('RETIRED:' + (@($Retired | Sort-Object) -join ','))

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($hash) -replace '-', '').Substring(0, 16).ToLowerInvariant()
}

# ---------- 纯函数：依赖识别 ----------
function Get-ScriptDependencies {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $deps = [System.Collections.Generic.List[string]]::new()
    if ($Text -match 'eos-sql\.ps1|Invoke-Sqlcmd|\bsqlcmd\b|SqlConnection|ConnectionString|MSSQL_ERP_CONN') { $deps.Add('连库') }
    if ($Text -match '5261|Invoke-RestMethod|Invoke-WebRequest|api/v1') { $deps.Add('需 API 在线') }
    if ($Text -match '(?m)^\s*&?\s*git\b|\bgit -C\b') { $deps.Add('git') }
    if ($Text -match '\bdotnet\b|\bnpm\b|\bnpx\b') { $deps.Add('工具链') }
    if ($deps.Count -eq 0) { $deps.Add('仅静态') }
    return @($deps)
}

# ---------- 纯函数：类别推导 ----------
function Get-ScriptKind {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][bool] $InCi,
        [hashtable] $Overrides = @{}
    )

    if ($Overrides.ContainsKey($Name)) { return $Overrides[$Name] }
    if ($InCi) { return '门禁（CI）' }
    if ($Name -match '^(adr\d|batch-|clean-|restore-|rollback-|migrate-|backfill-|materialize-|metadata-disposition|compare-|analyze-)') {
        return '一次性批次'
    }
    if ($Name -match '^(check|verify)-') { return '门禁（本地按需）' }
    return '工具'
}

# ---------- 自检 ----------
if ($SelfTest) {
    $sample = @'
<#
.SYNOPSIS
    合成样本：说明会被抽成一句话。
.DESCRIPTION
    其余段落不参与抽取。
#>
param([string]$X)
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
$r = Invoke-RestMethod -Uri 'http://localhost:5261/api/v1/x'
& git -C . status
dotnet build
'@
    $desc = Get-ScriptDescription -Text $sample
    if ($desc.Source -ne 'SYNOPSIS') { throw "自检失败：应识别为 SYNOPSIS，实际 $($desc.Source)" }
    if ($desc.Text -ne '合成样本：说明会被抽成一句话。') { throw "自检失败：抽取内容不对：$($desc.Text)" }
    $deps = @(Get-ScriptDependencies -Text $sample)
    foreach ($need in '连库', '需 API 在线', 'git', '工具链') {
        if ($deps -notcontains $need) { throw "自检失败：合成样本应识别出依赖「$need」，实际 $($deps -join '/')" }
    }
    Write-Host '  [PASS] 正向自检：.SYNOPSIS 抽取与四类依赖识别'

    # 无 .SYNOPSIS、只有头注释时，说明仍要抽得出来（否则会把"有注释"误判成"没说明"）
    $hashOnly = "# 头注释第一行：把说明写在 # 里也应被抽出。`n# 第二行补一句。`n`nparam([string]`$X)`nWrite-Output x"
    $hashDesc = Get-ScriptDescription -Text $hashOnly
    if ($hashDesc.Source -ne 'HASH') { throw "自检失败：应回退到头注释，实际 $($hashDesc.Source)" }
    if ($hashDesc.Text -notlike '头注释第一行*') { throw "自检失败：头注释抽取内容不对：$($hashDesc.Text)" }

    $blockOnly = "<#`n    块注释里的说明。`n#>`nparam()"
    $blockDesc = Get-ScriptDescription -Text $blockOnly
    if ($blockDesc.Source -ne 'BLOCK') { throw "自检失败：应回退到 <# #> 块，实际 $($blockDesc.Source)" }

    $bare = "param([string]`$X)`nWrite-Output x"
    $bareDesc = Get-ScriptDescription -Text $bare
    if ($bareDesc.Source -ne 'NONE') { throw "自检失败：完全没有说明时应判 NONE，实际 $($bareDesc.Source)" }
    Write-Host '  [PASS] 回退自检：块注释 / 头注释 / 无说明三档判定正确'

    $staticOnly = @(Get-ScriptDependencies -Text 'Write-Output 1')
    if ($staticOnly.Count -ne 1 -or $staticOnly[0] -ne '仅静态') { throw '自检失败：无外部依赖时应判"仅静态"' }
    Write-Host '  [PASS] 反向自检：无外部依赖判"仅静态"'

    # 类别推导的次序：CI 优先，其次一次性前缀，再其次 check/verify 前缀
    $cases = @(
        @{ Name = 'check-x.ps1';         InCi = $true;  Expect = '门禁（CI）' }
        @{ Name = 'check-x.ps1';         InCi = $false; Expect = '门禁（本地按需）' }
        @{ Name = 'adr012-b1-verify.ps1'; InCi = $false; Expect = '一次性批次' }
        @{ Name = 'compare-x.ps1';       InCi = $false; Expect = '一次性批次' }
        @{ Name = 'export-x.ps1';        InCi = $false; Expect = '工具' }
    )
    foreach ($c in $cases) {
        $got = Get-ScriptKind -Name $c.Name -InCi $c.InCi -Overrides @{}
        if ($got -ne $c.Expect) { throw "自检失败：$($c.Name) 期望「$($c.Expect)」实际「$got」" }
    }
    Write-Host '  [PASS] 类别自检：CI → 一次性前缀 → check/verify 前缀 的判定次序正确'

    # 一次性脚本的锚点：写了要能取到，"没写"要能判出来（门禁靠这条断言）
    $anchored = "# 一次性｜锚点：迁移 345`n# 把某次收口的一次性脚本`nparam()"
    if ((Get-OneshotAnchor -Text $anchored) -ne '迁移') {
        throw "自检失败：锚点应取到「迁移」，实际「$(Get-OneshotAnchor -Text $anchored)」"
    }
    $anchored2 = "# 一次性 | 锚点: ADR-024`nparam()"
    if ((Get-OneshotAnchor -Text $anchored2) -ne 'ADR-024') {
        throw "自检失败：半角分隔符写法应同样识别，实际「$(Get-OneshotAnchor -Text $anchored2)」"
    }
    if ((Get-OneshotAnchor -Text "param()`nWrite-Output 1") -ne '') {
        throw '自检失败：没有锚点行时应返回空串'
    }
    Write-Host '  [PASS] 锚点自检：全角/半角分隔符都能取到，缺失时判空'

    # 退役清单解析：只认「已退役脚本」小节里的、第一列带反引号的脚本名
    $sampleReadme = @'
# scripts/

## 一、三个入口

| 跑什么 |
|---|
| `pwsh scripts/impact.ps1` |

## 四、已退役脚本

| 已退役脚本 | 它守的不变式 |
|---|---|
| `check-gone.ps1` | 合成样本 |
| 没有反引号的行走文 | 应被忽略 |

## 五、其它
'@
    $probeReadme = Join-Path ([System.IO.Path]::GetTempPath()) 'eos-retired-probe.md'
    try {
        Set-Content -LiteralPath $probeReadme -Encoding utf8 -Value $sampleReadme
        $parsed = @(Get-RetiredScripts -ReadmePath $probeReadme)
        if ($parsed -ne @('check-gone.ps1')) { throw "自检失败：退役清单应解析出 1 条，实际 $($parsed -join '/')" }
    } finally {
        Remove-Item -LiteralPath $probeReadme -Force -ErrorAction SilentlyContinue
    }
    Write-Host '  [PASS] 退役清单自检：只认小节内的反引号脚本名'

    # 指纹：同类输入必须同值（否则 CI 会随机红）；造册语义一变必须变值（否则台账落后无人知）
    $mkEntry = {
        param([string] $Kind, [int] $RefCount)
        [pscustomobject]@{
            Name = 'a.ps1'; Kind = $Kind; DescSource = 'SYNOPSIS'; Description = 'x'
            Anchor = ''; Standalone = $false; IsOneshot = $false; RefCount = $RefCount
        }
    }
    $fp1 = Get-InventoryFingerprint -Entries @(& $mkEntry '工具' 3) -Standalone @{} -Retired @('gone.ps1')
    $fp2 = Get-InventoryFingerprint -Entries @(& $mkEntry '工具' 3) -Standalone @{} -Retired @('gone.ps1')
    if ($fp1 -ne $fp2) { throw '自检失败：同一输入两次指纹不一致（CI 会随机红）' }
    $fpChanged = Get-InventoryFingerprint -Entries @(& $mkEntry '门禁（CI）' 3) -Standalone @{} -Retired @('gone.ps1')
    if ($fpChanged -eq $fp1) { throw '自检失败：类别变了指纹却没变（台账落后不会被发现）' }
    $fpRefCount = Get-InventoryFingerprint -Entries @(& $mkEntry '工具' 99) -Standalone @{} -Retired @('gone.ps1')
    if ($fpRefCount -ne $fp1) { throw '自检失败：引用点数不该进指纹——它随无关改动而变，进了会让 CI 乱红' }
    Write-Host '  [PASS] 指纹自检：同输入同值、类别变则变、引用点数不进指纹'

    Write-Host '-- SELFTEST OK'
    exit 0
}

# ---------- 引用扫描范围 ----------
function Get-ReferenceUniverse {
    <#
      参与"是否被引用"判定的文件。刻意排除：
        · docs/plans/        —— 不随仓库分发，在别人克隆里不存在，不能当证据；
        · docs/guide/_index —— 生成的台账本身（它会引用每个脚本，会让判定恒真）；
        · 本脚本自身         —— 白名单里的脚本名同样会让判定恒真（"我登记了它"不等于"有人用它"）；
        · EOS.API.Tests/*.ps1 —— 本机资产（CI 上没有），纳入会让结果随机器而变；
        · node_modules / bin / obj / dist / logs 等构建与运行产物。
    #>
    param([string] $RepoRoot)

    $selfName = $SelfName
    $dirs = @('.github', 'docs', 'scripts', '.agents')
    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($d in $dirs) {
        $full = Join-Path $RepoRoot $d
        if (-not (Test-Path -LiteralPath $full)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $full -Recurse -File -ErrorAction SilentlyContinue) {
            if ($f.Extension -notin '.ps1', '.md', '.yml', '.yaml', '.json', '.txt', '.mjs') { continue }
            if ($f.Name -eq $selfName) { continue }
            $rel = $f.FullName.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            if ($rel -match '^(docs/plans/|docs/guide/_index/|.*/(node_modules|bin|obj|dist)/)') { continue }
            $files.Add($f.FullName)
        }
    }
    foreach ($name in 'AGENTS.md', 'CONTRIBUTING.md', 'README.md', 'LESSONS.md', 'ARCHITECTURE.md', 'QUICKSTART.md') {
        $p = Join-Path $RepoRoot $name
        if (Test-Path -LiteralPath $p) { $files.Add($p) }
    }
    return @($files)
}

function Get-ReferencedNames {
    <# 返回 @{ 小写脚本名 = HashSet[引用它的文件全路径] } #>
    param([string[]] $Files)

    $refs = @{}
    foreach ($file in $Files) {
        $text = Get-Content -LiteralPath $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($null -eq $text) { continue }
        foreach ($m in [regex]::Matches($text, '[A-Za-z0-9_.-]+\.ps1')) {
            $key = $m.Value.ToLowerInvariant()
            if (-not $refs.ContainsKey($key)) { $refs[$key] = [System.Collections.Generic.HashSet[string]]::new() }
            $null = $refs[$key].Add($file)
        }
    }
    return $refs
}

function Get-LastCommitDates {
    <# 单次 git log 扫出 scripts/ 下每个文件的最近提交日期，避免逐文件起 git 进程。 #>
    param([string] $RepoRoot)

    $map = @{}
    $current = ''
    foreach ($line in @(& git -C $RepoRoot log --format='C|%cs' --name-only -- scripts 2>$null)) {
        if (-not $line) { continue }
        if ($line.StartsWith('C|')) { $current = $line.Substring(2); continue }
        $rel = ($line.Trim() -replace '\\', '/')
        if ($rel.StartsWith('scripts/') -and -not $map.ContainsKey($rel)) { $map[$rel] = $current }
    }
    return $map
}

# ---------- 采集 ----------
$scriptsDir = Join-Path $Root 'scripts'
if (-not (Test-Path -LiteralPath $scriptsDir)) { throw "找不到脚本目录：$scriptsDir" }

$ciPath = Join-Path $Root '.github/workflows/ci.yml'
$ciText = if (Test-Path -LiteralPath $ciPath) { Get-Content -Raw -Encoding UTF8 $ciPath } else { '' }
$guide06Path = Join-Path $Root 'docs/guide/06-测试与质量门禁.md'
$guide06Text = if (Test-Path -LiteralPath $guide06Path) { Get-Content -Raw -Encoding UTF8 $guide06Path } else { '' }

$universe = Get-ReferenceUniverse -RepoRoot $Root
$refs = Get-ReferencedNames -Files $universe
$dates = Get-LastCommitDates -RepoRoot $Root

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $scriptsDir -Recurse -File -Filter '*.ps1' | Sort-Object FullName)) {
    $rel = ($file.FullName.Substring($Root.Length).TrimStart('\', '/') -replace '\\', '/')
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $desc = Get-ScriptDescription -Text $text
    $inCi = $ciText -match [regex]::Escape($file.Name)
    $key = $file.Name.ToLowerInvariant()
    $refCount = if ($refs.ContainsKey($key)) {
        @($refs[$key] | Where-Object { $_ -ne $file.FullName }).Count
    } else { 0 }
    $isOneshot = $rel.StartsWith($OneshotPrefix)

    $entries.Add([pscustomobject]@{
            Name        = $file.Name
            Path        = $rel
            Description = $desc.Text
            DescSource  = $desc.Source
            IsOneshot   = $isOneshot
            Anchor      = if ($isOneshot) { Get-OneshotAnchor -Text $text } else { '' }
            Kind        = if ($isOneshot) { '一次性批次' } else { Get-ScriptKind -Name $file.Name -InCi $inCi -Overrides $KindOverrides }
            Dependencies = @(Get-ScriptDependencies -Text $text)
            InCi        = $inCi
            InGuide06   = ($guide06Text -match [regex]::Escape($file.Name))
            RefCount    = $refCount
            Standalone  = $StandaloneTools.ContainsKey($file.Name)
            LastCommit  = if ($dates.ContainsKey($rel)) { $dates[$rel] } else { '' }
        })
}

$fileCount = @(Get-ChildItem -LiteralPath (Join-Path $Root 'EOS.API.Tests') -Recurse -File -Filter '*.ps1' -ErrorAction SilentlyContinue).Count

# ---------- 退役清单 ----------
$readmePath = Join-Path $scriptsDir 'README.md'
$retired = @(Get-RetiredScripts -ReadmePath $readmePath)
$retiredBack = @($retired | Where-Object { Test-Path -LiteralPath (Join-Path $scriptsDir $_) })
$retiredStillRegistered = @($retired | Where-Object { $StandaloneTools.ContainsKey($_) -or $KindOverrides.ContainsKey($_) })

# ---------- 台账渲染 ----------
function Format-Cell {
    param([string] $Value, [int] $Max = 120)
    $v = ($Value -replace '\s+', ' ').Trim() -replace '\|', '\|'
    if ($v.Length -gt $Max) { $v = $v.Substring(0, $Max - 1) + '…' }
    return $v
}

function New-LedgerLines {
    param(
        [object[]] $Entries,
        [hashtable] $Standalone,
        [int] $LocalTestScripts,
        [string[]] $Retired = @()
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!-- inventory-fingerprint: ' + (Get-InventoryFingerprint -Entries $Entries -Standalone $Standalone -Retired $Retired) + ' -->')
    $lines.Add('# 脚本台账（`scripts/`）')
    $lines.Add('')
    $lines.Add('> 本文件由 [`scripts/script-inventory.ps1`](../../../scripts/script-inventory.ps1) **生成**，不要手改；')
    $lines.Add('> `pwsh scripts/script-inventory.ps1 -Check` 在 CI 里校验它与脚本目录一致（新增脚本后没重新生成即红）。')
    $lines.Add('> **目录制度与已退役脚本见 [`scripts/README.md`](../../../scripts/README.md)**——退役清单只有那一处。')
    $lines.Add('>')
    $lines.Add('> **类别是按证据推导的**，不是逐条人工标注：在 CI 里跑 → 门禁（CI）；`check-` / `verify-` 前缀 →')
    $lines.Add('> 门禁（本地按需）；一次性前缀（`adr*` / `batch-` / `clean-` / `restore-` / `rollback-` / `migrate-` /')
    $lines.Add('> `backfill-` / `materialize-` / `metadata-disposition` / `compare-` / `analyze-`）→ 一次性批次；其余 → 工具。')
    $lines.Add('>')
    $lines.Add('> **作用**取自脚本自身：优先 `.SYNOPSIS`，其次文件头 `<# … #>` 块，再其次文件头 `#` 注释。')
    $lines.Add('> **引用**是"CI / 手册 / 技能 / 其它脚本里提到过它的文件名"的点数——扫描范围排除 `docs/plans/`（不随仓库')
    $lines.Add('> 分发）与本台账自身；它是**生成时刻的快照**，随时可能因别处提一句而变，不必为它重生成。')
    $lines.Add('>')
    $lines.Add('> 文件开头的 `inventory-fingerprint` 是**造册语义**的指纹（脚本集合 / 类别 / 说明 / 锚点 / 独立工具状态 /')
    $lines.Add('> 白名单 / 无人引用集合 / 退役集合），`-Check` 比的就是它——**不含**引用点数与时间戳，')
    $lines.Add('> 否则一次无关提交或别处的一句提及就会让 CI 乱红。')
    $lines.Add('> **一次性脚本用完即废**：新的一次性脚本落 `scripts/oneshot/`（目录即状态，可整目录清空），头部写一行')
    $lines.Add('> `# 一次性｜锚点：<迁移 NNN / ADR-0xx / 模块号>`；脚本根目录的历史一次性脚本按数量棘轮执行（只减不增）。')
    $lines.Add('')

    $byKind = @{}
    foreach ($kind in $KindOrder) { $byKind[$kind] = [System.Collections.Generic.List[object]]::new() }
    foreach ($e in $Entries) {
        if (-not $byKind.ContainsKey($e.Kind)) { $byKind[$e.Kind] = [System.Collections.Generic.List[object]]::new() }
        $byKind[$e.Kind].Add($e)
    }

    $lines.Add('## 一、总览')
    $lines.Add('')
    $lines.Add(('- 入库脚本 **{0}** 个：' -f $Entries.Count) + (@($KindOrder | ForEach-Object { '{0} {1}' -f $_, @($byKind[$_]).Count }) -join ' · '))
    $lines.Add('- 说明缺失（三种头注释都没有）：**{0}** 个' -f @($Entries | Where-Object DescSource -eq 'NONE').Count)
    $lines.Add('- 非标准头（有说明但不是 `.SYNOPSIS`，`Get-Help` 取不到）：**{0}** 个' -f @($Entries | Where-Object DescSource -ne 'SYNOPSIS').Count)
    $lines.Add('- 无人引用（不在 CI / 手册 / 技能 / 其它脚本里）：**{0}** 个，见第七节' -f @($Entries | Where-Object RefCount -eq 0).Count)
    $lines.Add('- `scripts/oneshot/` 待清理的一次性脚本：**{0}** 个，见第二节' -f @($Entries | Where-Object IsOneshot).Count)
    $lines.Add(('- 本机不入库的验收脚本（{0}）：`EOS.API.Tests/*.ps1` 等 **{1}** 个——被 `.git/info/exclude` 排除，' -f '不入库', $LocalTestScripts))
    $lines.Add('  别人的克隆里没有它们，故不参与上面的引用统计。')
    $lines.Add('')

    # ---- 二、一次性脚本落点 ----
    $lines.Add('## 二、一次性脚本落点（`scripts/oneshot/`，用完即删）')
    $lines.Add('')
    $lines.Add('这里的一次性脚本随时可以整目录清空，不影响任何门禁。**锚点**说明它随哪件收口完成而失效；')
    $lines.Add('锚点指向的迁移/收口一旦完成，脚本就该删掉（正文在 git 历史里，不必留在工作区）。')
    $lines.Add('')
    $oneshot = @($Entries | Where-Object IsOneshot | Sort-Object Name)
    if ($oneshot.Count -eq 0) {
        $lines.Add('（无）')
    } else {
        $lines.Add('| 脚本 | 锚点 | 作用 |')
        $lines.Add('|---|---|---|')
        foreach ($e in $oneshot) {
            $anchor = if ($e.Anchor -ne '') { $e.Anchor } else { '**缺锚点**' }
            $lines.Add(('| `{0}` | {1} | {2} |' -f $e.Name, $anchor, (Format-Cell $e.Description)))
        }
    }
    $lines.Add('')

    $section = 3
    $titles = @{
        '门禁（CI）'      = '三、门禁（CI 自动跑，不需要你动手）'
        '门禁（本地按需）' = '四、门禁（本地按需，多数需连库或服务在线）'
        '一次性批次'      = '五、一次性批次（使命已完结，保留作留痕）'
        '工具'            = '六、工具（可重复使用）'
    }
    foreach ($kind in $KindOrder) {
        $lines.Add('## ' + $titles[$kind])
        $lines.Add('')
        $lines.Add('| 脚本 | 作用 | 依赖 | 引用 |')
        $lines.Add('|---|---|---|---|')
        foreach ($e in ($byKind[$kind] | Sort-Object Name)) {
            $desc = if ($e.Description -ne '') { $e.Description } else { '**（无说明：连头注释都没有）**' }
            $ref = if ($e.Standalone) { '独立工具' } elseif ($e.RefCount -eq 0) { '**0**' } else { "$($e.RefCount)" }
            $lines.Add(('| `{0}` | {1} | {2} | {3} |' -f $e.Name, (Format-Cell $desc), ($e.Dependencies -join '、'), $ref))
        }
        $lines.Add('')
        $section++
    }

    $lines.Add('## 七、无人引用的脚本（每条的白名单理由）')
    $lines.Add('')
    $lines.Add('| 脚本 | 白名单 | 理由 |')
    $lines.Add('|---|---|---|')
    foreach ($e in ($Entries | Where-Object RefCount -eq 0 | Sort-Object Name)) {
        $mark = if ($e.Standalone) { '已登记' } else { '**未登记**' }
        $reason = if ($e.Standalone) { $Standalone[$e.Name] } else { '（新增脚本要么被 CI / 手册 / 技能 / 其它脚本引用，要么进 `$StandaloneTools`）' }
        $lines.Add(('| `{0}` | {1} | {2} |' -f $e.Name, $mark, (Format-Cell $reason 150)))
    }
    $lines.Add('')

    $lines.Add('## 八、待处置：门禁类脚本既不在 CI、也没在 `docs/guide/06` 里登记')
    $lines.Add('')
    $lines.Add('这些脚本自称门禁，但**没有任何地方会跑到它**——要么把它登记进 CI 或 [06 篇](../06-测试与质量门禁.md)（说明它守什么、')
    $lines.Add('怎么跑），要么确认其约束已失效并退役。本节**只提示不失败**：处置需要业务判断。')
    $lines.Add('')
    $pending = @($Entries | Where-Object { $_.Kind -like '门禁*' -and -not $_.InCi -and -not $_.InGuide06 } | Sort-Object Name)
    if ($pending.Count -eq 0) {
        $lines.Add('（无）')
    } else {
        $lines.Add('| 脚本 | 作用 | 依赖 |')
        $lines.Add('|---|---|---|')
        foreach ($e in $pending) {
            $lines.Add(('| `{0}` | {1} | {2} |' -f $e.Name, (Format-Cell $e.Description), ($e.Dependencies -join '、')))
        }
    }
    $lines.Add('')

    $lines.Add('## 九、白名单条目复核（只减不增）')
    $lines.Add('')
    $stale = @($Standalone.Keys | Where-Object { $_ -notin @($Entries | Where-Object RefCount -eq 0).Name })
    if ($stale.Count -eq 0) {
        $lines.Add('（无：白名单条目都还是"无人引用"状态）')
    } else {
        $lines.Add('下列条目已被引用、或脚本已不存在，可从 `scripts/script-inventory.ps1` 的白名单移除：')
        $lines.Add('')
        foreach ($name in ($stale | Sort-Object)) { $lines.Add("- ``$name``") }
    }
    $lines.Add('')

    return @($lines)
}

$ledgerLines = New-LedgerLines -Entries $entries -Standalone $StandaloneTools -LocalTestScripts $fileCount -Retired $retired
$ledgerContent = ($ledgerLines -join "`n") + "`n"
$fingerprint = Get-InventoryFingerprint -Entries $entries -Standalone $StandaloneTools -Retired $retired
$outPath = Join-Path $Root $OutFile

if ($Json) {
    @($entries | ForEach-Object {
            [pscustomobject]@{
                name         = $_.Name
                path         = $_.Path
                description  = $_.Description
                descSource   = $_.DescSource
                kind         = $_.Kind
                dependencies = @($_.Dependencies)
                inCi         = $_.InCi
                inGuide06    = $_.InGuide06
                refCount     = $_.RefCount
                standalone   = $_.Standalone
                lastCommit   = $_.LastCommit
            }
        }) | ConvertTo-Json -Depth 4
    exit 0
}



$noDescription = @($entries | Where-Object DescSource -eq 'NONE')
$unreferenced = @($entries | Where-Object { $_.RefCount -eq 0 -and -not $_.Standalone })
$pending = @($entries | Where-Object { $_.Kind -like '门禁*' -and -not $_.InCi -and -not $_.InGuide06 })
$warnNoSynopsis = @($entries | Where-Object { $_.DescSource -ne 'SYNOPSIS' })
$missingAnchor = @($entries | Where-Object { $_.IsOneshot -and $_.Anchor -eq '' })
$rootOneshot = @($entries | Where-Object { -not $_.IsOneshot -and $_.Kind -eq '一次性批次' })

if ($Check) {
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($e in $noDescription) {
        $failures.Add("$($e.Path) 没有可提取的说明：补 `.SYNOPSIS`（或文件头注释）写清它干什么")
    }
    foreach ($e in $unreferenced) {
        $failures.Add("$($e.Path) 无人引用：要么让 CI / 手册 / 技能 / 其它脚本引用它，要么加进 script-inventory.ps1 的白名单并写明理由")
    }
    foreach ($e in $missingAnchor) {
        $failures.Add("$($e.Path) 是一次性脚本但没写锚点：头部补一行 ``# 一次性｜锚点：<迁移 NNN / ADR-0xx / 模块号>``，说明它随哪件收口完成而失效")
    }
    if ($rootOneshot.Count -gt $LegacyOneshotBaseline) {
        $failures.Add("scripts/ 根目录的一次性脚本 $($rootOneshot.Count) 个，超过基线 $LegacyOneshotBaseline：新的一次性脚本落 $OneshotPrefix（用完即删），或先删掉被它替代的那个")
    }
    if (-not (Test-Path -LiteralPath $readmePath)) {
        $failures.Add("scripts/README.md 不存在：目录制度与退役记录缺了单一应答处（脚本目录的入口也是给人和 Agent 看的）")
    } elseif ($retired.Count -eq 0) {
        $failures.Add("scripts/README.md 里读不到退役清单：小节标题须为 `## <序号>、已退役脚本`，表格第一列写 `` `脚本名.ps1` ``")
    }
    foreach ($name in $retiredBack) {
        $failures.Add("scripts/$name 已在退役清单里，却又出现在磁盘上：要么删掉它，要么把它从 scripts/README.md 的退役清单移除（说好退役就得真没了）")
    }
    foreach ($name in $retiredStillRegistered) {
        $failures.Add("scripts/$name 已退役，却还留在 script-inventory.ps1 的白名单/类别例外里：删掉那些条目")
    }
    if (-not (Test-Path -LiteralPath $outPath)) {
        $failures.Add("台账 $OutFile 不存在：跑一次 pwsh scripts/script-inventory.ps1 生成它")
    } else {
        # 只比"造册语义"的指纹，不比整份文本：引用点数与时间戳会随无关改动而变，
        # 拿它们比对会让 CI 在不相干的 PR 上乱红（本仓 .gitattributes 只规定"索引里存 LF"，
        # 整份文本比对还会因 core.autocrlf 在不同机器上假红）。
        $diskText = Get-Content -LiteralPath $outPath -Raw -Encoding UTF8
        $diskFingerprint = ''
        $fpMatch = [regex]::Match($diskText, 'inventory-fingerprint:\s*(?<fp>[0-9a-f]+)')
        if ($fpMatch.Success) { $diskFingerprint = $fpMatch.Groups['fp'].Value }
        if ($diskFingerprint -eq '') {
            $failures.Add("台账 $OutFile 里没有 inventory-fingerprint：跑一次 pwsh scripts/script-inventory.ps1 重新生成")
        } elseif ($diskFingerprint -ne $fingerprint) {
            $failures.Add("台账 $OutFile 已落后于脚本目录（指纹 $diskFingerprint ≠ $fingerprint）：跑一次 pwsh scripts/script-inventory.ps1 重新生成并提交")
        }
    }

    foreach ($e in $warnNoSynopsis) {
        Write-Host ("  [INFO] {0} 的说明不是 .SYNOPSIS（Get-Help 取不到）：{1}" -f $e.Name, $e.DescSource) -ForegroundColor DarkGray
    }
    foreach ($e in $pending) {
        Write-Host ("  [WARN] {0} 自称门禁，但既不在 CI 也没在 docs/guide/06 里登记——登记或退役，见 {1} 第八节" -f $e.Name, $OutFile) -ForegroundColor Yellow
    }
    foreach ($name in @($StandaloneTools.Keys | Where-Object { $_ -notin @($entries | Where-Object RefCount -eq 0).Name } | Sort-Object)) {
        Write-Host ("  [INFO] 白名单条目 {0} 已被引用或脚本已不存在，可从白名单移除" -f $name) -ForegroundColor DarkGray
    }
    if ($rootOneshot.Count -lt $LegacyOneshotBaseline) {
        Write-Host ("  [INFO] scripts/ 根目录的一次性脚本已降到 {0} 个（基线 {1}）：可把 `$LegacyOneshotBaseline` 下调到 {0}" -f $rootOneshot.Count, $LegacyOneshotBaseline) -ForegroundColor DarkGray
    }

    if ($failures.Count -gt 0) {
        Write-Host '== 脚本台账门禁 ==' -ForegroundColor Cyan
        foreach ($line in $failures) { Write-Host "  [FAIL] $line" -ForegroundColor Red }
        Write-Host ("-- FAIL（脚本 {0} 个，未引用 {1} 个，缺说明 {2} 个，缺锚点 {3} 个，根目录一次性 {4}/{5}）" -f `
                $entries.Count, $unreferenced.Count, $noDescription.Count, $missingAnchor.Count, $rootOneshot.Count, $LegacyOneshotBaseline)
        exit 1
    }
    Write-Host ("PASS 脚本台账：{0} 个脚本全部有说明且在册；台账与目录一致（待处置门禁 {1} 个，仅提示）" -f $entries.Count, $pending.Count)
    exit 0
}

$outDir = Split-Path -Parent $outPath
if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
Set-Content -LiteralPath $outPath -Value $ledgerContent -Encoding utf8 -NoNewline

Write-Host '== 脚本台账已生成 ==' -ForegroundColor Cyan
Write-Host "  文件：$OutFile"
Write-Host ("  脚本 {0} 个：" -f $entries.Count) -NoNewline
Write-Host (@($KindOrder | ForEach-Object { '{0} {1}' -f $_, @($entries | Where-Object Kind -eq $_).Count }) -join ' · ')
Write-Host ("  待处置门禁（不在 CI / 不在 06 篇）：{0} 个" -f $pending.Count)
Write-Host ("  无人引用（未登记白名单）：{0} 个" -f $unreferenced.Count)
if ($warnNoSynopsis.Count -gt 0) {
    Write-Host ("  非标准头（取不到 Get-Help）：{0} 个" -f $warnNoSynopsis.Count)
}
exit 0
