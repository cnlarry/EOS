<#
.SYNOPSIS
    连接 EOS.ERP 的 sqlcmd 参数解析与执行入口（供本仓脚本复用）。

.DESCRIPTION
    优先使用 SQL 认证：读取 User 级环境变量 MSSQL_ERP_CONN（由
    scripts/dev/set-mssql-mcp-env.ps1 维护，与 opencode/ZCode 的 mssql MCP 同一连接串）。

    为什么优先 SQL 认证：在受限/沙箱环境里，集成认证（sqlcmd -E）要走 SSPI 与 schannel，会被拒
    （"Failed to generate SSPI context" / "SSL 提供程序: 安全包中没有可用的凭证"）；
    而 SQL 认证 + 默认不加密可以直接连。未配置该变量时回落到 -E，行为与既有脚本一致。

    为什么还要有托管回退：部分受限进程里连 SQL 认证的 sqlcmd 都用不了——ODBC 驱动建连时走
    schannel，进程拿不到系统加密凭证，报"客户端不支持加密 / 安全包中没有可用的凭证"。
    这与服务端配置无关（服务重启不解决）。托管 System.Data.SqlClient 走 TDS 预登录，
    显式 Encrypt=False 时不碰 schannel，因此可用。

    回退策略：**先用一条 `SELECT 1` 探测 sqlcmd 是否能连**（每个连接串只探一次），
    能连就照旧走 sqlcmd（输出格式与历史行为完全一致），不能连才整程改走托管客户端。
    不做"执行失败再重试一遍"——那对迁移/夹具这类有副作用的脚本等于跑两遍。

.EXAMPLE
    . scripts/dev/eos-sql.ps1
    Invoke-EosSqlQuery -Query 'SELECT DB_NAME();'
    Invoke-EosSqlFile -Path 'D:\repo\EOS.API\Data\Migrations\076_x.sql'

.NOTES
    托管回退的查询结果按**制表符**分隔各列（sqlcmd -W 是按列宽空格补齐）。
    单列结果两者一致；本仓多列查询的惯例是 SELECT CONCAT(a, char(124), b) 先合成一列，
    不受此差异影响。

    查询串含多个 SELECT 时，两条路径都返回**全部结果集**的行（按结果集顺序拼接）：
    托管路径用 ExecuteReader + NextResult 抽干，不使用 SqlDataAdapter.Fill(DataTable)
    （它只取第一个结果集，后续结果集会被静默丢弃）。
#>

$script:EosSqlCmd = (Get-Command sqlcmd -ErrorAction SilentlyContinue).Source
# 连接串 → sqlcmd 可用性（每进程每个连接串只探测一次）
$script:EosSqlCmdProbe = @{}

function Get-EosSqlTarget {
    <#
      返回 @{ Server; Database; Auth; Args; AdoConnectionString }：
      Args 可直接展开给 sqlcmd，AdoConnectionString 给托管 System.Data.SqlClient。
    #>
    [CmdletBinding()]
    param([string] $ConnectionString)

    if (-not $ConnectionString) {
        $ConnectionString = [Environment]::GetEnvironmentVariable('MSSQL_ERP_CONN', 'User')
    }
    if (-not $ConnectionString) { $ConnectionString = $env:MSSQL_ERP_CONN }

    if ($ConnectionString) {
        $map = @{}
        foreach ($part in ($ConnectionString -split ';')) {
            if ($part -match '^\s*([^=]+?)\s*=\s*(.*)$') { $map[$Matches[1].ToLowerInvariant()] = $Matches[2] }
        }
        $server = $map['server']; if (-not $server) { $server = $map['data source'] }
        $database = $map['database']; if (-not $database) { $database = $map['initial catalog'] }
        $user = $map['user id']; if (-not $user) { $user = $map['uid'] }
        $password = $map['password']; if (-not $password) { $password = $map['pwd'] }
        if ($server -and $user) {
            if (-not $database) { $database = 'EOS.ERP' }
            return @{
                Server   = $server
                Database = $database
                Auth     = 'sql'
                Args     = @('-S', $server, '-d', $database, '-U', $user, '-P', $password)
                AdoConnectionString = Add-EosEncryptOff -ConnectionString $ConnectionString
            }
        }
    }

    # 回落：集成认证（与既有脚本一致；沙箱/受限环境下可能因 SSPI 被拒）
    return @{
        Server   = 'localhost'
        Database = 'EOS.ERP'
        Auth     = 'integrated'
        Args     = @('-S', 'localhost', '-d', 'EOS.ERP', '-E')
        AdoConnectionString = 'Server=localhost;Database=EOS.ERP;Integrated Security=True;Encrypt=False;TrustServerCertificate=True'
    }
}

function Add-EosEncryptOff {
    <# 托管客户端显式不加密：未显式指定 Encrypt 时补 Encrypt=False，避免走 schannel。 #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    if ($ConnectionString -match '(?i)(^|;)\s*encrypt\s*=') { return $ConnectionString }
    return ($ConnectionString.TrimEnd(';') + ';Encrypt=False;TrustServerCertificate=True')
}

function Test-EosSqlCmd {
    <#
      sqlcmd 能否连上库：用一条无副作用的 SELECT 1 探测，结果按连接串缓存。
      不能连（驱动/schannel 故障、未安装）时返回 $false，由调用方改走托管客户端。
    #>
    [CmdletBinding()]
    param([string] $ConnectionString)
    $key = if ($ConnectionString) { $ConnectionString } else { '' }
    if ($script:EosSqlCmdProbe.ContainsKey($key)) { return $script:EosSqlCmdProbe[$key] }

    $usable = $false
    $reason = 'sqlcmd 未安装'
    if ($script:EosSqlCmd) {
        $target = Get-EosSqlTarget -ConnectionString $ConnectionString
        $raw = & $script:EosSqlCmd @($target.Args) -C -l 5 -h -1 -W -Q 'SELECT 1' 2>&1
        if ($LASTEXITCODE -eq 0) { $usable = $true }
        else { $reason = (@($raw) -join ' ').Trim() }
    }
    if (-not $usable) {
        Write-Host "[eos-sql] sqlcmd unusable, falling back to managed SqlClient: $reason"
    }
    $script:EosSqlCmdProbe[$key] = $usable
    return $usable
}

function Invoke-EosManagedQuery {
    <#
      托管客户端执行查询，返回行数组（列以制表符分隔，NULL 变空串）。

      查询串里可以有多个 SELECT：**所有结果集按顺序拼接返回**，与 sqlcmd 打印多个结果集的
      顺序一致。不能用 SqlDataAdapter.Fill(DataTable)——它只填第一个结果集，后续结果集被
      静默丢弃，调用方拿到的行数少了却没有任何报错。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $Sql,
        [int] $Timeout = 300
    )
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $lines = New-Object System.Collections.Generic.List[string]
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = $Timeout
        try {
            $reader = $command.ExecuteReader()
            try {
                do {
                    while ($reader.Read()) {
                        $cells = for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                            if ($reader.IsDBNull($index)) { '' } else { [string]$reader.GetValue($index) }
                        }
                        $lines.Add(($cells -join "`t"))
                    }
                } while ($reader.NextResult())
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }
    } finally {
        $connection.Dispose()
    }
    return $lines.ToArray()
}

function Invoke-EosManagedFile {
    <#
      托管客户端执行 .sql 文件：按 GO 切批，同一连接顺序执行。
      对应 sqlcmd 语义：不使用 -b 时遇错继续（ExitCode 记错误消息数），使用 -b 时首个错误即停。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $Path,
        [switch] $NoErrorStop
    )
    $scriptText = Get-Content -Raw -LiteralPath $Path
    $batches = [regex]::Split($scriptText, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim() -ne '' }
    $output = New-Object System.Collections.Generic.List[string]
    $errorCount = 0
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    # PRINT/RAISERROR 走 InfoMessage，不带事件就看不到（sqlcmd 会照原样打印）。
    $infoHandler = [System.Data.SqlClient.SqlInfoMessageEventHandler] {
        param($sender, $eventArgs)
        for ($i = 0; $i -lt $eventArgs.Errors.Count; $i++) { $output.Add($eventArgs.Errors[$i].Message) }
    }
    $connection.add_InfoMessage($infoHandler)
    try {
        $connection.Open()
        foreach ($batch in $batches) {
            $command = $connection.CreateCommand()
            $command.CommandText = $batch
            $command.CommandTimeout = 300
            try {
                # 必须用 ExecuteReader 并抽干结果集：ExecuteNonQuery 会把脚本里的 SELECT 输出整段丢掉。
                $reader = $command.ExecuteReader()
                try {
                    do {
                        while ($reader.Read()) {
                            $cells = foreach ($column in 0..($reader.FieldCount - 1)) {
                                $value = $reader.GetValue($column)
                                if ($null -eq $value -or $value -is [System.DBNull]) { '' } else { [string]$value }
                            }
                            $output.Add(($cells -join "`t"))
                        }
                    } while ($reader.NextResult())
                } finally {
                    $reader.Dispose()
                }
            } catch {
                $errorCount++
                $output.Add($_.Exception.Message)
                if (-not $NoErrorStop) { break }
            } finally {
                $command.Dispose()
            }
        }
    } catch {
        $errorCount++
        $output.Add($_.Exception.Message)
    } finally {
        $connection.remove_InfoMessage($infoHandler)
        $connection.Dispose()
    }
    return @{ ExitCode = $errorCount; Output = @($output) }
}

function Invoke-EosSqlQuery {
    <#
      执行查询并返回非空输出行（sqlcmd -h -1 -W，按行返回）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Query,
        [string] $ConnectionString
    )
    $target = Get-EosSqlTarget -ConnectionString $ConnectionString
    # SET 选项：FIELD_RELATION 等表被索引视图/索引引用，DML 需要 QUOTED_IDENTIFIER ON。
    $sql = "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; " + $Query

    if (Test-EosSqlCmd -ConnectionString $ConnectionString) {
        $raw = & $script:EosSqlCmd @($target.Args) -C -f 65001 -h -1 -W -Q $sql 2>&1
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd 查询失败 rc=$LASTEXITCODE`n$($raw -join "`n")" }
        return @($raw -split "`r?`n" | Where-Object { $_.Trim() -ne '' -and $_.Trim() -notlike '(* rows affected)' })
    }

    return @(Invoke-EosManagedQuery -ConnectionString $target.AdoConnectionString -Sql $sql |
        Where-Object { $_.Trim() -ne '' })
}

function Invoke-EosSqlFile {
    <#
      执行 .sql 文件；-Path 必须是绝对路径（大脚本经管道传给 sqlcmd 会静默丢行）。
      返回 @{ ExitCode; Output }。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $ConnectionString,
        [switch] $NoErrorStop
    )
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw "必须使用绝对路径：$Path" }
    if (-not (Test-Path -LiteralPath $Path)) { throw "文件不存在：$Path" }

    $target = Get-EosSqlTarget -ConnectionString $ConnectionString
    if (Test-EosSqlCmd -ConnectionString $ConnectionString) {
        $args = @($target.Args) + @('-C', '-f', '65001', '-i', $Path)
        if (-not $NoErrorStop) { $args += '-b' }
        $raw = & $script:EosSqlCmd @args 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = @($raw) }
    }

    return Invoke-EosManagedFile -ConnectionString $target.AdoConnectionString -Path $Path -NoErrorStop:$NoErrorStop
}
