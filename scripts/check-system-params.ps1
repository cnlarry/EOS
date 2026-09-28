<#
.SYNOPSIS
    系统参数门禁：参数定义完整性 + 引用键存在性 + 归属模块合法性。

.DESCRIPTION
    背景：系统参数（表 dbo.SYSSS，一行一个参数）把"参数定义"从列结构搬进了数据，
    因此编译与单测都拦不住三类漂移：

      1. 定义不全：某一行的类型/分组/说明/生效范围缺失或取值非法——页面渲染不出来，
         或改参数时没有人知道它做什么；
      2. 引用悬空：配置（校验规则、效果动作、已发布快照）或代码里点名了某个参数键，
         而参数表里没有这一行。运行期按"缺键即关闭"处理，于是**开关静默失效**，
         比报错更难发现；
      3. 归属越界：OWNER_MODULE 不在登记的设置模块内（页面取不到、权限门对不上）。

    引用来源与判定：
      · 校验规则  MODULE_VALIDATION_RULE.PARAM_STRUCT           —— switch.gates / switch.key
      · 效果动作  MODULE_BUSINESS_ACTION.{CONDITION,PARAM,REVERSE}_STRUCT
      · 已发布快照 WORKBENCH_DEFINITION_SNAPSHOT.DEFINITION_JSON（内嵌配置，转义 JSON 形式）
      · 源码      EOS.API/**/*.cs 中按参数键直取的调用点
      对每个引用，键必须存在于该 scope 对应的 OWNER_MODULE 下。

.PARAMETER SelfTest
    正反自检：先确认当前库通过，再验证"伪造一个不存在的引用键"能被本脚本判出。

.EXAMPLE
    pwsh scripts/check-system-params.ps1
    pwsh scripts/check-system-params.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [switch] $SelfTest,
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

$AllowedModules = @{ 110111 = '系统参数设置'; 180213 = '考勤数据设置'; 180662 = '考勤数据设置（月度）' }
$problems = [System.Collections.Generic.List[string]]::new()

function Get-ParameterRows {
    <# 参数表：OWNER_MODULE|PARAM_KEY|VALUE_TYPE|DEFAULT_VALUE|GROUP_CODE|SEQ_NO 的规范化文本行 #>
    $sql = @"
SET NOCOUNT ON;
SELECT CONVERT(varchar(10), OWNER_MODULE) + N'|' + CONVERT(varchar(64), PARAM_KEY) + N'|'
     + CONVERT(varchar(16), VALUE_TYPE) + N'|' + ISNULL(CONVERT(varchar(4000), DEFAULT_VALUE), N'~NULL~') + N'|'
     + CONVERT(varchar(32), GROUP_CODE) + N'|' + CONVERT(varchar(10), SEQ_NO)
FROM dbo.SYSSS ORDER BY OWNER_MODULE, GROUP_CODE, SEQ_NO, PARAM_KEY;
"@
    return Invoke-EosSqlQuery -Query $sql -ConnectionString $ConnectionString
}

function Get-WindowSql {
    <# 在若干 JSON 来源里定位字面量并返回其后的定长窗口（窗口边长固定，避开 sqlcmd 列宽截断） #>
    param([string] $Pattern, [int] $Width = 200)
    $tables = @(
        @{ Table = 'dbo.MODULE_VALIDATION_RULE'; Column = 'PARAM_STRUCT' },
        @{ Table = 'dbo.MODULE_BUSINESS_ACTION'; Column = 'CONDITION_STRUCT' },
        @{ Table = 'dbo.MODULE_BUSINESS_ACTION'; Column = 'PARAM_STRUCT' },
        @{ Table = 'dbo.MODULE_BUSINESS_ACTION'; Column = 'REVERSE_STRUCT' },
        @{ Table = 'dbo.WORKBENCH_DEFINITION_SNAPSHOT'; Column = 'DEFINITION_JSON'; Extra = 'IS_CURRENT = 1' }
    )
    $union = ($tables | ForEach-Object {
        $where = "CHARINDEX(N'$Pattern', CAST($($_.Column) AS nvarchar(max))) > 0"
        if ($_.ContainsKey('Extra')) { $where = "$($_.Extra) AND $where" }
        "SELECT CAST($($_.Column) AS nvarchar(max)) AS j FROM $($_.Table) WHERE $where"
    }) -join "`nUNION ALL`n"
    return @"
SET NOCOUNT ON;
;WITH src AS (
$union
), r AS (
    SELECT j, CHARINDEX(N'$Pattern', j) AS p FROM src
    UNION ALL
    SELECT j, CHARINDEX(N'$Pattern', j, p + 1) FROM r WHERE CHARINDEX(N'$Pattern', j, p + 1) > 0
)
SELECT DISTINCT SUBSTRING(CAST(j AS nvarchar(max)), p, $Width) FROM r OPTION (MAXRECURSION 0);
"@
}

function Get-ConfigReferences {
    <#
      从配置与快照里抽出 (Scope, Key) 引用。
      定位用"裸 token"（SYSSS / HR_SETUP / HRM_SETUP / gateFlag），窗口从其出现处向后取，
      再在窗口内截断到下一个 scope 标记——这样"某个门控的 key"不会被相邻门控的 key 混进来。
      快照里的配置是转义 JSON（\u0022），两种形式都要认。
    #>
    param([hashtable] $Scopes)
    $refs = [System.Collections.Generic.List[hashtable]]::new()
    $keyRegex = [regex]'(?:\\u0022|")key(?:\\u0022|")\s*:\s*(?:\\u0022|")([A-Z0-9_]+)'
    $fieldRegex = [regex]'(?:\\u0022|")([A-Za-z]+Field)(?:\\u0022|")\s*:\s*(?:\\u0022|")([A-Z0-9_]+)'
    $scopeBoundary = [regex]'(?:\\u0022|")scope(?:\\u0022|")'
    foreach ($scope in @($Scopes.Keys)) {
        $owner = $Scopes[$scope]
        foreach ($window in (Invoke-EosSqlQuery -Query (Get-WindowSql -Pattern $scope) -ConnectionString $ConnectionString)) {
            $boundary = $scopeBoundary.Match($window, 1)
            $cut = if ($boundary.Success) { $window.Substring(0, $boundary.Index) } else { $window }
            foreach ($match in $keyRegex.Matches($cut)) {
                $refs.Add(@{ Scope = $scope; Owner = $owner; Key = $match.Groups[1].Value; Source = "switch@$scope" })
            }
            foreach ($match in $fieldRegex.Matches($cut)) {
                $refs.Add(@{ Scope = $scope; Owner = $owner; Key = $match.Groups[2].Value; Source = "$($match.Groups[1].Value)@$scope" })
            }
        }
    }
    # 处理器参数里的开关字段（如 cop-send-check 的 gateFlag）。
    # 它按处理器不同可能指向系统参数键，也可能指向模块自身列（ERROR_NO_SAVE 之类），
    # 因此单独标记为 gateFlag 域，由 Test-References 按两侧白名单判定。
    $gateRegex = [regex]'gateFlag(?:\\u0022|")\s*:\s*(?:\\u0022|")([A-Z0-9_]+)'
    foreach ($window in (Invoke-EosSqlQuery -Query (Get-WindowSql -Pattern 'gateFlag') -ConnectionString $ConnectionString)) {
        foreach ($match in $gateRegex.Matches($window)) {
            $refs.Add(@{ Scope = 'gateFlag'; Owner = 110111; Key = $match.Groups[1].Value; Source = 'gateFlag' })
        }
    }
    return $refs
}

function Get-ModuleColumns {
    <# dbo.MODULES 的列名集合：gateFlag 允许指向模块自身开关列 #>
    $sql = "SET NOCOUNT ON; SELECT CONVERT(varchar(64), c.name) FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.MODULES');"
    return @(Invoke-EosSqlQuery -Query $sql -ConnectionString $ConnectionString)
}

function Get-CodeReferences {
    <# 源码里按参数键直取的调用点：SystemParameterService.<Owner> 常量后紧跟的字符串字面量 #>
    $root = Join-Path (Split-Path -Parent $PSScriptRoot) 'EOS.API'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $ownerMap = @{ SystemOwner = 110111; AttendanceOwner = 180213; AttendanceMonthlyOwner = 180662 }
    $refs = [System.Collections.Generic.List[hashtable]]::new()
    $regex = [regex]'SystemParameterService\.(?<owner>SystemOwner|AttendanceOwner|AttendanceMonthlyOwner)\s*,\s*"(?<key>[A-Z][A-Z0-9_]*)"'
    # 参数键也可以来自同文件里"以 Keys 结尾的字符串数组常量"（键集合集中声明时）：
    # 这类间接引用同样按参数白名单校核，否则改键名会静默漏检。
    $arrayRegex = [regex]'(?s)(?:static\s+readonly|readonly\s+static|static)[^=;]*?\b(?<name>\w*Keys)\b\s*=\s*\[(?<body>[^\]]*)\]'
    $literalRegex = [regex]'"([A-Z][A-Z0-9_]*)"'
    $ownerTokenRegex = [regex]'SystemParameterService\.(SystemOwner|AttendanceOwner|AttendanceMonthlyOwner)'
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.cs -File) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in $regex.Matches($text)) {
            $refs.Add(@{
                Scope = $match.Groups['owner'].Value
                Owner = $ownerMap[$match.Groups['owner'].Value]
                Key   = $match.Groups['key'].Value
                Source = $file.Name
            })
        }
        $owners = @($ownerTokenRegex.Matches($text) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        if ($owners.Count -ne 1) { continue }
        foreach ($array in $arrayRegex.Matches($text)) {
            if ($text -notmatch [regex]::Escape($array.Groups['name'].Value) + '\s*\[') { continue }
            foreach ($literal in $literalRegex.Matches($array.Groups['body'].Value)) {
                $refs.Add(@{
                    Scope = "$($owners[0])[]"
                    Owner = $ownerMap[$owners[0]]
                    Key   = $literal.Groups[1].Value
                    Source = "$($file.Name):$($array.Groups['name'].Value)"
                })
            }
        }
    }
    return $refs
}

function Test-Definitions {
    param([string[]] $Rows)
    if ($Rows.Count -eq 0) {
        $problems.Add('FAIL 参数表 dbo.SYSSS 没有任何参数行。')
        return
    }
    $sql = @"
SET NOCOUNT ON;
SELECT N'定义缺失|' + PARAM_KEY FROM dbo.SYSSS
 WHERE VALUE_TYPE NOT IN (N'bit', N'int', N'decimal', N'string')
    OR EFFECT_SCOPE NOT IN (N'immediate', N'restart')
    OR LTRIM(RTRIM(GROUP_CODE)) = N'' OR LTRIM(RTRIM(GROUP_LABEL)) = N''
    OR LTRIM(RTRIM(DESC_TEXT)) = N'' OR OWNER_MODULE IS NULL
UNION ALL
SELECT N'默认值非法|' + PARAM_KEY FROM dbo.SYSSS
 WHERE DEFAULT_VALUE IS NOT NULL AND (
        (VALUE_TYPE = N'bit' AND DEFAULT_VALUE NOT IN (N'0', N'1'))
     OR (VALUE_TYPE = N'int' AND TRY_CONVERT(int, DEFAULT_VALUE) IS NULL)
     OR (VALUE_TYPE = N'decimal' AND TRY_CONVERT(decimal(38, 10), DEFAULT_VALUE) IS NULL))
UNION ALL
SELECT N'组内序号重复|' + CONVERT(nvarchar(32), GROUP_CODE) + N'.' + CONVERT(nvarchar(10), SEQ_NO)
  FROM dbo.SYSSS GROUP BY OWNER_MODULE, GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1
UNION ALL
SELECT N'归属模块未登记|' + CONVERT(nvarchar(10), OWNER_MODULE)
  FROM dbo.SYSSS WHERE OWNER_MODULE NOT IN (110111, 180213, 180662) GROUP BY OWNER_MODULE
UNION ALL
SELECT N'分组顺序未登记|' + CONVERT(nvarchar(32), GROUP_CODE)
  FROM dbo.SYSSS WHERE GROUP_SEQ = 0 OR GROUP_SEQ IS NULL GROUP BY GROUP_CODE
UNION ALL
SELECT N'分组顺序重复|' + CONVERT(nvarchar(10), OWNER_MODULE) + N'.' + CONVERT(nvarchar(10), GROUP_SEQ)
  FROM (SELECT DISTINCT OWNER_MODULE, GROUP_CODE, GROUP_SEQ FROM dbo.SYSSS) g
  GROUP BY OWNER_MODULE, GROUP_SEQ HAVING COUNT(*) > 1;
"@
    foreach ($row in (Invoke-EosSqlQuery -Query $sql -ConnectionString $ConnectionString)) {
        $problems.Add("FAIL $row")
    }
}

function Test-References {
    param($ParameterRows, $References, [string[]] $ModuleColumns)
    $known = @{}
    foreach ($row in $ParameterRows) {
        $parts = $row -split '\|'
        $known["$($parts[0])|$($parts[1])"] = $true
    }
    $checked = 0
    foreach ($reference in $References) {
        $checked++
        $id = "$($reference.Owner)|$($reference.Key)"
        if ($known.ContainsKey($id)) { $reference['Resolved'] = 'param'; continue }
        # gateFlag 是处理器参数：指向模块自身开关列时也算已登记（清单里标注成 gateFlag(module)）
        if ($reference.Scope -eq 'gateFlag' -and ($ModuleColumns -contains $reference.Key)) { $reference['Resolved'] = 'module'; continue }
        $problems.Add("FAIL 引用键不存在：$($reference.Scope).$($reference.Key)（来源：$($reference.Source)）")
    }
    return $checked
}

function Get-CodeReferencedRegistry {
    <#
      源码直读参数键的登记表（SystemParameterService.CodeReferencedKeys），按"归属模块|键"返回。
      设置页靠它区分"这个参数真的被读"与"暂时没有读取方"，所以要断言：源码里扫到的每个直读键
      都已登记在对应归属下——否则页面会把活参数标成"无引用方"。
    #>
    param([string] $Root)
    $file = Join-Path $Root 'EOS.API/Data/SystemParameterService.cs'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $text = Get-Content -LiteralPath $file -Raw
    $match = [regex]::Match($text, '(?s)CodeReferencedKeys\s*=\s*new[^;{]*\{(?<body>.*?)\};')
    if (-not $match.Success) { return $null }
    return @([regex]::Matches($match.Groups['body'].Value, '"(\d{6})\|([A-Z][A-Z0-9_]*)"') |
        ForEach-Object { "$($_.Groups[1].Value)|$($_.Groups[2].Value)" } | Sort-Object -Unique)
}

# ---- 主流程 ----
try {
    $parameterRows = @(Get-ParameterRows)
}
catch {
    Write-Output "FAIL 无法读取参数表（迁移是否已落库？）：$($_.Exception.Message)"
    exit 2
}

Test-Definitions -Rows $parameterRows

$scopeMap = @{ SYSSS = 110111; HR_SETUP = 180213; HRM_SETUP = 180662 }
$configReferences = @(Get-ConfigReferences -Scopes $scopeMap)
$codeReferences = @(Get-CodeReferences)
$references = @(
    @($configReferences) + @($codeReferences)
    | Sort-Object -Property { "$($_.Scope)|$($_.Owner)|$($_.Key)|$($_.Source)" } -Unique
)
$moduleColumns = @(Get-ModuleColumns)
$referenceCount = Test-References -ParameterRows $parameterRows -References $references -ModuleColumns $moduleColumns

if ($SelfTest) {
    Write-Output "  —— 引用清单（配置 $($configReferences.Count) 处 / 源码 $($codeReferences.Count) 处，去重后 $($references.Count) 处）——"
    $references | ForEach-Object {
        # 解析到哪一侧就标哪一侧：gateFlag 可能指向系统参数键，也可能指向模块自身开关列
        $side = if ($_.Resolved -eq 'module') { '(module)' } elseif ($_.Scope -eq 'gateFlag') { '(param)' } else { '' }
        Write-Output "    $($_.Scope)$side.$($_.Key)  <- $($_.Source)"
    }
}

# 源码直读键必须全部登记进 SystemParameterService.CodeReferencedKeys
$registry = Get-CodeReferencedRegistry -Root (Split-Path -Parent $PSScriptRoot)
if ($null -eq $registry) {
    $problems.Add('FAIL 无法从 SystemParameterService.cs 解析 CodeReferencedKeys（页面靠它标注"无引用方"）。')
}
else {
    foreach ($reference in $codeReferences) {
        if ($registry -notcontains "$($reference.Owner)|$($reference.Key)") {
            $problems.Add("FAIL 源码直读的参数键未按归属登记为 CodeReferencedKeys：$($reference.Owner)|$($reference.Key)（$($reference.Source)）")
        }
    }
}

if ($SelfTest) {
    $fake = @(@{ Scope = 'SYSSS'; Owner = 110111; Key = 'ZZ_NOT_A_PARAMETER'; Source = '-SelfTest' })
    $before = $problems.Count
    Test-References -ParameterRows $parameterRows -References $fake -ModuleColumns $moduleColumns | Out-Null
    if ($problems.Count -eq $before) {
        Write-Output 'FAIL 自检：伪造的不存在引用键没有被判出。'
        exit 1
    }
    $problems.RemoveAt($problems.Count - 1)
    Write-Output 'PASS 自检：伪造的不存在引用键被正确判出。'
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output $_ }
    Write-Output "FAIL 系统参数门禁：$($problems.Count) 项问题。"
    exit 1
}

Write-Output "PASS 系统参数门禁：参数 $($parameterRows.Count) 行，检查引用 $referenceCount 处，定义与引用一致。"
exit 0
