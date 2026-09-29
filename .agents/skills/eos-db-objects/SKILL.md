---
name: eos-db-objects
description: EOS 数据库对象与迁移规范。新增库对象、编写元数据查询、或涉及 SQL Server EOS.ERP 库对象时按此执行。Use when the user wants to create tables/columns/indexes, write a DbUp migration, query metadata, or touch SQL schema.
---

EOS 唯一业务库为 `EOS.ERP`。新库对象一律经 DbUp 版本化迁移落地，元数据查询一律走 `sys.*` 目录视图。权威规则以 `AGENTS.md`「数据库对象命名规范」「元数据查询规范」为准，本文是可执行流程。

## 1. 数据库对象命名（强制）

- **所有数据库对象一律全大写**：表名、列名（字段名）、视图、存储过程、函数、索引、约束名，以及 SQL 中的表别名（如 `PRODUCT_J`、`PRODUCT_M`）均大写。
- 动态 SQL、元数据查询、白名单校验中的标识符必须与库内对象名称一致（全大写或大小写不敏感匹配）；禁止用驼峰/小写新建对象。
- `PRODUCT_J` 是 `PRODUCT` 的 SQL 别名，不是物理表。
- `EOS.IM` / `EOS.Mail` 属历史遗留独立库，不处理、不登记、不理会。

## 2. 新库对象落地路径（DbUp 版本化迁移）

- 唯一业务库 `EOS.ERP`：新表/列/索引/约束一律经 `EOS.API/Data/Migrations/` 版本化迁移落地（`ErpDatabaseInitializer` 启动执行，journal `ERP_SCHEMA_JOURNAL`），对象名全大写。
- `docs/migrations/update.sql` **已冻结（2026-08-21）**：不再追加新段落，仅作升级史保留；不要向其中写新对象。
- 禁止为功能单独建库。
- `EOS.Database/`（SSDT sqlproj）**已于 2026-09-27 退役并整体移出仓库**，现在不存在。它只能由人在 Visual Studio 里手工重导、每次刷完就已滞后于迁移，作为"参考"只会误导。**不要引用它、不要重新引入**——库结构的唯一事实源是 `EOS.API/Data/Migrations/` 下的版本化迁移。

## 3. 元数据查询规范（强制）

- 一律使用 `sys.*` 目录视图（`sys.columns` / `sys.objects` / `sys.schemas` / `sys.indexes` / `sys.index_columns`），**禁止新增 `INFORMATION_SCHEMA.*` 查询**（性能差：实测主键列查询 98ms→2ms）。
- 需同时覆盖表和视图时用 `sys.objects o ... o.type IN ('U','V')`。
- 类型名用 `TYPE_NAME(user_type_id)`；字符列长度注意 `nvarchar/nchar` 的 `max_length` 是字节数（÷2 为字符数），`max_length = -1` 为 max 类型（长度语义返回 NULL）；列顺序用 `column_id`，主键键序用 `key_ordinal`。

## 4. 安全边界（涉及 SQL 时同时自查）

- 动态表名/字段名/排序/查询字段必须来自服务端白名单/Definition，不信任前端提交，不拼接用户输入。
- 动态查询用结构化条件 + 参数化 SQL，禁止字符串拼接用户值。
- 子表查询必须依赖有效主表关联键，缺关联条件禁止无条件读取。
- 高风险旧系统表达式（`VIRTUAL_EXP`/`CONVERT_FUNCTION`/`DATASOURCE_SQL`/`DATA_FILTER`）不得未经受控解析直接进入 SQL 或业务运行时。
- 新增对象后：对象名全大写、迁移文件落 `Migrations/`、重启后经 `/health/ready` 确认迁移完成。