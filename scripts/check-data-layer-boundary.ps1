<#
.SYNOPSIS
    分层边界门禁：EOS.API/Data/ 根目录的 .cs 文件集合不得新增（白名单棘轮，只减不增）。

.DESCRIPTION
    Data/ 根目录已平铺 91 个 .cs，里面混着领域服务、仓储、解析器、校验器、渲染器与基础设施。
    实测代价不是"看着乱"，而是**重复实现会回潮**：同一件事的既有实现散在 91 个平铺文件里
    找不到，新功能就再写一份（同一安全语义两份实现、两处各维护一份算子表，原因都在这里）。
    因此定一条可机械检查的边界：

      新代码落位规则
        · 领域能力（新的域服务 / 单据规则 / 效果处理器）→ EOS.API/Features/<域>/
        · 与 Data 同层的共享原语（SQL / 解析 / 校验）→ Data/ 下**已有子目录**或新建子目录
        · 只有确实属于 Data 根定位的少数文件才留在根目录，且必须**显式**加进本脚本白名单

    棘轮语义：白名单**只减不增**。把文件拆进子目录、或迁去 Features 后，应同步从白名单删除；
    删除只提示不报错。新增 Data/ 根目录文件即 FAIL——加白名单是**一次需要说明理由的显式动作**，
    不是随手放行。

    刻意不做的事：本脚本不判断"某个文件该不该是领域服务"（那需要语义理解，会误报）。
    它只守"根目录不再变胖"这一条**确定性**事实。

.EXAMPLE
    pwsh scripts/check-data-layer-boundary.ps1            # exit 0 = clean
    pwsh scripts/check-data-layer-boundary.ps1 -SelfTest  # 正反自检：埋一个新增文件必须被判 FAIL
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$dataDir = Join-Path $Root 'EOS.API/Data'

# 棘轮基线：2026-09-24 现状。只允许从中删除，不允许未经说明地追加。
$allowed = @'
AmountCalculator.cs
AssistantQueryBuilder.cs
AssistantRepository.cs
AssistantUsageRepository.cs
AttachmentRepository.cs
AttendanceCalcService.cs
AuthenticationRepository.cs
BillNoGenerator.cs
BusinessActionCatalog.cs
BusinessActionLabels.cs
ChooserConditionBuilder.cs
ChooserFilterCompiler.cs
ChooserFilterModel.cs
ChooserFilterValidator.cs
ChooserJoinCatalog.cs
ChooserModels.cs
ChooserRepository.cs
ConditionTemplateParser.cs
ConvertFunctionResolver.cs
DataFilterParser.cs
DbConnectionFactory.cs
DepotStockPolicyService.cs
DocumentPdfService.cs
DocumentWorkbenchRepository.cs
EffectStructSchemas.cs
ErpDatabaseInitializer.cs
FieldAdminRepository.cs
FlowDefinitionService.cs
FormChooserSourceMemo.cs
FormDefaultRules.cs
FormFieldSelector.cs
GroupExpressionParser.cs
HumanResourceJobsService.cs
ILayoutRenderer.cs
KnowledgeRepository.cs
LayoutExceptions.cs
MenuAdminRepository.cs
MenuIconResolver.cs
ModuleBusinessConfigRepository.cs
ModuleBusinessConfigValidator.cs
ModuleBusinessMap.cs
ModuleRightsRepository.cs
ModuleRouteValidator.cs
MrpRecalcService.cs
NavigationGroupsRepository.cs
NavigationRepository.cs
PdfLayout.cs
PrintService.cs
PrintSettingsRepository.cs
QuestPdfLayoutRenderer.cs
RecordModels.cs
RecordPayloadValidator.cs
ReportAdminRepository.cs
ReportAdminValidator.cs
ReportAggregateRegistry.cs
ReportFormatRepository.cs
ReportFormatValidator.cs
ReportFormLayoutRepository.cs
ReportInboxRepository.cs
ReportListGrouping.cs
ReportListPdfComposer.cs
ReportPdfService.cs
ReportRepository.cs
RestrictedExpressionService.cs
RightsAdminRepository.cs
SamplePrintDataFactory.cs
SearchCenterRepository.cs
SqlDataReaderExtensions.cs
SystemParameterService.cs
UserAdminRepository.cs
VirtualArithmeticParser.cs
VirtualColumnResolver.cs
WorkbenchApprovalService.cs
WorkbenchAuditWriter.cs
WorkbenchBrowseResolver.cs
WorkbenchChooserService.cs
WorkbenchCommandHandler.cs
WorkbenchDefinitionBuilder.cs
WorkbenchDefinitionProvider.cs
WorkbenchDefinitionSnapshotService.cs
WorkbenchDefinitionValidator.cs
WorkbenchDirtyMarker.cs
WorkbenchFieldMetaMapper.cs
WorkbenchIdempotency.cs
WorkbenchKeyCondition.cs
WorkbenchQueryComposer.cs
WorkbenchScopeFilter.cs
WorkbenchSql.cs
WorkbenchVirtualColumnResolver.cs
WorkflowEngine.cs
WorkflowStates.cs
WriteFailureTranslator.cs
'@ -split "`r?`n" | Where-Object { $_ -ne '' }

function Get-RootDataFiles {
    param([string] $Directory)
    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.cs' | Select-Object -ExpandProperty Name | Sort-Object)
}

function Test-Gate {
    param([string[]] $Files)

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($file in $Files) {
        if ($allowed -contains $file) { continue }
        $problems.Add("EOS.API/Data/$file 是新增的根目录文件：请改落 Data/<域>/ 子目录或 Features/<域>/；" +
                      "确需留在根目录时，把文件名显式加进 scripts/check-data-layer-boundary.ps1 的白名单并说明理由")
    }
    return $problems
}

if ($SelfTest) {
    $probe = Join-Path $dataDir '__boundary_gate_selftest.cs'
    $null = New-Item -ItemType File -Path $probe -Force
    try {
        Set-Content -LiteralPath $probe -Encoding UTF8 -Value '// selftest probe'
        $dirty = Get-RootDataFiles -Directory $dataDir
        $found = @(Test-Gate -Files $dirty)
        if ($found.Count -eq 0) { throw '自检失败：埋入的根目录新文件没有被门禁发现（门禁形同虚设）。' }
        if (-not ($found -join ' ').Contains('__boundary_gate_selftest.cs')) {
            throw '自检失败：门禁报了问题，但没有指向埋入的探针文件（判定对象不对）。'
        }
        Write-Host '  [PASS] 反向自检：埋入的根目录新文件被判 FAIL'
    }
    finally {
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    }
    $clean = Get-RootDataFiles -Directory $dataDir
    $cleanProblems = Test-Gate -Files $clean
    if ($cleanProblems.Count -gt 0) { throw "自检失败：清理探针后仍报新增 ⇒ $($cleanProblems[0])" }
    Write-Host "  [PASS] 正向自检：当前根目录 $($clean.Count) 个文件全部在白名单内"
    Write-Host '-- SELFTEST OK'
    exit 0
}

$files = Get-RootDataFiles -Directory $dataDir
$problems = Test-Gate -Files $files
if ($problems.Count -gt 0) {
    Write-Host '== Data 分层边界门禁 =='
    foreach ($line in $problems) { Write-Host "  [FAIL] $line" }
    Write-Host "-- FAIL（根目录 $($files.Count) 个文件，白名单 $($allowed.Count) 个）"
    exit 1
}

# 棘轮：白名单里已不存在的名字，说明发生过拆分/迁移，应同步收缩白名单（只减不增）。
$retired = $allowed | Where-Object { $files -notcontains $_ }
if ($retired.Count -gt 0) {
    foreach ($file in $retired) {
        Write-Host "  [INFO] $file 已不在 Data/ 根目录：可从白名单移除（棘轮只减不增）。"
    }
}
Write-Host "PASS Data 分层边界：根目录 $($files.Count) 个文件，全部在白名单内（白名单 $($allowed.Count) 项）。"
exit 0
