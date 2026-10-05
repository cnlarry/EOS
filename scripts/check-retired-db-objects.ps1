<#
.SYNOPSIS
    退役库对象门禁：已退役的库列 / 库对象不得再被生产代码与脚本引用。

.DESCRIPTION
    库对象退役（DROP COLUMN / DROP FUNCTION）和代码改动是两件独立的事，编译、单测、
    迁移干跑都不会把二者联系起来：列删掉之后，漏改的 SELECT 只在**运行时**炸成 500。

    实例（2026-09-19）：迁移 191 删掉 SYSDL.G_IDX 后，AuthenticationRepository 仍在
    SELECT l.G_IDX，登录接口立刻 500，所有需要登录的接口不可用——当时编译 0 错误、
    单测全绿、迁移干跑 PASS，三道关卡都没拦住。

    本门禁把"退役清单"固化在脚本里，扫描生产代码与运维脚本，命中即 exit 1。
    新增退役对象时：在迁移落地前把条目加进 $retiredObjects，并写明迁移文件与原因。

    扫描范围：EOS.API/**/*.cs（排除 bin/obj）、EOS.API.Tests/**/*.ps1、scripts/**/*.ps1。
    刻意排除：
    - EOS.API/Data/Migrations/**：迁移脚本本身就是在做退役动作，必然出现旧名；
    - docs/、publish/：冻结文档与开源种子，不是运行时路径。

.EXAMPLE
    pwsh scripts/check-retired-db-objects.ps1            # exit 0 = 无残留引用
    pwsh scripts/check-retired-db-objects.ps1 -SelfTest  # 正反样本自检门禁本身有判别力
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

# 退役清单：每项含对象名与退役依据。
# SYSDL.G_IDX 走 Get-RetiredHit 里的别名分析（要看清"哪个别名绑定了 SYSDL 又访问 .G_IDX"，
# 否则会把"账号表 SYSDL 与关联表列 SYSDG_USER.G_IDX 同段出现"的正常代码误判）；
# 其余条目用 Pattern 正则匹配。
$retiredObjects = @(
    [pscustomobject]@{
        Name    = 'SYSDL.G_IDX'
        Reason  = '用户组关系唯一来源是 SYSDG_USER；见 Migrations/191_drop_sysdl_group_idx.sql'
    }
    [pscustomobject]@{
        Name    = 'dbo.f_get_user_gidx'
        Reason  = '直接 SELECT SYSDL.G_IDX 的遗留函数，已被迁移 191 删除且无调用方'
        Pattern = 'f_get_user_gidx'
    }
    # 字段级排布配置退役（迁移 243）：排布改由 MODULE_FORM_LAYOUT / MODULE_FORM_TAB 承载。
    # 判据用「点号修饰 + 全大写」：库列在 SQL 里一律大写，而 C# 模型同名属性是 PascalCase
    # （definition.FormColumns / definition.FormTabs），不区分大小写会把后者误判。
    # 覆盖不到的形态（已知缺口，登记在案）：裸列名（如 `SELECT FORM_ORDER FROM dbo.FIELDS`）——
    # 因为原位占位别名 `NULL AS FORM_TABS` / `CAST(1 AS int) AS FORM_TAB_NO` 仍在模型与仓储里
    # 保留（模块列表映射按列序号取值，删别名要连带挪 10 个序号，风险大于收益），
    # 故只判"点号修饰"这一种真实炸库的写法。
    [pscustomobject]@{
        Name          = 'FIELDS 字段级排布列'
        Reason        = 'FIELDS.FORM_ORDER/FORM_TAB_NO/FORM_SPAN/FORM_NEW_LINE/FORM_CELL_GROUP/FORM_CELL_ROLE 已退役，见 Migrations/243_drop_field_level_form_layout_columns.sql'
        Pattern       = '\.\s*(?:FORM_ORDER|FORM_TAB_NO|FORM_SPAN|FORM_NEW_LINE|FORM_CELL_GROUP|FORM_CELL_ROLE)\b'
        CaseSensitive = $true
    }
    [pscustomobject]@{
        Name          = 'MODULES 页签/列数列'
        Reason        = 'MODULES.FORM_TABS/FORM_COLUMNS 已退役（页签归 MODULE_FORM_TAB、列数固定四子列），见 Migrations/243_drop_field_level_form_layout_columns.sql'
        Pattern       = '\.\s*(?:FORM_TABS|FORM_COLUMNS)\b'
        CaseSensitive = $true
    }
    # 版式「微调」权限位退役（迁移 248）：版式设计权只保留完整设计一档。
    [pscustomobject]@{
        Name          = 'SYSDD/SYSDH 版式微调权限位'
        Reason        = 'SYSDD.FORM_ADJUST_TAG/SYSDH.FORM_ADJUST_TAG 已退役（版式设计权只保留 FORM_DESIGN_TAG 一档），见 Migrations/248_retire_form_adjust_tag.sql'
        Pattern       = '\.\s*FORM_ADJUST_TAG\b'
        CaseSensitive = $true
    }
    # 模块 ID 外键列改名（迁移 249）：10 张表的 MODULE_ID 统一为 M_IDX，与 MODULES.M_IDX 及
    # SYSDD/SYSDH/SYSDF/SYSTEMP/TASK/WF_APPROVE 等既有口径一致。
    # 覆盖不到的形态（已知缺口，登记在案）：裸列名——审计查询按契约把该列投影成 `M_IDX AS MODULE_ID`
    # （对外属性名不随库列名变），故只判"点号修饰"这一种真实炸库的写法。
    [pscustomobject]@{
        Name          = '模块 ID 外键列 MODULE_ID'
        Reason        = 'ATTACHMENT/AUDIT_EVENT/FORM_CHOOSER_SOURCE_MEMO/MODULE_BUSINESS_ACTION/MODULE_VALIDATION_RULE/REPORT_INBOX/REPORT_SUBSCRIPTION/WORKBENCH_DEFINITION_SNAPSHOT/WORKBENCH_IDEMPOTENCY/WORKBENCH_MODULE_DIRTY 的 MODULE_ID 已改名为 M_IDX，见 Migrations/249_rename_module_id_to_m_idx.sql'
        Pattern       = '\.\s*MODULE_ID\b'
        CaseSensitive = $true
    }
    # 孤立对象整表退役（迁移 255）：4 张空发票表 + 1 张库存留底表。
    # 这 5 个名字不会与其它标识符混淆，用词边界匹配裸名即可；命中即说明有人重新引用了已退役的表。
    # 退役依据见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql（含三方引用核查结论）。
    [pscustomobject]@{
        Name    = 'INVOICE_IN_M'
        Reason  = '孤立发票表（0 行 / 0 模块 / 0 依赖）已退役，见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql'
        Pattern = '\bINVOICE_IN_M\b'
    }
    [pscustomobject]@{
        Name    = 'INVOICE_IN_D'
        Reason  = '孤立发票表（0 行 / 0 模块 / 0 依赖）已退役，见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql'
        Pattern = '\bINVOICE_IN_D\b'
    }
    [pscustomobject]@{
        Name    = 'INVOICE_OUT_M'
        Reason  = '孤立发票表（0 行 / 0 模块 / 0 依赖）已退役，见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql'
        Pattern = '\bINVOICE_OUT_M\b'
    }
    [pscustomobject]@{
        Name    = 'INVOICE_OUT_D'
        Reason  = '孤立发票表（0 行 / 0 模块 / 0 依赖）已退役，见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql'
        Pattern = '\bINVOICE_OUT_D\b'
    }
    [pscustomobject]@{
        Name    = 'INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP'
        Reason  = '孤立库别留底表（68 行 DEPOT_ID=YL、无登记、无依赖）已退役；68 行留档于 logs/c5-retire/orphan-backup-rows.json，见 Migrations/255_retire_orphan_invoice_and_backup_tables.sql'
        Pattern = '\bINV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP\b'
    }
    # 报表权限例外层结构退役（迁移 277）：整表 SYSDH_REPORT + SYSDD_REPORT 的三列勾选。
    # 判据用裸名 + 词边界：这两个名字不会与其它标识符混淆，命中即说明有人重新引用了已退役的结构
    # （迁移脚本本身在扫描范围之外，必然出现旧名）。
    # 边界：`DATA_FILTER` **不在**退役之列——它是模块权限（SYSDD/SYSDH）的活列，报表链路仍在 AND 合并它；
    # 而 `SYSDD_REPORT` 表本身也仍在（承载收藏/排序/最近使用），退役的只是它那三列勾选。
    [pscustomobject]@{
        Name    = 'SYSDH_REPORT'
        Reason  = '逐报表例外层的组表（0 行）已随权限收敛退役，见 Migrations/277_report_permission_layer_retire.sql'
        Pattern = '\bSYSDH_REPORT\b'
    }
    [pscustomobject]@{
        Name          = 'SYSDD_REPORT 逐报表勾选列'
        Reason        = 'SYSDD_REPORT.PREVIEW_TAG/PRINT_TAG/EXPORT_TAG 已退役（表保留给用户状态列），见 Migrations/277_report_permission_layer_retire.sql'
        Pattern       = '\b(?:PREVIEW_TAG|PRINT_TAG|EXPORT_TAG)\b'
        CaseSensitive = $true
    }
    # 历史备份表退役（迁移 315）：5 个 BAK / Backup / GHOST 快照表，与现行的表结构脱节，
    # 此前一直靠导出脚本按名字模式排除才没进建库种子。名字足够独特，用词边界匹配裸名即可。
    [pscustomobject]@{
        Name    = 'FIELDS_GHOST_BAK_20260909'
        Reason  = 'FIELDS 的历史快照表，见 Migrations/315_drop_legacy_backup_tables.sql'
        Pattern = '\bFIELDS_GHOST_BAK_20260909\b'
    }
    [pscustomobject]@{
        Name    = 'TABLES_GHOST_BAK_20260909'
        Reason  = 'TABLES 的历史快照表，见 Migrations/315_drop_legacy_backup_tables.sql'
        Pattern = '\bTABLES_GHOST_BAK_20260909\b'
    }
    [pscustomobject]@{
        Name    = 'FIELD_DATASOURCE_RESIDUE_BAK_20260909'
        Reason  = '字段数据来源重构期的中间表快照，见 Migrations/315_drop_legacy_backup_tables.sql'
        Pattern = '\bFIELD_DATASOURCE_RESIDUE_BAK_20260909\b'
    }
    [pscustomobject]@{
        Name    = 'MODULES_Backup_ADR004'
        Reason  = 'ADR-004 迁移前对 MODULES 的快照，见 Migrations/315_drop_legacy_backup_tables.sql'
        Pattern = '\bMODULES_Backup_ADR004\b'
    }
    [pscustomobject]@{
        Name    = 'SYSDL_Backup_ADR004'
        Reason  = 'ADR-004 迁移前对 SYSDL 的快照，见 Migrations/315_drop_legacy_backup_tables.sql'
        Pattern = '\bSYSDL_Backup_ADR004\b'
    }
    # 坏死遗留函数退役（迁移 316）：三个函数引用改名前的老列，调用即报「列名无效」。
    # 它们在库里能存在，只是因为创建时被引用的表还不存在（SQL Server 的延迟名称解析）。
    [pscustomobject]@{
        Name    = 'dbo.f_get_form_desc'
        Reason  = '引用老列 WFFORM.FORM_IDX / TABLES.T_ID，调用即报错，见 Migrations/316_retire_broken_legacy_functions.sql'
        Pattern = 'f_get_form_desc'
    }
    [pscustomobject]@{
        Name    = 'dbo.f_get_user_desc'
        Reason  = '引用已改名的列 EMP_NAME，调用即报错，见 Migrations/316_retire_broken_legacy_functions.sql'
        Pattern = 'f_get_user_desc'
    }
    [pscustomobject]@{
        Name    = 'dbo.f_get_user_listdesc'
        Reason  = '函数体第 589 字符处调用已退役的 f_get_user_desc，一并退役，见 Migrations/316_retire_broken_legacy_functions.sql'
        Pattern = 'f_get_user_listdesc'
    }
)

function Get-RetiredHit {
    param([string] $Text, [string] $FileName = '')

    $found = New-Object System.Collections.Generic.List[string]

    # SYSDL.G_IDX 的三种真实写法（避免"账号表 SYSDL 与关联表列 G_IDX 同段出现"这类误报）：
    #   1) 别名绑定了 SYSDL，又用该别名访问 .G_IDX（旧 AuthenticationRepository 的形态）；
    #   2) 显式写 SYSDL.G_IDX / dbo.SYSDL.G_IDX；
    #   3) INSERT INTO SYSDL (...) / UPDATE SYSDL SET ... 的列清单里含 G_IDX。
    $aliasPattern = '(?:FROM|JOIN|INTO|UPDATE)\s+(?:dbo\.)?SYSDL\s+(?:WITH\s*\([^)]*\)\s*)?(?<alias>[A-Za-z_]\w*)'
    foreach ($match in [regex]::Matches($Text, $aliasPattern, 'IgnoreCase')) {
        $alias = $match.Groups['alias'].Value
        if ($alias -ieq 'WITH' -or $alias -ieq 'SET') { continue }
        if ([regex]::IsMatch($Text, [regex]::Escape($alias) + '\s*\.\s*G_IDX', 'IgnoreCase')) {
            $found.Add('SYSDL.G_IDX')
            break
        }
    }
    if ([regex]::IsMatch($Text, '(?:dbo\.)?SYSDL\s*\.\s*G_IDX', 'IgnoreCase')) { $found.Add('SYSDL.G_IDX') }
    foreach ($match in [regex]::Matches($Text, '(?:INSERT\s+INTO|UPDATE)\s+(?:dbo\.)?SYSDL\s*\((?<cols>[^)]*)\)', 'IgnoreCase')) {
        if ($match.Groups['cols'].Value -match '\bG_IDX\b') { $found.Add('SYSDL.G_IDX'); break }
    }

    foreach ($entry in $retiredObjects) {
        if ($entry.Name -eq 'SYSDL.G_IDX') { continue }
        $caseSensitive = $entry.PSObject.Properties['CaseSensitive'] -and $entry.CaseSensitive
        $options = if ($caseSensitive) { 'None' } else { 'IgnoreCase' }
        if ([regex]::IsMatch($Text, $entry.Pattern, $options)) { $found.Add($entry.Name) }
    }
    return $found
}

if ($SelfTest) {
    $failures = New-Object System.Collections.Generic.List[string]

    $dirty = @(
        'SELECT l.USER_ID, l.G_IDX FROM dbo.SYSDL l WHERE l.USER_ID=@Id;',
        'INSERT INTO dbo.SYSDL (USER_ID,EMP_ID,G_IDX,USER_PWD) VALUES (@Id,@Emp,@Group,@Hash);',
        'SELECT dbo.f_get_user_gidx(@UserId);',
        # 退役列的真实炸库写法：别名点号取值
        'SELECT f.F_ID, f.FORM_ORDER FROM dbo.FIELDS f ORDER BY f.FORM_ORDER;',
        'SELECT m.M_IDX, m.FORM_COLUMNS FROM dbo.MODULES m;',
        'SELECT l.F_ID FROM dbo.MODULE_FORM_LAYOUT l WHERE l.FORM_CELL_GROUP IS NOT NULL;',
        'SELECT a.MODULE_ID, a.EFFECT_KEY FROM dbo.MODULE_BUSINESS_ACTION a;'
    )
    foreach ($sample in $dirty) {
        if ((Get-RetiredHit -Text $sample).Count -eq 0) {
            $failures.Add("正样本未被拦下：$sample")
        }
    }

    $clean = @(
        'SELECT gu.G_IDX FROM dbo.SYSDG_USER gu WHERE gu.USER_ID=@Id;',
        'SELECT G_IDX, G_DESC FROM dbo.SYSDG;',
        'SELECT * FROM dbo.SYSDL l INNER JOIN dbo.SYSDN n ON n.EMP_ID=l.EMP_ID;',
        # 迁移 191 之后的正确形态：账号表取用户，组取自关联表
        'SELECT l.USER_ID, (SELECT TOP 1 gu.G_IDX FROM dbo.SYSDG_USER gu WHERE gu.USER_ID=l.USER_ID) AS G_IDX FROM dbo.SYSDL l;',
        # 原位占位别名（退役列删掉后仍在模型/仓储里保留的写法）不算引用
        'SELECT NULL AS FORM_TABS, NULL AS FORM_COLUMNS, CAST(1 AS int) AS FORM_TAB_NO FROM dbo.MODULES;',
        # C# 模型同名属性是 PascalCase：不区分大小写会把这两行误判
        'var columns = definition.FormColumns is int c and > 0 ? c : 2;',
        'CompareValue(mismatches, id, title, "formTabs", definition.FormTabs ?? "null");',
        # 改名后的正确形态，以及审计查询为保住对外属性名而保留的投影别名
        'SELECT s.M_IDX, s.VERSION FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WHERE s.IS_CURRENT = 1;',
        'SELECT M_IDX AS MODULE_ID FROM dbo.AUDIT_EVENT;'
    )
    foreach ($sample in $clean) {
        if ((Get-RetiredHit -Text $sample).Count -gt 0) {
            $failures.Add("干净样本被误判：$sample")
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output 'FAIL 退役对象门禁自检未通过：'
        $failures | ForEach-Object { Write-Output "  $_" }
        exit 1
    }
    Write-Output "PASS 退役对象门禁自检：$($retiredObjects.Count) 条清单，正样本拦下、干净样本放行。"
    exit 0
}

$files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
# 注意：循环变量不能叫 $root —— PowerShell 变量名不区分大小写，会覆盖参数 $Root
foreach ($area in @('EOS.API', 'EOS.API.Tests', 'scripts')) {
    $path = Join-Path $Root $area
    if (-not (Test-Path $path)) { continue }
    foreach ($file in Get-ChildItem -Path $path -Recurse -File) {
        if ($file.Extension -notin '.cs', '.ps1') { continue }
        if ($file.FullName -match '\\(bin|obj)\\') { continue }
        if ($file.FullName -match '\\Migrations\\') { continue }
        if ($file.FullName -eq $PSCommandPath) { continue }   # 本脚本自身持有退役清单
        $files.Add($file)
    }
}

$hits = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    $text = Get-Content -Raw -Encoding UTF8 $file.FullName
    foreach ($name in (Get-RetiredHit -Text $text)) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        $hits.Add("$relative → $name")
    }
}

if ($hits.Count -gt 0) {
    Write-Output 'FAIL 已退役的库对象仍被引用（退役后漏改会只在运行时炸成 500）：'
    $hits | Sort-Object -Unique | ForEach-Object {
        $name = ($_ -split ' → ')[-1]
        $why = ($retiredObjects | Where-Object { $_.Name -eq $name } | Select-Object -First 1).Reason
        Write-Output "  $_"
        Write-Output "      依据：$why"
    }
    exit 1
}

# ---- 已退役的目录 / 项目：不得重新入库 ------------------------------------
# SSDT 快照项目（EOS.Database/）已整体移出仓库，内容归档为两个 git tag：
#   archive/legacy-ssdt-full-snapshot（2026-08-23 旧全量，438 个过程）
#   archive/legacy-ssdt-snapshot（退役当时的快照）
# 判据刻意取"目录与解决方案入口是否存在"，而不是扫文本引用——归档 tag 里的路径串
# （git show <tag>:EOS.Database/...）是**合法用法**，扫文本会把还原脚本误判。
# 这两条断言是 Test-Path / 单行正则，不纳入 -SelfTest（无判别力可自证）。
$pathProblems = New-Object System.Collections.Generic.List[string]
if (Test-Path (Join-Path $Root 'EOS.Database')) {
    $pathProblems.Add('EOS.Database/ 重新出现：该 SSDT 快照项目已退役（看旧 schema 用 git show archive/legacy-ssdt-full-snapshot:EOS.Database/...）')
}
if (Test-Path (Join-Path $Root 'EOS.Database/EOS.Database.sqlproj')) {
    $pathProblems.Add('EOS.Database.sqlproj 重新出现：SSDT 项目不得重新入库（它会漂移，且 DeployToDatabase 是对账式部署）')
}
$solutionFile = Join-Path $Root 'EOS.slnx'
if ((Test-Path $solutionFile) -and ((Get-Content -Raw -Encoding UTF8 $solutionFile) -match 'EOS\.Database')) {
    $pathProblems.Add('EOS.slnx 重新引用已退役的 EOS.Database 项目')
}
if ($pathProblems.Count -gt 0) {
    Write-Output 'FAIL 已退役的目录 / 项目重新出现：'
    $pathProblems | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "PASS 无退役库对象残留引用（清单 $($retiredObjects.Count) 条，扫描 $($files.Count) 个文件）；已退役目录/项目未重新出现（EOS.Database）。"
exit 0