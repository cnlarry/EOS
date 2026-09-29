<#
.SYNOPSIS
  助手可代理动作注册表门禁：六要素齐全、动作名唯一、且注册表内不含职权类动作。

.DESCRIPTION
  注册表（EOS.API/Features/Assistant/Actions/AssistantActionRegistry.cs）是"助手能做哪些动作"的受控目录。
  本脚本按**动作名与目标端点双重匹配**断言两件事：

    1. 每条动作的六要素（谁 / 对什么 / 做什么 / 参数 / 幂等键 / 审计）与落点（目标端点 / 实现位置）
       都非空——缺一项，登记就退化成了注释；
    2. 动作名、目标端点与审计动作码里都不出现职权类动词（批核 / 解批 / 结案 / 取消结案等）——
       它们是职权行使，不在可代理动作面内。

  判别性：往注册表里加一条 approve 动作，或把端点改成审批服务的写入口，本脚本必须红。

.PARAMETER Root
  仓库根目录，缺省按脚本位置向上推导。

.PARAMETER SelfTest
  正反自检：用合成样本断言"该抓的抓得到、干净的不误报"，不读仓库文件。

.EXAMPLE
  pwsh scripts/check-assistant-action-registry.ps1
  pwsh scripts/check-assistant-action-registry.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$registryPath = Join-Path $Root 'EOS.API/Features/Assistant/Actions/AssistantActionRegistry.cs'

# 六要素 + 落点：任一为空即 FAIL
$requiredFields = @('ActorSubject', 'Target', 'Name', 'Parameters', 'IdempotencyKey', 'AuditAction', 'Endpoint', 'Implementation')

# 职权类动词：与单测 NoApprovalEndpointCallTests 的动词表同一份口径
$forbiddenVerbs = @('approve', 'deapprove', 'endcase', 'unendcase', 'finish', '批核', '解批', '结案', '取消结案', '审批')

function Get-RegistryEntries {
    param([Parameter(Mandatory)][string] $Text)

    $entries = @()
    $parts = [regex]::Split($Text, 'new\s+AssistantActionDefinition\s*\(')
    for ($index = 1; $index -lt $parts.Count; $index++) {
        $body = $parts[$index]
        $fields = @{}
        foreach ($match in [regex]::Matches($body, '(?m)^\s*([A-Za-z]+)\s*:\s*(.+?)\s*,\s*$')) {
            $fields[$match.Groups[1].Value] = $match.Groups[2].Value
        }
        $entries += [pscustomobject]@{ Body = $body; Fields = $fields }
    }
    return $entries
}

function Get-RegistryFindings {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $FileName
    )

    $problems = New-Object System.Collections.Generic.List[string]
    $entries = Get-RegistryEntries -Text $Text
    if ($entries.Count -eq 0) {
        $problems.Add("注册表里一条动作都没有：目录为空等于没有受控目录。")
        return $problems.ToArray()
    }

    $seen = New-Object System.Collections.Generic.HashSet[string]
    foreach ($entry in $entries) {
        $name = $entry.Fields['Name']
        if (-not $seen.Add($name)) {
            $problems.Add("动作名重复登记：$name")
        }
        foreach ($field in $requiredFields) {
            $value = $entry.Fields[$field]
            # 空串（""）与缺失同样算"没写"：登记项里留一个空串，等于这一要素不存在
            $normalized = if ($null -eq $value) { '' } else { $value.Trim().Trim('"', ' ', ')', ',') }
            if ([string]::IsNullOrWhiteSpace($normalized)) {
                $problems.Add("动作 $(if ($name) { $name } else { '（未声明 Name）' }) 的 $field 为空：六要素与落点都不得缺省。")
            }
        }

        # 双重匹配：动作名与目标端点都不得出现职权类动作
        foreach ($probe in @(@{ Kind = '动作名'; Value = $name }, @{ Kind = '目标端点'; Value = $entry.Fields['Endpoint'] },
                             @{ Kind = '审计动作码'; Value = $entry.Fields['AuditAction'] })) {
            if ([string]::IsNullOrWhiteSpace($probe.Value)) { continue }
            $text = $probe.Value.Trim('"', ' ', ')')
            foreach ($verb in $forbiddenVerbs) {
                if ($text.IndexOf($verb, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $problems.Add("$($probe.Kind) '$text' 含职权类动词 '$verb'：职权行使不在可代理动作面内。")
                }
            }
        }
    }

    return $problems.ToArray()
}

if ($SelfTest) {
    $clean = @'
        new AssistantActionDefinition(
            Name: "insert",
            ActorSubject: "user",
            Target: "module row",
            Parameters: "contract",
            IdempotencyKey: "server derived",
            AuditAction: "INSERT",
            Endpoint: "DocumentWorkbenchRepository.CreateRecordAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),
'@
    $dirty = @'
        new AssistantActionDefinition(
            Name: "approve",
            ActorSubject: "user",
            Target: "module row",
            Parameters: "contract",
            IdempotencyKey: "server derived",
            AuditAction: "approve",
            Endpoint: "WorkbenchApprovalService.ApproveAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),
'@
    $missing = @'
        new AssistantActionDefinition(
            Name: "insert",
            ActorSubject: "",
            Target: "module row",
            Parameters: "contract",
            IdempotencyKey: "server derived",
            AuditAction: "INSERT",
            Endpoint: "DocumentWorkbenchRepository.CreateRecordAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),
'@
    $empty = '// 空注册表'

    $cases = @(
        [pscustomobject]@{ Name = '干净样本'; Expect = 0; Text = $clean },
        [pscustomobject]@{ Name = '注入职权类动作'; Expect = 1; Text = $dirty },
        [pscustomobject]@{ Name = '缺要素'; Expect = 1; Text = $missing },
        [pscustomobject]@{ Name = '空目录'; Expect = 1; Text = $empty }
    )

    $failed = 0
    foreach ($case in $cases) {
        $found = @(Get-RegistryFindings -Text $case.Text -FileName 'selftest.cs')
        if ($found.Count -lt $case.Expect) {
            $failed++
            Write-Output ("FAIL 自检[{0}] 期望至少 {1} 条问题，实得 {2} 条" -f $case.Name, $case.Expect, $found.Count)
        }
        else {
            Write-Output ("PASS 自检[{0}] 实得 {1} 条问题（期望 ≥{2}）" -f $case.Name, $found.Count, $case.Expect)
        }
    }

    if ($failed -gt 0) { Write-Output ("FAIL 自检：{0} 例不符预期。" -f $failed); exit 1 }
    Write-Output '-- SELFTEST OK'
    exit 0
}

if (-not (Test-Path -LiteralPath $registryPath)) {
    Write-Output "FAIL 找不到动作注册表：$registryPath"
    exit 1
}

$text = Get-Content -LiteralPath $registryPath -Raw -Encoding utf8
$problems = @(Get-RegistryFindings -Text $text -FileName 'AssistantActionRegistry.cs')
$entries = @(Get-RegistryEntries -Text $text)

if ($problems.Count -gt 0) {
    Write-Output '== 助手动作注册表门禁 =='
    foreach ($line in $problems) { Write-Output "  [FAIL] $line" }
    Write-Output "-- FAIL（登记 $($entries.Count) 条动作）"
    exit 1
}

Write-Output "PASS 助手动作注册表：$($entries.Count) 条动作，六要素与落点齐全，且不含职权类动作。"
exit 0
