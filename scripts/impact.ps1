#Requires -Version 7.0
<#
.SYNOPSIS
    改动影响面报告：给定本次改动文件，列出命中哪些手册篇目、必须同批跟改哪些登记面、该跑哪些验证。

.DESCRIPTION
    本仓库的经验规则大多以散文形式散在 docs/guide/ 与 LESSONS.md 里（"改了 X 要连 Y 一起改"），
    人读得懂，但每次都靠记忆与翻文档。本脚本把这些规则收敛成一份可执行的判定表：
    输入改动路径集合，输出「篇目 / 登记面 / 验证命令 / 通用提醒」四类结论，
    每条结论都标注出处（篇目或脚本路径），便于复核。

    两件事刻意不做：
      · 不判断"这样改对不对"——它只回答"这次改动会碰到哪些面"，判定仍由门禁与人负责；
      · 不读库、不改文件。影响面是静态事实（路径 + git 状态 + 两份登记清单），不依赖运行环境。

    篇目来源有两处，取并集：
      1. docs/guide/_map.md 的「文档 ↔ 源码」映射（按源码路径前缀匹配）；
      2. 判定表里显式登记的篇目——映射粒度只到目录/文件，部分能力（如助手工具）登记得太细，
         靠前缀匹配会漏，故由判定表补齐。

.parameter Path
    显式指定改动路径（可多个）。不传时按 -Staged 或工作区状态自动取。

.parameter Staged
    只取已暂存的改动（git diff --cached），用于暂存完成后的复核。

.parameter Json
    输出结构化 JSON（供 AI 助手或其它脚本消费），不打印人类可读报告。

.parameter SelfTest
    正反自检：用合成路径驱动判定函数本身，断言该命中的规则命中、不相关路径不误报。

.EXAMPLE
    pwsh scripts/impact.ps1                     # 报告当前工作区（含未跟踪）的影响面
    pwsh scripts/impact.ps1 -Staged             # 只报告已暂存的改动
    pwsh scripts/impact.ps1 -Path EOS.API/Data/Migrations/350_x.sql
    pwsh scripts/impact.ps1 -Json               # 机器可读
    pwsh scripts/impact.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string[]] $Path,
    [switch] $Staged,
    [switch] $Json,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$MapPath = Join-Path $Root 'docs/guide/_map.md'
$NolockPath = Join-Path $Root 'EOS.API.Tests/NolockAllowlist.txt'
$BoundaryScript = Join-Path $Root 'scripts/check-data-layer-boundary.ps1'

function Get-NormalizedPath {
    param([string] $Value)
    return ($Value -replace '\\', '/').Trim().Trim('"')
}

# ---------- 判定表（规则即数据；每条都须指得出出处） ----------
$Rules = @(
    [pscustomobject]@{
        Id      = 'api-data-root'
        Title   = 'EOS.API/Data 根目录新增 / 改动平铺文件'
        Match   = '^EOS\.API/Data/[^/]+\.cs$'
        Actions = @(
            '新代码不落 Data/ 根目录：领域能力放 EOS.API/Features/<域>/，共享原语放 Data/<已有子目录>/'
            '确需留在根目录时，把文件名显式加进 scripts/check-data-layer-boundary.ps1 白名单并说明理由（棘轮只减不增）'
        )
        Gates   = @('scripts/check-data-layer-boundary.ps1')
        Docs    = @('docs/guide/01-代码地图与仓库结构.md')
    }
    [pscustomobject]@{
        Id      = 'api-controllers'
        Title   = 'EOS.API/Controllers 控制器'
        Match   = '^EOS\.API/Controllers/[^/]+\.cs$'
        Actions = @(
            '新增依赖必须在 EOS.API/Program.cs 注册（漏注册 = 编译 0 错、单测全绿、端点全 500）'
            '权限门在控制器入口按 moduleId 重新授权（CanBrowse / CanEdit），fail-closed'
            '新增承载页路由要登记 ModuleRouteValidator（三处同改，见 routes-custom-pages 条）'
            '新增错误码登记 EOS.API/Errors/ApiErrorCodes.cs 并同步 04 篇'
        )
        Gates   = @('scripts/check-di-registrations.ps1')
        Docs    = @('docs/guide/04-API契约与前后端协作.md', 'docs/guide/21-授权模型.md')
    }
    [pscustomobject]@{
        Id      = 'api-security'
        Title   = 'EOS.API/Security 认证与授权'
        Match   = '^EOS\.API/Security/'
        Actions = @(
            '权限判定是服务端唯一真源，前端判断只改善体验，不得据此放行'
            '个人权限（SYSDD）存在即完全覆盖组权限（SYSDH），不合并'
            '组权限聚合：布尔位取 OR、EXEC_TAG 取最大值、禁止字段取交集'
        )
        Gates   = @('scripts/verify-permission-gate.ps1')
        Docs    = @('docs/guide/21-授权模型.md', 'docs/guide/22-权限配置后台.md')
    }
    [pscustomobject]@{
        Id      = 'api-errors'
        Title   = 'EOS.API/Errors 错误契约'
        Match   = '^EOS\.API/Errors/'
        Actions = @('错误码是稳定契约，前端按 code 分支、message 只用于展示；新增码同步 04 篇的码表')
        Gates   = @()
        Docs    = @('docs/guide/04-API契约与前后端协作.md')
    }
    [pscustomobject]@{
        Id      = 'assistant'
        Title   = '工作助手（能力面与红线）'
        Match   = '^EOS\.API/Features/Assistant/|^EOS\.Web/src/features/assistant/|^EOS\.API/Controllers/(Assistant|Kb)'
        Actions = @(
            '批核 / 解批 / 结案 / 取消结案永不进助手能力面（注册项 = 0，红线常量，配置层试图覆盖即启动失败）'
            '阈值只在 Features/Assistant/Governance/AssistantActionLimits.cs 声明一次，业务代码不得写死同值数字'
            '权限类配置（RightsAdmin / UserAdmin）助手侧不存在调用通路'
            '探测存在性一律 404（不区分"不存在"与"无权限"）；回答"我自己为什么做不了"必须明说'
            '审计 ACTOR_TYPE=3 / CLIENT_TYPE=3，ACTOR_USER_ID 恒为真人；AI 发起与用户确认各留一条'
        )
        Gates   = @(
            'scripts/check-assistant-action-registry.ps1'
            'scripts/check-assistant-action-limits.ps1'
            'scripts/check-workbench-policy-single-entry.ps1'
        )
        Docs    = @('docs/guide/60-AI工作助手.md', 'docs/guide/61-工作助手的处境与诊断.md', 'docs/guide/62-嵌入模型与知识库接线.md')
    }
    [pscustomobject]@{
        Id      = 'migrations'
        Title   = 'DbUp 迁移脚本'
        Match   = '^EOS\.API/Data/Migrations/'
        Actions = @(
            '编号取当前最大 +1，不回收空号；文件名是判重身份，编号冲突时改内容不改名'
            '库对象（表/列/索引/约束）命名全大写'
            '元数据变更必须随迁移一起提交，别人拉下代码即有一致环境'
            '退役库对象时把条目加进 check-retired-db-objects.ps1 的退役清单（写明迁移文件与原因）'
            '改表 / 改列必须连读取路径与校验一起改（放宽可空、改索引这类改动只在真库上炸）'
        )
        Gates   = @('scripts/check-migration-journal.ps1', 'scripts/check-retired-db-objects.ps1')
        Docs    = @('docs/guide/05-数据库迁移与建库.md', 'docs/guide/10-元数据模型.md')
    }
    [pscustomobject]@{
        Id      = 'routes-custom-pages'
        Title   = '定制页 / 承载页路由（三处同改）'
        Match   = '^EOS\.API/Data/ModuleRouteValidator\.cs$|^EOS\.Web/src/app/workspaceRoutes\.tsx$|^EOS\.API\.Tests/ModuleRouteValidatorTests\.cs$'
        Actions = @(
            '新增 / 下线承载页必须三处同改：库内 MODULES.M_URL、后端 ModuleRouteValidator（ExactRoutes / ParameterizedBases）、前端 workspaceRoutes.tsx'
            '同时改 ModuleRouteValidatorTests 里钉住它的 [InlineData]（含模块号），漏改任一处即红'
            '下线一个承载面时三处一起减，并同步用例与注释（只换数字会留下说谎的注释）'
        )
        Gates   = @()
        Docs    = @('docs/guide/12-元数据消费.md')
    }
    [pscustomobject]@{
        Id      = 'workbench'
        Title   = '统一工作台 / 统一表单'
        Match   = '^EOS\.API/Data/Workbench/|^EOS\.API/Data/DocumentWorkbenchRepository\.cs$|^EOS\.Web/src/features/document-workbench/'
        Actions = @(
            '动态表名/列名/排序/查询字段只来自服务端白名单或定义，不信任前端提交'
            '动态查询走结构化条件 + 参数化 SQL；子表查询必须依赖有效主表关联键'
            '列宽写回 FIELDS.DISPLAY_LENGTH 仅限统一工作台（需 CanSetup），其余列表一律 localStorage'
        )
        Gates   = @()
        Docs    = @('docs/guide/40-统一工作台.md', 'docs/guide/42-统一表单.md', 'docs/guide/41-定义快照与发布.md')
    }
    [pscustomobject]@{
        Id      = 'effects'
        Title   = '单据行为效果引擎与动作目录'
        Match   = '^EOS\.API/Data/Effects/|^EOS\.API/Data/DocumentActions/|^EOS\.API/Data/BusinessActionCatalog\.cs$|^EOS\.API/Data/EffectStructSchemas\.cs$'
        Actions = @(
            '效果链动作在库内 MODULE_BUSINESS_ACTION 配置，新动作要能落进校验闭集'
            'SAVE 事件的效果链当前不可预演，助手侧必须显式标注原因，不得静默'
            '效果引擎总闸现读 IOptionsMonitor：读到坏值沿用上一次有效值（行为契约，不是可删的防御）'
        )
        # 效果域的三条全量门禁已退役（见 scripts/README.md 退役清单）：目录一致性与"每个键都有说明"
        # 由 EOS.API.Tests/EffectContractCatalogTests.cs 覆盖，运行时数据驱动与"库内在用键在目录内"
        # 这两层现在没有自动断言，故这里只留真库用例作为验证手段。
        Gates   = @()
        Docs    = @('docs/guide/47-单据行为效果引擎.md')
    }
    [pscustomobject]@{
        Id      = 'chooser'
        Title   = '统一选择器与数据源'
        Match   = '^EOS\.API/Data/Chooser|^EOS\.Web/src/components/common/(UnifiedChooser|chooserSource)'
        Actions = @(
            '新增数据源在 ChooserRepository.cs 注册，且四张表要登记齐：列清单 / 排序白名单 / 关键字白名单 / 列表达式'
            '列与默认列来自服务端元数据或受控白名单，前端禁止硬编码列清单'
            'loader 只用于存量过渡，新代码不得新增'
        )
        Gates   = @()
        Docs    = @('docs/guide/46-统一选择器.md', 'docs/guide/44-受控查询与条件模板.md')
    }
    [pscustomobject]@{
        Id      = 'web-components'
        Title   = '前端通用组件与样式约定'
        Match   = '^EOS\.Web/src/components/(common|ui|layout)/'
        Actions = @(
            '列表一律用 ErpTable，禁止手写 <table>；排序/列宽/行内单选等能力要显式传参，不传不报错只是不出现'
            '选择业务数据一律用 UnifiedChooser，禁止页面内自建选择弹窗'
            '命令栏按钮统一 16px 图标 + 文字 + .erp-command-btn；纯图标下拉用 .erp-command-icon-btn'
            '新增 erp-* 类名必须有对应 CSS 定义（悬空类名不报错，只是页面像没写样式）'
        )
        Gates   = @('scripts/check-css-classes.ps1')
        Docs    = @('docs/guide/03-工程规范与硬约束.md')
    }
    [pscustomobject]@{
        Id      = 'web-app'
        Title   = '前端路由 / 应用外壳 / 构建配置'
        Match   = '^EOS\.Web/src/app/|^EOS\.Web/(vite|vitest)\.config\.ts$|^EOS\.Web/package\.json$'
        Actions = @('新增页面在 src/app/ 登记路由；路由守卫与按钮渲染由模块权限生成')
        Gates   = @()
        Docs    = @('docs/guide/01-代码地图与仓库结构.md', 'docs/guide/04-API契约与前后端协作.md')
    }
    [pscustomobject]@{
        Id      = 'web-features'
        Title   = '前端业务域（features/<域>）'
        Match   = '^EOS\.Web/src/features/'
        Actions = @(
            '组件不直接 fetch：页面 → TanStack Query → services/api → HTTP → EOS.API'
            '新增域目录要在 docs/guide/_map.md 登记（否则手册陈旧检测扫不到它）'
        )
        Gates   = @()
        Docs    = @('docs/guide/01-代码地图与仓库结构.md')
    }
    [pscustomobject]@{
        Id      = 'db-bootstrap'
        Title   = '建库脚本与种子'
        Match   = '^db/'
        Actions = @(
            '日常开发只写迁移，不改 10_schema.sql / 20_metadata.sql——它们在版本收口时由 export-bootstrap-*.ps1 重新生成'
            '基础资料（模块清单）与库内实际、EOS.API.Tests/base-data-modules.json 三者必须一致'
        )
        Gates   = @('scripts/check-base-data.ps1')
        Docs    = @('docs/guide/05-数据库迁移与建库.md', 'docs/guide/02-本地环境与运行.md')
    }
    [pscustomobject]@{
        Id      = 'tests'
        Title   = '后端测试'
        Match   = '^EOS\.API\.Tests/'
        Actions = @(
            '真库用例一律打 [Trait("Category", "Integration")]（类级即可）——它是排除真库用例的唯一依据，比文件名带 Live 可靠'
            '--artifacts-path 一律指向仓库内目录，否则扫描型用例报"找不到仓库根目录"'
            '写入端点必须有一条真库用例真的走一次写（读路径全绿 ≠ 写路径可用）'
            '"数据前提"要断言而不是 return 跳过：跳过只留给"没有连接串"这类环境缺失'
        )
        Gates   = @('scripts/check-live-fixture-hygiene.ps1')
        Docs    = @('docs/guide/06-测试与质量门禁.md')
    }
    [pscustomobject]@{
        Id      = 'scripts'
        Title   = '门禁脚本'
        Match   = '^scripts/'
        Actions = @(
            '改门禁脚本本身必须跑它的 -SelfTest（用合成数据做正反自检）'
            '新门禁若进 CI，同步 .github/workflows/ci.yml；口径变化同步 docs/guide/06 第五节表格'
            '棘轮类清单（白名单 / 退役清单 / 悬空类名基线）只减不增'
            '新增 / 改名 / 删除脚本后重跑 pwsh scripts/script-inventory.ps1（CI 用 -Check 校验台账与目录一致）'
        )
        Gates   = @()
        Docs    = @('docs/guide/06-测试与质量门禁.md')
    }
    [pscustomobject]@{
        Id      = 'agent-skills'
        Title   = 'Agent 技能'
        Match   = '^\.agents/skills/'
        Actions = @('新增技能或技能开始引用新资产 → 更新 .agents/skills/_map.md（技能清单 / 引用资产例外 / 忽略片段）')
        Gates   = @('scripts/check-agent-skills.ps1')
        Docs    = @('.agents/skills/README.md')
    }
    [pscustomobject]@{
        Id      = 'guide'
        Title   = '开发手册'
        Match   = '^docs/guide/'
        Actions = @(
            '新增篇目必须在 _map.md 登记，否则陈旧检测扫不到它'
            '按内容三分判断去向：机制事实不进知识库；规范/坑 → kb_guide_conventions；设计动因（ADR）→ kb_guide_rationale'
            '来源与篇目要在同一个提交里更新，否则新鲜度门禁在两次提交之间持续报落后'
        )
        Gates   = @('docs/guide/_tools/check-freshness.ps1')
        Docs    = @('docs/guide/README.md')
    }
    [pscustomobject]@{
        Id      = 'adr'
        Title   = '架构决策记录'
        Match   = '^docs/decisions/'
        Actions = @(
            '编号取现有最大 +1，不回收空号；头部三项必填（状态 / 日期 / 决策者），版本按需（有修订才写）'
            '正文含背景与证据、决策、实施计划、已知后果、替代方案、验收标准、待确认项'
            '需要拍板的事项同时登记 docs/plans/decision-ledger.md'
            '新增 / 改了 ADR 后重跑 pwsh scripts/gen-adr-index.ps1（CI 用 -Check 校验索引与决策目录一致；头部缺项也在那里报红）'
        )
        Gates   = @()
        Docs    = @('CONTRIBUTING.md')
    }
    [pscustomobject]@{
        Id      = 'ci'
        Title   = 'CI 与仓库设置'
        Match   = '^\.github/workflows/|^\.github/CODEOWNERS$'
        Actions = @('CI 只点亮红灯：拦不拦得住取决于 main 的分支规则要求 PR + CI 通过（仓库设置，不在代码里）')
        Gates   = @()
        Docs    = @('docs/guide/06-测试与质量门禁.md')
    }
    [pscustomobject]@{
        Id      = 'lessons'
        Title   = 'LESSONS 坑清单'
        Match   = '^LESSONS\.md$'
        Actions = @(
            'L<n> 只增不改、不回收空号；新增取当前最大 +1'
            '一条只记一次事故，四段（触发／症状 / 根因 / 处置 / 防线）缺一即红'
            '不记一次性故障、进度汇报与"当时怎么想的"'
        )
        Gates   = @('scripts/check-docs.ps1')
        Docs    = @('LESSONS.md')
    }
    [pscustomobject]@{
        Id      = 'agents-md'
        Title   = '规则清单（AGENTS.md / CONTRIBUTING.md）'
        Match   = '^AGENTS\.md$|^CONTRIBUTING\.md$'
        Actions = @(
            'AGENTS.md 第二、三节是规则清单真源；docs/guide/03 讲"为什么"，冲突时以 AGENTS.md 为准并回来修正 03 篇'
            '提交信息规范与提交边界在 AGENTS.md 第四节；外部贡献者的入口是 CONTRIBUTING.md'
        )
        Gates   = @()
        Docs    = @('docs/guide/03-工程规范与硬约束.md')
    }
    [pscustomobject]@{
        Id      = 'readme'
        Title   = 'README 与版本引用'
        Match   = '^README\.md$|^version\.json$|^CHANGELOG\.md$'
        Actions = @(
            'README 的版本引用必须与 version.json 一致（发布脚本会一并改写，手工只改一边即报错）'
            'README 不复述里程碑历史，历史归 git 与 docs/status.md'
        )
        Gates   = @('scripts/check-docs.ps1')
        Docs    = @()
    }
)

# ---------- 映射表：文档 ↔ 源码 ----------
function Get-GuideMap {
    param([string] $PathToMap)
    $rows = [System.Collections.Generic.List[object]]::new()
    if (-not (Test-Path -LiteralPath $PathToMap)) { return $rows }
    foreach ($line in (Get-Content -LiteralPath $PathToMap -Encoding UTF8)) {
        if ($line -notmatch '^\s*\|') { continue }
        $cells = @($line.Trim().Trim('|') -split '\|' | ForEach-Object { $_.Trim() })
        if ($cells.Count -lt 3) { continue }
        if ($cells[0] -eq '文档' -or $cells[0] -match '^-+$') { continue }
        $sources = @()
        if ($cells[2] -ne '-' -and $cells[2] -ne '') {
            $sources = @($cells[2] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        $rows.Add([pscustomobject]@{ Doc = $cells[0]; Sources = $sources })
    }
    return $rows
}

function Test-SourceMatch {
    <# 前缀匹配：源是目录时要求下一字符是 '/'，避免 Workbench 命中 WorkbenchFoo.cs #>
    param([string] $File, [string] $Source)
    $src = ($Source -replace '\\', '/').Trim().TrimEnd('/')
    if ($src -eq '') { return $false }
    if ($File -eq $src) { return $true }
    return $File.StartsWith($src + '/', [StringComparison]::OrdinalIgnoreCase)
}

# ---------- 按路径登记的清单：搬家的连带面 ----------
function Get-PathRegisteredLists {
    <# 返回 { 文件名(小写) = 清单名 }；这两个清单按"文件路径"登记，文件搬家必须同批改。 #>
    param([string] $NolockFile, [string] $BoundaryFile)
    $map = @{}
    if (Test-Path -LiteralPath $NolockFile) {
        foreach ($line in (Get-Content -LiteralPath $NolockFile -Encoding UTF8)) {
            $name = $line.Trim()
            if ($name -eq '' -or $name.StartsWith('#')) { continue }
            $leaf = [System.IO.Path]::GetFileName(($name -replace '\\', '/'))
            if ($leaf) { $map[$leaf.ToLowerInvariant()] = 'EOS.API.Tests/NolockAllowlist.txt' }
        }
    }
    if (Test-Path -LiteralPath $BoundaryFile) {
        $text = Get-Content -Raw -Encoding UTF8 $BoundaryFile
        foreach ($m in [regex]::Matches($text, "(?s)@'\r?\n(.*?)\r?\n'@")) {
            foreach ($line in ($m.Groups[1].Value -split "`r?`n")) {
                $name = $line.Trim()
                if ($name -eq '' -or -not $name.EndsWith('.cs')) { continue }
                $map[$name.ToLowerInvariant()] = 'scripts/check-data-layer-boundary.ps1 白名单'
            }
        }
    }
    return $map
}

# ---------- 纯判定：由改动路径集合产出报告 ----------
function Get-ImpactReport {
    param(
        [string[]] $Files,
        [object[]] $RuleTable,
        [object]   $GuideMap,
        [hashtable] $PathLists
    )

    # 注意：PowerShell 变量名大小写不敏感，累加器不能叫 $files（会覆盖参数 $Files）
    $entries = [System.Collections.Generic.List[object]]::new()
    $allDocs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allGates = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($raw in $Files) {
        $file = Get-NormalizedPath $raw
        if ($file -eq '') { continue }

        $hits = [System.Collections.Generic.List[object]]::new()
        $docs = [System.Collections.Generic.List[string]]::new()
        foreach ($rule in $RuleTable) {
            if ($file -match $rule.Match) {
                $hits.Add($rule)
                foreach ($g in $rule.Gates) { $null = $allGates.Add($g) }
                foreach ($d in $rule.Docs) {
                    # 规则登记的篇目也要落到该文件名下，否则逐文件清单与汇总清单对不上
                    if (-not $docs.Contains($d)) { $docs.Add($d) }
                    $null = $allDocs.Add($d)
                }
            }
        }

        # 按路径登记的清单：文件已在清单里 ⇒ 搬家 / 改名要同批删旧加新
        $leaf = [System.IO.Path]::GetFileName($file).ToLowerInvariant()
        if ($PathLists.ContainsKey($leaf)) {
            $hits.Add([pscustomobject]@{
                    Id      = 'path-registered'
                    Title   = "该文件登记在 $($PathLists[$leaf])（按路径登记）"
                    Actions = @('文件搬家 / 改名时必须在同一次改动里删旧条目、加新条目，否则新旧两侧同时告警')
                    Gates   = @()
                    Docs    = @()
                })
        }

        foreach ($row in $GuideMap) {
            foreach ($src in $row.Sources) {
                if (Test-SourceMatch -File $file -Source $src) {
                    # 统一带 docs/guide/ 前缀：判定表里的篇目也是这个写法，两处拼接口径要一致
                    $doc = 'docs/guide/' + $row.Doc
                    if (-not $docs.Contains($doc)) { $docs.Add($doc) }
                    $null = $allDocs.Add($doc)
                    break
                }
            }
        }

        $entries.Add([pscustomobject]@{ Path = $file; Rules = $hits; Docs = $docs })
    }

    # ---- 验证命令：按改动所在工程聚合 ----
    $commands = [System.Collections.Generic.List[string]]::new()
    $paths = @($entries | ForEach-Object { $_.Path })
    $hasApi = @($paths | Where-Object { $_ -like 'EOS.API/*' -or $_ -eq 'EOS.API' }).Count -gt 0
    $hasWeb = @($paths | Where-Object { $_ -like 'EOS.Web/*' }).Count -gt 0
    $hasTests = @($paths | Where-Object { $_ -like 'EOS.API.Tests/*' }).Count -gt 0
    $hasMigrations = @($paths | Where-Object { $_ -like 'EOS.API/Data/Migrations/*' }).Count -gt 0
    $hasGuide = @($paths | Where-Object { $_ -like 'docs/guide/*' }).Count -gt 0

    if ($hasApi) {
        $commands.Add('dotnet build EOS.API/EOS.API.csproj')
        $commands.Add('pwsh scripts/check-di-registrations.ps1    # 每次改动都建议跑：它拦"端点全挂"那类问题')
    }
    if ($hasApi -or $hasTests) {
        $commands.Add('dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter "Category!=Integration&Category!=Tool"')
    }
    if ($hasMigrations) {
        $commands.Add('dotnet test EOS.API.Tests/EOS.API.Tests.csproj    # 库结构类改动必须跑真库用例')
        $commands.Add('pwsh scripts/check-migration-journal.ps1         # 需连库')
    }
    if ($hasWeb) {
        $commands.Add('cd EOS.Web; npm run lint; npm run build; npm test')
    }
    if ($hasGuide) {
        $commands.Add('pwsh docs/guide/_tools/check-freshness.ps1        # 落后判定按提交比对，提交后确认')
    }

    $reminders = @(
        '若本次改动含接口 / 路由 / 错误码 / 配置项 / 权限键 / 公共组件 props / 库对象结构 → 必须同步 docs/guide 对应篇目（篇目见上）'
        '提交信息：Conventional Commits + 简体中文描述；自查 pwsh scripts/check-commit-msg.ps1 -Message ''...'''
        'EOS.API.Tests 与 EOS.API 同级：git add 用目录白名单时别漏它，提交后核对 git status --porcelain 为空'
        '需连库 / 需服务在线的门禁不在 CI 里，按上表挑着跑；改动收尾的验证口径是编译 + 静态检查'
    )

    return [pscustomobject]@{
        Files     = $entries
        Docs      = @($allDocs | Sort-Object)
        Gates     = @($allGates | Sort-Object)
        Commands  = @($commands)
        Reminders = $reminders
    }
}

# ---------- 取改动集合 ----------
function Get-ChangedPaths {
    param([string] $RepoRoot, [string[]] $Explicit, [switch] $OnlyStaged)
    if ($Explicit -and $Explicit.Count -gt 0) {
        # 允许逗号分隔的写法：命令行经 -File 传参时 `-Path a,b` 会作为一个字符串到达，
        # 用户按直觉写的分隔形式应当在脚本里被拆开，而不是变成一个不存在的路径。
        return @($Explicit | ForEach-Object { $_ -split ',' } |
                ForEach-Object { (Get-NormalizedPath $_) } | Where-Object { $_ -ne '' })
    }

    $result = [System.Collections.Generic.List[string]]::new()
    if ($OnlyStaged) {
        $lines = @(& git -C $RepoRoot diff --name-only --cached)
        foreach ($l in $lines) { if ($l) { $result.Add($l.Trim()) } }
        return @($result)
    }
    foreach ($line in @(& git -C $RepoRoot status --porcelain)) {
        if (-not $line -or $line.Length -lt 4) { continue }
        $body = $line.Substring(3).Trim()
        if ($body -match ' -> ') {
            # 改名：新旧两侧都要算（旧路径停了引用、新路径开始被引用）
            $parts = $body -split ' -> '
            foreach ($p in $parts) { $result.Add((Get-NormalizedPath $p)) }
        } else {
            $result.Add((Get-NormalizedPath $body))
        }
    }
    return @($result)
}

# ---------- 自检 ----------
if ($SelfTest) {
    $syntheticRules = @(
        [pscustomobject]@{ Id = 'api-data-root'; Title = 't'; Match = '^EOS\.API/Data/[^/]+\.cs$'; Actions = @(); Gates = @(); Docs = @() }
        [pscustomobject]@{ Id = 'migrations'; Title = 't'; Match = '^EOS\.API/Data/Migrations/'; Actions = @(); Gates = @('g'); Docs = @('docs/guide/05-数据库迁移与建库.md') }
        [pscustomobject]@{ Id = 'assistant'; Title = 't'; Match = '^EOS\.API/Features/Assistant/'; Actions = @(); Gates = @('g'); Docs = @() }
        [pscustomobject]@{ Id = 'tests'; Title = 't'; Match = '^EOS\.API\.Tests/'; Actions = @(); Gates = @(); Docs = @() }
    )
    $syntheticMap = @(
        [pscustomobject]@{ Doc = '40-统一工作台.md'; Sources = @('EOS.API/Data/Workbench') }
        [pscustomobject]@{ Doc = '05-数据库迁移与建库.md'; Sources = @('EOS.API/Data/Migrations') }
    )

    $positive = @('EOS.API/Data/Brand_New_Service.cs', 'EOS.API/Data/Workbench/QueryComposer.cs',
        'EOS.API/Data/Migrations/350_x.sql', 'EOS.API/Features/Assistant/Tools/FooTool.cs')
    $report = Get-ImpactReport -Files $positive -RuleTable $syntheticRules -GuideMap $syntheticMap -PathLists @{}
    $ids = @($report.Files | ForEach-Object { $_.Rules } | ForEach-Object { $_.Id })
    foreach ($expected in 'api-data-root', 'migrations', 'assistant') {
        if ($ids -notcontains $expected) { throw "自检失败：合成路径应命中规则 $expected，实际命中 $($ids -join ', ')" }
    }
    $docs = @($report.Docs)
    if ($docs -notcontains 'docs/guide/40-统一工作台.md') { throw '自检失败：Workbench 下的文件应命中 40-统一工作台.md' }
    if ($docs -notcontains 'docs/guide/05-数据库迁移与建库.md') { throw '自检失败：迁移目录应命中 05-数据库迁移与建库.md' }
    # 规则显式登记的篇目也要落到该文件名下（逐文件清单与汇总清单必须一致）
    $migEntry = @($report.Files | Where-Object { $_.Path -like '*Migrations*' })[0]
    if (@($migEntry.Docs) -notcontains 'docs/guide/05-数据库迁移与建库.md') {
        throw '自检失败：规则登记的篇目没有落到逐文件清单里'
    }
    Write-Host '  [PASS] 正向自检：合成路径命中规则与篇目'

    # 前缀边界：目录条目不得命中同前缀的兄弟文件
    $boundary = @('EOS.API/Data/WorkbenchDefinitionBuilder.cs')
    $boundaryReport = Get-ImpactReport -Files $boundary -RuleTable $syntheticRules -GuideMap $syntheticMap -PathLists @{}
    if (@($boundaryReport.Docs).Count -ne 0) {
        throw "自检失败：Data/WorkbenchDefinitionBuilder.cs 不应命中目录条目 EOS.API/Data/Workbench（命中了 $($boundaryReport.Docs -join ', ')）"
    }
    if (@($boundaryReport.Files[0].Rules).Count -eq 0) { throw '自检失败：Data 根目录文件应命中 api-data-root' }
    Write-Host '  [PASS] 边界自检：目录条目不误伤同前缀文件'

    # 负例：与判定表无关的路径不得产出任何规则
    $negative = @('README.md', 'LICENSE')
    $negativeReport = Get-ImpactReport -Files $negative -RuleTable $syntheticRules -GuideMap $syntheticMap -PathLists @{}
    if (@($negativeReport.Files | ForEach-Object { $_.Rules }).Count -ne 0) {
        throw '自检失败：无关路径不应命中任何规则'
    }
    Write-Host '  [PASS] 反向自检：无关路径不误报'

    Write-Host '-- SELFTEST OK'
    exit 0
}

# ---------- 主流程 ----------
$changed = Get-ChangedPaths -RepoRoot $Root -Explicit $Path -OnlyStaged:$Staged
$map = Get-GuideMap -PathToMap $MapPath
$pathLists = Get-PathRegisteredLists -NolockFile $NolockPath -BoundaryFile $BoundaryScript
$report = Get-ImpactReport -Files $changed -RuleTable $Rules -GuideMap $map -PathLists $pathLists
$head = ((& git -C $Root rev-parse --short HEAD 2>$null) | Out-String).Trim()

if ($Json) {
    $payload = [pscustomobject]@{
        head      = $head
        files     = @($report.Files | ForEach-Object {
                [pscustomobject]@{
                    path  = $_.Path
                    rules = @($_.Rules | ForEach-Object { [pscustomobject]@{ id = $_.Id; title = $_.Title; actions = @($_.Actions); gates = @($_.Gates) } })
                    docs  = @($_.Docs)
                }
            })
        docs      = @($report.Docs)
        gates     = @($report.Gates)
        commands  = @($report.Commands)
        reminders = @($report.Reminders)
    }
    $payload | ConvertTo-Json -Depth 6
    exit 0
}

Write-Host "== 影响面报告（HEAD=$head，$($report.Files.Count) 个改动文件）==" -ForegroundColor Cyan

if ($report.Files.Count -eq 0) {
    Write-Host '工作区无改动（或指定的路径为空）。'
    exit 0
}

Write-Host ''
Write-Host '一、命中篇目（关联文档：涉及它所描述行为的必须同步）'
if ($report.Docs.Count -eq 0) {
    Write-Host '  （无：本次改动未命中任何已登记篇目）'
} else {
    foreach ($doc in $report.Docs) { Write-Host "  - $doc" }
}

Write-Host ''
Write-Host '二、必须同批跟改的登记面 / 边界'
$seenRule = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($item in $report.Files) {
    foreach ($rule in $item.Rules) {
        if (-not $seenRule.Add($rule.Id)) { continue }
        Write-Host "  [$($rule.Id)] $($rule.Title)"
        foreach ($action in $rule.Actions) { Write-Host "      · $action" }
    }
}
if ($seenRule.Count -eq 0) { Write-Host '  （无）' }

Write-Host ''
Write-Host '三、逐文件命中'
foreach ($item in $report.Files) {
    $ids = @($item.Rules | ForEach-Object { $_.Id })
    $idText = if ($ids.Count -gt 0) { $ids -join ', ' } else { '（无规则命中）' }
    $docText = if ($item.Docs.Count -gt 0) { '  篇目: ' + ($item.Docs -join ', ') } else { '' }
    Write-Host "  $($item.Path)"
    Write-Host "      $idText$docText"
}

Write-Host ''
Write-Host '四、建议的验证'
if ($report.Commands.Count -eq 0) {
    Write-Host '  （本次改动不涉及两个工程，按改动范围自行判断）'
} else {
    foreach ($cmd in $report.Commands) { Write-Host "  $cmd" }
}
if ($report.Gates.Count -gt 0) {
    Write-Host '  相关门禁（按上表挑跑；不带连接串/服务不在线时跳过连库类）:'
    foreach ($gate in $report.Gates) { Write-Host "    pwsh $gate" }
}

Write-Host ''
Write-Host '五、通用提醒'
foreach ($reminder in $report.Reminders) { Write-Host "  · $reminder" }

exit 0
