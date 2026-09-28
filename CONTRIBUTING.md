# 贡献指南

感谢你参与 EOS 的开发。请先阅读本文件与 [QUICKSTART.md](QUICKSTART.md)，
并遵守以下约定。

## 提交前

- 确保 `EOS.Web` 通过 `npm run lint` 与 `npm run build`；
- 确保 `EOS.API` 通过 `dotnet build`，新增行为尽量带测试；
- 涉及数据库结构的改动必须走 DbUp 迁移：
  在 `EOS.API/Data/Migrations/` 新增 `NNN_描述.sql`（`NNN` 取现有最大编号 + 1），
  对象命名全大写，并在本地启动时确认迁移自动执行成功；
- 改动到接口、路由、错误码、配置项、权限键、公共组件 props 或数据库对象结构时，
  同步更新 `docs/guide/` 下对应篇目（篇目与源码的对应关系见该目录 `_map.md`），
  并运行 `pwsh docs/guide/_tools/check-freshness.ps1` 确认没有遗留的落后篇目。

## 代码约定

- 数据库对象一律全大写命名；代码内引用保持一致；
- 动态表名、字段名、排序字段、查询字段一律来自服务端白名单或元数据，
  不得拼接用户输入；
- 动态查询使用结构化条件 + 参数化 SQL；
- 金额、数量、日期精度以服务端为准；ID 在前端一律按字符串处理；
- 仓库代码读取元数据使用 `sys.*` 目录视图，不使用 `INFORMATION_SCHEMA`；
- 注释描述「为什么这样写」，不记录任务编号或开发过程；
- 不提交任何连接串口令、Cookie 密钥或其它凭据；
- 新增第三方依赖时，同步在 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) 登记组件、版本与许可；
  非 MIT / Apache-2.0 的许可要先确认约束可接受（如 QuestPDF 的 Community License 有营收门槛）。

## 数据库元数据的修改

界面形态由元数据驱动。若你提交的改动依赖库内元数据（模块、字段、列配置）的变化，
请在迁移脚本中一并提供 INSERT/UPDATE（幂等写法：先判断存在性再写），
让其他开发者仅靠 bootstrap + 迁移就能得到一致环境。

## 发布节奏

本仓库的发布由维护者批量同步，外部 PR 通常在下一个发布快照中落地。
对于改动较大的 PR，请先开 issue 讨论设计，避免返工。

## 提交信息

- 使用祈使句概括改动，例如 `Add column persistence to workbench`；
- 一个 PR 只做一件事，保持 diff 聚焦。
