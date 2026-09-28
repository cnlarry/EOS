#Requires -Version 7.0
<#
.SYNOPSIS
EOS 开发/测试语义化测试数据种子（用户拍板 2026-08-23，决策清单 §17）。

.DESCRIPTION
当前系统处于开发阶段、无真实 ERP 用户，放量主线被"无引用数据"阻塞。本脚本按
上下文语意与真实业务场景生成测试数据，解除以下阻塞：
  - 170204 其它付款凭证：RECEIVE 结算方式 / ACCOUNT_TYPE 帐款类型；
  - 2002  原纸条码资料：PAP_TYPE / PAP_BRAND / PAP_GRAMME / PAP_SPECS 原纸基础链；
  - 3001/3002/3004 海关进/出仓单：CUS_DEPOT 海关库别；
  - 3302  抱怨退货处理单：为流水线首选客户（668ZS，CLIENT 选择器第一行）补
    品号 → 库存（130103 其它入库）→ 已批核送货单（1406）完整链。
  - 3305/3306 重工/特采引用链：已批核客诉单（RESULT1+RESULT3）与异常单
    （RESULT1+RESULT2），使 3305/3306 的 COMPLAIN_NO/EXCEPTION_NO 选择器可解析。

幂等：基础资料与 3302 链均按存在性跳过；可重复执行。测试数据带 EOSDEV 前缀，
可被真实数据替换。单据链经 EOS.API 受控端点创建；批核标记/基础资料为测试种子
直接落库（对齐 E2E 种子与 EOS-17 BANK 先例）。
.EXAMPLE
.\scripts\seed-dev-data.ps1                 # 基础资料 + 3302 链
.\scripts\seed-dev-data.ps1 -SkipDeliveryChain   # 只种基础资料
#>
param(
    [string]$ApiUrl = 'http://localhost:5261',
    [string]$UserId = 'admin',
    [string]$Password = 'admin',
    [switch]$SkipBaseSeed,
    [switch]$SkipDeliveryChain,
    [switch]$SkipQualityChain
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')

$clientId = '668ZS'          # CLIENT 选择器（3302 必填）首行：已确认客户中 CLIENT_ID 最小
$proNo    = 'EOSDEV-PK-001'  # 668ZS 唯一已确认品号（PRO_NO 升序首行，3302 品号选择器命中）
$depotId  = 'CP'             # 华精仓（DEPOT 已有数据）

function Write-Step {
    param([string]$Message)
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

# ---------------- A. 基础资料（幂等 SQL 种子） ----------------
if (-not $SkipBaseSeed) {
    Write-Step '基础资料种子：RECEIVE / ACCOUNT_TYPE / CUS_DEPOT / PAP_*'
    # 元数据修正：AMOUNT_WORD（金额大写）物理列为 nvarchar，FIELDS.F_TYPE 误标 float 会令
    # 保存校验按数值解析中文大写金额而失败（170204/170104）。修正为 nvarchar（幂等）。
    Invoke-EosSqlNonQuery -Query "UPDATE dbo.FIELDS SET F_TYPE=N'nvarchar', LAST_UPDATE_BY=N'EOS-SEED', LAST_UPDATE_DATE=GETDATE() WHERE F_ID=N'AMOUNT_WORD' AND T_ID IN (N'COP_RECEIPT_OTHER', N'PUR_PAY_OTHER') AND LOWER(LTRIM(RTRIM(F_TYPE)))=N'float';" | Out-Null
    $baseSql = @'
SET NOCOUNT ON;

-- 结算方式（170204 RECEIVE_ID 依赖；110109 结算方式设定）
IF NOT EXISTS (SELECT 1 FROM dbo.RECEIVE WHERE LTRIM(RTRIM(RECEIVE_ID))='CASH')
  INSERT dbo.RECEIVE (RECEIVE_ID, RECEIVE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('CASH', N'现金', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RECEIVE WHERE LTRIM(RTRIM(RECEIVE_ID))='TT')
  INSERT dbo.RECEIVE (RECEIVE_ID, RECEIVE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('TT', N'电汇', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RECEIVE WHERE LTRIM(RTRIM(RECEIVE_ID))='M30')
  INSERT dbo.RECEIVE (RECEIVE_ID, RECEIVE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('M30', N'月结30天', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RECEIVE WHERE LTRIM(RTRIM(RECEIVE_ID))='CHK')
  INSERT dbo.RECEIVE (RECEIVE_ID, RECEIVE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('CHK', N'支票', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

-- 帐款类型（170204 ACCOUNT_TYPE_ID 依赖；110110 帐款类型设定）
IF NOT EXISTS (SELECT 1 FROM dbo.ACCOUNT_TYPE WHERE LTRIM(RTRIM(ACCOUNT_TYPE_ID))='AP')
  INSERT dbo.ACCOUNT_TYPE (ACCOUNT_TYPE_ID, ACCOUNT_TYPE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('AP', N'应付帐款', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.ACCOUNT_TYPE WHERE LTRIM(RTRIM(ACCOUNT_TYPE_ID))='PREPAY')
  INSERT dbo.ACCOUNT_TYPE (ACCOUNT_TYPE_ID, ACCOUNT_TYPE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('PREPAY', N'预付帐款', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.ACCOUNT_TYPE WHERE LTRIM(RTRIM(ACCOUNT_TYPE_ID))='AR')
  INSERT dbo.ACCOUNT_TYPE (ACCOUNT_TYPE_ID, ACCOUNT_TYPE_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('AR', N'应收帐款', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

-- 海关库别（3001/3002/3004 明细 DEPOT_ID 依赖）
IF NOT EXISTS (SELECT 1 FROM dbo.CUS_DEPOT WHERE LTRIM(RTRIM(DEPOT_ID))='BS')
  INSERT dbo.CUS_DEPOT (DEPOT_ID, DEPOT_NAME, DEPOT_TYPE, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('BS', N'保税仓', '1', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.CUS_DEPOT WHERE LTRIM(RTRIM(DEPOT_ID))='CM')
  INSERT dbo.CUS_DEPOT (DEPOT_ID, DEPOT_NAME, DEPOT_TYPE, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('CM', N'海关料件仓', '1', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.CUS_DEPOT WHERE LTRIM(RTRIM(DEPOT_ID))='CG')
  INSERT dbo.CUS_DEPOT (DEPOT_ID, DEPOT_NAME, DEPOT_TYPE, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('CG', N'海关成品仓', '1', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

-- 原纸基础链（2002 PAP_BARCODE_M 依赖）
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_TYPE WHERE LTRIM(RTRIM(TYPE_ID))='KL')
  INSERT dbo.PAP_TYPE (TYPE_ID, TYPE_NAME, TYPE_SORT, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('KL', N'牛皮卡纸', '1', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_TYPE WHERE LTRIM(RTRIM(TYPE_ID))='NB')
  INSERT dbo.PAP_TYPE (TYPE_ID, TYPE_NAME, TYPE_SORT, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('NB', N'瓦楞芯纸', '2', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_TYPE WHERE LTRIM(RTRIM(TYPE_ID))='WB')
  INSERT dbo.PAP_TYPE (TYPE_ID, TYPE_NAME, TYPE_SORT, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('WB', N'白面牛卡', '3', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

IF NOT EXISTS (SELECT 1 FROM dbo.PAP_BRAND WHERE LTRIM(RTRIM(BRAND_ID))='ND')
  INSERT dbo.PAP_BRAND (BRAND_ID, BRAND_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('ND', N'玖龙', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_BRAND WHERE LTRIM(RTRIM(BRAND_ID))='LW')
  INSERT dbo.PAP_BRAND (BRAND_ID, BRAND_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('LW', N'理文', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_BRAND WHERE LTRIM(RTRIM(BRAND_ID))='SY')
  INSERT dbo.PAP_BRAND (BRAND_ID, BRAND_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('SY', N'山鹰', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

IF NOT EXISTS (SELECT 1 FROM dbo.PAP_GRAMME WHERE LTRIM(RTRIM(GRAMME_ID))='100')
  INSERT dbo.PAP_GRAMME (GRAMME_ID, GRAMME_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('100', N'100g', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_GRAMME WHERE LTRIM(RTRIM(GRAMME_ID))='120')
  INSERT dbo.PAP_GRAMME (GRAMME_ID, GRAMME_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('120', N'120g', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_GRAMME WHERE LTRIM(RTRIM(GRAMME_ID))='150')
  INSERT dbo.PAP_GRAMME (GRAMME_ID, GRAMME_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('150', N'150g', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_GRAMME WHERE LTRIM(RTRIM(GRAMME_ID))='170')
  INSERT dbo.PAP_GRAMME (GRAMME_ID, GRAMME_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('170', N'170g', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_GRAMME WHERE LTRIM(RTRIM(GRAMME_ID))='200')
  INSERT dbo.PAP_GRAMME (GRAMME_ID, GRAMME_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('200', N'200g', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);

IF NOT EXISTS (SELECT 1 FROM dbo.PAP_SPECS WHERE LTRIM(RTRIM(SPECS_ID))='W1100')
  INSERT dbo.PAP_SPECS (SPECS_ID, SPECS_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('W1100', N'1100mm', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_SPECS WHERE LTRIM(RTRIM(SPECS_ID))='W1300')
  INSERT dbo.PAP_SPECS (SPECS_ID, SPECS_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('W1300', N'1300mm', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_SPECS WHERE LTRIM(RTRIM(SPECS_ID))='W1500')
  INSERT dbo.PAP_SPECS (SPECS_ID, SPECS_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('W1500', N'1500mm', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.PAP_SPECS WHERE LTRIM(RTRIM(SPECS_ID))='W1800')
  INSERT dbo.PAP_SPECS (SPECS_ID, SPECS_NAME, CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
  VALUES ('W1800', N'1800mm', 'EOS-SEED', GETDATE(), 'EOS-SEED', GETDATE(), 1);
'@
    Invoke-EosSqlNonQuery -Query $baseSql | Out-Null
    $counts = Invoke-EosSql -Query "SET NOCOUNT ON; SELECT 'RECEIVE', COUNT(*) FROM dbo.RECEIVE UNION ALL SELECT 'ACCOUNT_TYPE', COUNT(*) FROM dbo.ACCOUNT_TYPE UNION ALL SELECT 'CUS_DEPOT', COUNT(*) FROM dbo.CUS_DEPOT UNION ALL SELECT 'PAP_TYPE', COUNT(*) FROM dbo.PAP_TYPE UNION ALL SELECT 'PAP_BRAND', COUNT(*) FROM dbo.PAP_BRAND UNION ALL SELECT 'PAP_GRAMME', COUNT(*) FROM dbo.PAP_GRAMME UNION ALL SELECT 'PAP_SPECS', COUNT(*) FROM dbo.PAP_SPECS"
    Write-Host "  基础资料行数：$($counts -join '; ')" -ForegroundColor Green
}

# ---------------- B. 3302 链（品号 → 库存 → 已批核送货单） ----------------
if (-not $SkipDeliveryChain) {
    Write-Step "3302 链：品号 $proNo → 库存(130103) → 送货单(1406 批核)"

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    function Call-Api {
        param([string]$Method, [string]$Path, $Body = $null)
        $params = @{ Uri = "$ApiUrl$Path"; Method = $Method; WebSession = $session; SkipHttpErrorCheck = $true }
        if ($null -ne $Body) { $params.ContentType = 'application/json'; $params.Body = ($Body | ConvertTo-Json -Depth 10) }
        # Form write path requires an idempotency key — inject a fresh one per call (one call = one user intent)
        if ($Method -match '^(POST|PUT|DELETE)$' -and $Path -match '/document-workbench/\d+/(record|approve|deapprove|endcase|unendcase)') {
            $params.Headers = @{ 'X-Idempotency-Key' = [guid]::NewGuid().ToString('N') }
        }
        $response = Invoke-WebRequest @params
        return @{ Status = [int]$response.StatusCode; Content = if ($response.Content) { $response.Content | ConvertFrom-Json } else { $null } }
    }
    function New-Record {
        param([int]$ModuleId, [hashtable]$Values, [object[]]$Details = @())
        return Call-Api 'POST' "/api/v1/document-workbench/$ModuleId/record" @{ Values = $Values; Details = $Details }
    }
    function Invoke-Workflow {
        param([int]$ModuleId, [object[]]$Key, [bool]$Approve)
        $action = if ($Approve) { 'approve' } else { 'deapprove' }
        return Call-Api 'POST' "/api/v1/document-workbench/$ModuleId/$action" @{ key = (ConvertTo-Json -Compress -InputObject $Key) }
    }

    $login = Call-Api 'POST' '/api/v1/auth/login' @{ userId = $UserId; password = $Password }
    if ($login.Status -ne 200) { throw "登录失败：$($login.Status) $($login.Content)" }

    # 1) 品号（1201）：走受控写路径创建，测试种子补批核标记（旧 P_WF_PRODUCT 为加密 SP，不触发）
    $prodCount = [int]([string](Invoke-EosSql -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO))='$proNo';" | Select-Object -First 1)).Trim()
    if ($prodCount -eq 0) {
        $product = New-Record 1201 @{
            PRO_NO = $proNo; PRO_NAME = 'EOS 开发测试瓦楞纸箱'; CLIENT_ID = $clientId
            UNIT_ID = 'KG'; LAST_PURCHASE_CURR_ID = 'RMB'; DEPOT_ID = $depotId
            LAST_PURCHASE_UNIT_ID = 'KG'; MAIN_SOURCE = '2'; PRO_SPEC = 'EOSDEV 测试规格'; PRO_TYPE = '1'
        }
        if ($product.Status -ne 200) { throw "品号创建失败（1201）：$($product.Status) $($product.Content)" }
    }
    Invoke-EosSqlNonQuery -Query "UPDATE dbo.PRODUCT SET CLIENT_ID='$clientId', CONFIRM_TAG=1, CONFIRM_PERSON='EOS-SEED', CONFIRM_DATE=GETDATE(), BUSINESS_TAG=ISNULL(BUSINESS_TAG,0) WHERE LTRIM(RTRIM(PRO_NO))='$proNo';" | Out-Null
    Write-Host "  PASS 品号 $proNo（CLIENT_ID=$clientId，已标批核）" -ForegroundColor Green

    # 2) 库存（130103 其它入库）：QTY=100 / CP，批核
    $stockQty = [int]([string](Invoke-EosSql -Query "SET NOCOUNT ON; SELECT ISNULL(SUM(QTY),0) FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO))='$proNo' AND LTRIM(RTRIM(DEPOT_ID))='$depotId';" | Select-Object -First 1)).Trim()
    if ($stockQty -lt 100) {
        $stockDate = Get-Date -Format 'yyyy-MM-dd'   # datetime 字段按 ISO 日期提交（长度校验已修复）
        $invIn = New-Record 130103 @{ OCCUR_DATE = $stockDate; EMP_ID = 'aaron' } @(
            @{ PRO_NO = $proNo; QTY = '100'; DEPOT_ID = $depotId; UNIT_ID = 'KG' }
        )
        if ($invIn.Status -ne 200) { throw "其它入库创建失败（130103）：$($invIn.Status) $($invIn.Content)" }
        $invKey = @($invIn.Content.key)
        $invApprove = Invoke-Workflow 130103 $invKey $true
        if ($invApprove.Status -ne 200) { throw "其它入库批核失败（130103）：$($invApprove.Status) $($invApprove.Content)" }
        Write-Host "  PASS 其它入库 QTY=100/$depotId 已批核（原库存 $stockQty）" -ForegroundColor Green
    } else {
        Write-Host "  库存充足（$stockQty/$depotId），跳过其它入库" -ForegroundColor Yellow
    }

    # 3) 送货单（1406）：QTY=10 / CP，批核 → COP_SEND_M.CONFIRM_TAG=1，3302 PRODUCE_NO 可命中
    $sendCount = [int]([string](Invoke-EosSql -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.COP_SEND_D d JOIN dbo.COP_SEND_M m ON m.SEND_TYPE=d.SEND_TYPE AND m.SEND_NO=d.SEND_NO WHERE d.PRO_NO='$proNo' AND m.CONFIRM_TAG=1;" | Select-Object -First 1)).Trim()
    if ($sendCount -eq 0) {
        $stockDate = Get-Date -Format 'yyyy-MM-dd'
        $send = New-Record 1406 @{
            SEND_DATE = $stockDate; CLIENT_ID = $clientId; CURR_ID = 'RMB'; CURR_RATE = '1'
            TAX_ID = 'TAX02'; TAX_TYPE = 'O'; TAX_RATE = '13'
        } @(
            @{ PRO_NO = $proNo; QTY = '10'; DEPOT_ID = $depotId; UNIT_ID = 'KG'; PRICE = '100' }
        )
        if ($send.Status -ne 200) { throw "送货单创建失败（1406）：$($send.Status) $($send.Content)" }
        $sendKey = @($send.Content.key)
        $sendApprove = Invoke-Workflow 1406 $sendKey $true
        if ($sendApprove.Status -ne 200) { throw "送货单批核失败（1406）：$($sendApprove.Status) $($sendApprove.Content)" }
        Write-Host "  PASS 送货单（$($sendKey -join '/')）已批核，3302 引用链就绪" -ForegroundColor Green
    } else {
        Write-Host "  已存在 $proNo 的已批核送货明细（$sendCount 条），跳过" -ForegroundColor Yellow
    }
}

# ---------------- C. 3305/3306 引用链（客诉单 + 异常单） ----------------
if (-not $SkipQualityChain) {
    Write-Step "3305/3306 链：已批核客诉单（RESULT1+RESULT3）+ 异常单（RESULT1+RESULT2）"
    $cpNo = 'EOSDEV-CP-0001'
    $exNo = 'EOSDEV-EX-0001'
    $cpCount = [int]([string](Invoke-EosSql -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.QC_COMPLAIN_M WHERE LTRIM(RTRIM(COMPLAIN_NO))='$cpNo';" | Select-Object -First 1)).Trim()
    if ($cpCount -eq 0) {
        $cp = New-Record 3302 @{
            CLIENT_ID = $clientId; PRO_NO = $proNo; PRODUCE_TYPE = 'SHD'; PRODUCE_NO = 'SH26080152'
            COMPLAIN_TYPE = 'BYTH'; COMPLAIN_NO = $cpNo; COMPLAIN_DATE = (Get-Date -Format 'yyyy-MM-dd')
            RESULT1 = '1'; RESULT3 = '1'; IF_RETURN = '1'
        }
        if ($cp.Status -ne 200) { throw "客诉单创建失败（3302）：$($cp.Status) $($cp.Content)" }
        Write-Host "  PASS 客诉单 $cpNo 已创建" -ForegroundColor Green
    } else {
        Write-Host "  客诉单 $cpNo 已存在，跳过创建" -ForegroundColor Yellow
    }
    $exCount = [int]([string](Invoke-EosSql -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.QC_EXCEPTION_M WHERE LTRIM(RTRIM(EXCEPTION_NO))='$exNo';" | Select-Object -First 1)).Trim()
    if ($exCount -eq 0) {
        $ex = New-Record 3301 @{
            EXCEPTION_TYPE = 'YCCL'; EXCEPTION_NO = $exNo; CLIENT_ID = $clientId; PRO_NO = $proNo
            RESULT1 = '1'; RESULT2 = '1'
        }
        if ($ex.Status -ne 200) { throw "异常单创建失败（3301）：$($ex.Status) $($ex.Content)" }
        Write-Host "  PASS 异常单 $exNo 已创建" -ForegroundColor Green
    } else {
        Write-Host "  异常单 $exNo 已存在，跳过创建" -ForegroundColor Yellow
    }
    # 3302/3301 无流程 SP，批核端点按设计返回 404（WORKFLOW_NOT_SUPPORTED）；
    # 测试种子直接标 RESULT 位 + CONFIRM_TAG（对齐品号/库存链的种子做法）。
    Invoke-EosSqlNonQuery -Query "SET NOCOUNT ON;
        UPDATE dbo.QC_COMPLAIN_M SET RESULT1=1, RESULT3=1, CONFIRM_TAG=1, CONFIRM_PERSON=N'EOS-SEED', CONFIRM_DATE=GETDATE()
        WHERE LTRIM(RTRIM(COMPLAIN_NO))='$cpNo';
        UPDATE dbo.QC_EXCEPTION_M SET RESULT1=1, RESULT2=1, CONFIRM_TAG=1, CONFIRM_PERSON=N'EOS-SEED', CONFIRM_DATE=GETDATE()
        WHERE LTRIM(RTRIM(EXCEPTION_NO))='$exNo';" | Out-Null
    Write-Host "  PASS 客诉单/异常单已确认（3305/3306 COMPLAIN_NO/EXCEPTION_NO 选择器可解析）" -ForegroundColor Green
}

Write-Host "种子完成。可重跑：scripts\auto-enable-batch.ps1 -ModuleIds 3302,170204,2002,3001,3002,3004" -ForegroundColor Cyan
