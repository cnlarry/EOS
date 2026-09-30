# 快速开始

目标：从空环境把 EOS 跑起来，看到完整模块菜单与空的业务数据。

## 环境要求

- Windows / Linux / macOS；
- .NET 10 SDK；
- Node.js 20+ 与 npm；
- SQL Server 2025+（知识库表使用原生 `vector` 类型；兼容级别 170）。以下按本机 Windows 身份验证示例。

## 1. 初始化数据库

```powershell
# 建库并执行 bootstrap 脚本（脚本说明见 db/README.md）
# 脚本为 UTF-8 编码，sqlcmd 需带 -f 65001 才能正确读取中文
sqlcmd -S . -E -i db/bootstrap/00_create_database.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/10_schema.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/20_metadata.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/30_admin.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/40_journal_baseline.sql -f 65001
```

如果使用 SQL 账号登录，把 `-E` 换成 `-U <用户> -P <口令>`。

## 2. 配置并启动后端

配置里只写**环境变量引用**（`appsettings.json` 中形如 `${MSSQL_ERP_CONN}` 的占位），真值放环境变量——
因此配置文件可以入库，连接串与密钥不落文件。需要设的变量：

```powershell
# 业务库连接串（必填；缺了 /health/ready 会报 Unhealthy）
[Environment]::SetEnvironmentVariable('MSSQL_ERP_CONN',
  'Server=localhost;Database=EOS.ERP;Trusted_Connection=True;TrustServerCertificate=True', 'User')

# 工作助手的模型密钥（可选；不配则助手降级并提示"尚未配置模型接入"，其余功能不受影响）
[Environment]::SetEnvironmentVariable('EOS_ASSISTANT_API_KEY', '<你的密钥>', 'User')
```

设完**重开终端**再启动（环境变量在启动进程时继承）：

```powershell
cd EOS.API
dotnet run
```

服务默认监听 `http://localhost:5261`。健康检查：`http://localhost:5261/health/live`。

首次启动时，应用会经 DbUp 检查迁移日志表 `ERP_SCHEMA_JOURNAL`。由于 `40_journal_baseline.sql`
已预登记基线内的迁移，只会执行后续新增的迁移，属正常现象。

## 3. 启动前端

```powershell
cd EOS.Web
npm install
npm run dev
```

前端开发服务器默认监听 **80** 端口（被占用时 Vite 会自动换端口，以它输出的地址为准），
并把 `/api` 代理到 `EOS.API`。打开该地址即可。

## 4. 登录

| 项目 | 值 |
| --- | --- |
| 账号 | `admin` |
| 口令 | `admin` |

登录后：
- 到「系统管理 → 用户权限」确认 `admin` 的全模块权限已就位；
- 到「用户资料」修改口令；
- 业务主档（客户、供应商、物料、员工等）为空，需要按单据流从基础资料开始建立。

## 5. 验证与测试

```powershell
# 后端
cd EOS.API
dotnet build
dotnet test ../EOS.API.Tests/EOS.API.Tests.csproj

# 前端
cd EOS.Web
npm run lint
npm run build
```

## 常见问题

- **登录提示用户不存在**：确认已执行 `30_admin.sql`，且连接串指向正确的数据库。
- **菜单不完整 / 字段缺失**：确认已执行 `20_metadata.sql`。
- **启动时迁移报错**：确认已按顺序执行全部 bootstrap 脚本；`10_schema.sql` 与
  `40_journal_baseline.sql` 的基线必须匹配（同为发布时的结构版本）。
- **日志中提示表名/列名不存在**：`10_schema.sql` 是结构快照，若你使用旧版本的初始化脚本
  后升级代码，请从干净库重新初始化，或等待新迁移补齐结构。
