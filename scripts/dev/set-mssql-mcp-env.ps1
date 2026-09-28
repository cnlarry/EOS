# EOS MCP SQL Server 环境变量维护脚本
#
# 作用：为 opencode/codex/ZCode 的 mssql MCP 服务器设置 SQL 认证连接串（User 级环境变量）。
#   opencode.jsonc 里 MCP 配置通过 {env:MSSQL_ERP_CONN} 等引用；ZCode 的 MCP 配置文件
#   不展开环境变量模板，子进程直接继承原生变量 MSSQL_ERP_CONN。
#   本脚本只写环境变量、不触碰配置文件，确保连接串（含 sa 密码）不进仓库。
#
# 用法：
#   pwsh scripts/dev/set-mssql-mcp-env.ps1 -Password '<sa 密码>'   # 设置业务库连接串
#   pwsh scripts/dev/set-mssql-mcp-env.ps1 -Password '<sa 密码>' -LegacyDbName Hiswitek  # 兼容旧库名（默认 EOS.ERP）
#
# 说明：
#   - 变量：MSSQL_ERP_CONN（opencode/codex 引用名）与 MSSQL_ERP_CONN
#     （mssql-mcp-server 原生变量名，ZCode 继承用），均为 User 级，内容相同。
#     EOS.IM / EOS.Mail 已随 ADR-007 下线，不再设置。
#   - SQL Server 认证（User Id=sa），不使用 Integrated Security（mssql-mcp-server 的
#     tedious 实现要求显式域凭据，SQL 账号走 Integrated Security 会被拒）。
#   - 改完环境变量后必须重启 opencode / ZCode，MCP 子进程才会继承新环境。

param(
    [Parameter(Mandatory = $true)]
    [string]$Password,
    [string]$LegacyDbName = "EOS.ERP"
)

$conn = "Server=localhost;Database={0};User Id=sa;Password={1};TrustServerCertificate=True;"

[Environment]::SetEnvironmentVariable("MSSQL_ERP_CONN", ($conn -f $LegacyDbName, $Password), "User")
[Environment]::SetEnvironmentVariable("MSSQL_ERP_CONN", ($conn -f $LegacyDbName, $Password), "User")

Write-Host "MSSQL_ERP_CONN  = $((($conn -f $LegacyDbName, '***') -replace $Password, '***'))"
Write-Host "已写入 User 环境变量。请重启 opencode / ZCode 使 MCP 子进程继承新环境。"
