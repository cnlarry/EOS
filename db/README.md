# 数据库初始化

开源仓库不附带业务数据库备份。要在空环境把系统跑起来，按下面的顺序用 sqlcmd
（或 SSMS 打开脚本逐一执行）初始化一个结构完整、界面齐全、无业务数据的空库。

## 1. 前提

- SQL Server 2025+（结构含原生 `vector` 列，兼容级别 170）。
- 具备创建数据库的权限（dbcreator / sysadmin），或由 DBA 代为执行。

## 2. 执行顺序

| 步骤 | 脚本 | 说明 |
| --- | --- | --- |
| 1 | `00_create_database.sql` | 创建 `EOS.ERP` 数据库 |
| 2 | `10_schema.sql` | 表、函数、视图与运行时依赖的存储过程（纯结构，无数据） |
| 3 | `20_metadata.sql` | 元数据种子：模块菜单、字段登记、通用查询、报表与表单版式 |
| 4 | `25_base_data.sql` | 基础资料种子：公司/部门、币别、税别、银行、条件、类别/颜色/单位/材质、仓库，以及示例客户与厂商 |
| 5 | `30_admin.sql` | 初始账号 `admin`（管理员）与 `larry`（普通用户），登录后请立即修改口令 |
| 6 | `40_journal_baseline.sql` | 登记已固化的迁移编号，应用启动时不再重复执行 |

示例：

```powershell
# 以当前 Windows 账户连接本机默认实例。
# 脚本均为 UTF-8 编码，sqlcmd 必须带 -f 65001 以 UTF-8 代码页读取
# （否则中文脚本在部分区域设置下会解析失败）。
sqlcmd -S . -E -i db/bootstrap/00_create_database.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/10_schema.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/20_metadata.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/25_base_data.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/30_admin.sql -f 65001
sqlcmd -S . -E -d "EOS.ERP" -i db/bootstrap/40_journal_baseline.sql -f 65001
```

## 3. 结构与数据口径

- 元数据（模块、字段、查询列、报表版式、表单布局、流程定义）随仓库发布，
  新库会用与演示环境一致的界面元数据启动。
- **基础资料随建库生成**：公司与部门、员工、币别、税别、银行、价格/付款条件、结算方式、
  帐款类型、产品类别/颜色/单位/材质、仓库/库位/库存策略，以及电子元器件行业的示例客户与厂商，
  见 `25_base_data.sql`。这些记录供其它单据引用，可整体替换为真实数据。
- 业务单据（订单、送货单、收料单等）为空，需按单据流自行建立。
- 初始账号两个：`admin`（管理员，全模块与报表权限）与 `larry`（普通用户，无配置/审批权限）。
- 基础资料的清单与 CRUD 验收见 `EOS.API.Tests/base-data-modules.json` 与
  `EOS.API.Tests/E2eBaseDataCrud.ps1`。
- 脚本可重复执行：结构脚本不防重，首次执行即可；元数据、基础资料与账号种子幂等。

## 4. 后续升级

应用启动时，`EOS.API` 通过 DbUp 把仓库中 `EOS.API/Data/Migrations` 下
**编号大于 `40_journal_baseline.sql` 所登记最大编号** 的迁移自动应用到库，
日志记录在 `dbo.ERP_SCHEMA_JOURNAL`。
日常开发不需要手工跑迁移，也不需要手工改动 `ERP_SCHEMA_JOURNAL`。
