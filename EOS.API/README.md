# EOS.API

`EOS.API` 是 EOS ERP 的后端接口工程：ASP.NET Core (.NET 10) + SQL Server。
系统的全部确定性业务能力——元数据解释、权限、单据状态机、报表与流程——都收敛在这里，
是数据访问与授权的唯一入口。

## 定位

- **唯一数据边界**：任何调用者（页面、脚本、未来的助手/外部集成）都不能绕过
  `EOS.API` 直接访问业务数据库；每个请求都按当前用户重新授权。
- **确定性优先**：库存、财务、审批、权限等判断是确定性的应用/领域服务，
  不由模型或动态脚本决定；模型输出永不进入 SQL 或表达式解析器。
- **元数据受控解释**：模块、字段、列配置、查询条件与表达式元数据在服务端按
  白名单受控消费，动态表名/字段名/排序字段不信任请求内容。

## 调用方

```text
EOS.Web ──HTTP──> EOS.API ──SQL──> SQL Server (EOS.ERP)
```

## 核心能力

- 账号认证（PBKDF2-SHA256 现代哈希）、会话 Cookie、登录限流/锁定、同源校验；
- 用户/员工/组织、菜单、模块操作位、字段级权限（禁看/禁改/禁新增）与数据范围过滤；
- 通用工作台（`DocumentWorkbench`）：单表/主子表定义、列表、列配置、表单布局的解析与数据查询；
- 结构化高级查询的服务端验证与参数化执行；
- 单据新增/编辑/批核/结案的校验、事务、并发控制与审计；
- 报表定义、条件模板、定时生成与订阅收件箱；
- 统一表单附件（元数据在库、文件在磁盘，权限按字段位强制）。

## 目录

- `Controllers/`：HTTP 契约与参数校验；
- `Data/`：仓库、领域服务与受控的元数据解释层；
- `Security/`：认证、会话与授权；
- `features/`：领域规则与工作流；
- `Data/Migrations/`：DbUp 版本化迁移（启动时自动执行）。

## 数据访问约定

- 业务库为 `EOS.ERP`；新增库对象一律全大写命名并走 DbUp 迁移；
- 元数据查询使用 `sys.*` 目录视图，不使用 `INFORMATION_SCHEMA`；
- 动态 SQL 只允许来自服务端白名单/元数据的标识符，值一律参数化。

## 本地开发

配置连接串（见 `appsettings.Development.example.json`）：

```powershell
Copy-Item appsettings.Development.example.json appsettings.Development.json
dotnet run
```

服务默认监听 `http://localhost:5261`；健康检查 `http://localhost:5261/health/live`。
数据库初始化见仓库根 `db/`。
