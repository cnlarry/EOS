# legacy-groundtruth/sprocs —— 旧行为基准过程（开发期资产）

## 这是什么

22 个旧系统的 `*_After_Save` 存储过程定义。`EOS.API.Tests/CompareAfterSave.ps1` 用它们做**旧行为特征化**：
在事务内执行旧过程、取其结果与统一管线对照——是**真实证据**，不是假断言。

`scripts/restore-legacy-groundtruth-sprocs.ps1` 读本目录，把这 22 个过程建回库里，让那套对照用例保持可跑。

## 从哪来

从 git 归档 tag **`archive/legacy-ssdt-full-snapshot`**（= 提交 `23068a4e`，2026-08-23 的 SSDT 全量 schema 快照，
438 个存储过程）里的 `EOS.Database/dbo/Stored Procedures/<名>.sql` **逐字节取出**（保留原文件的 UTF-8 BOM——
`Invoke-EosSqlFile` 以 `sqlcmd -f 65001` 执行，与原路径下的用法一致）。

原 `EOS.Database/` 项目已于 2026-09-27 整体退役（理由见 `AGENTS.md`「工程组成」）。退役时实测：该项目工作区里
**只剩 55 个 `P_WF_*`/`P_WFB_*`/`P_HRM_*`，一个 `*_After_Save` 都没有**——也就是说，自 2026-09-20 那次
"刷新 SSDT 快照至当前库结构"起，`restore-legacy-groundtruth-sprocs.ps1` 的取源就已经空了；这 22 个定义
只存在于那份旧全量快照里。故把它们落到本目录，脚本改为读这里，**不再依赖 git**。

## 终局

这些过程与整个目录都是**开发期资产**，不是产品代码。既定终局（`docs/plans/ADR-012两级覆盖率报告.md` §八 批 3/4）
是把 `CompareAfterSave.ps1` 的对照改成"**冻结期望**"，之后本目录与还原脚本一并删除。

## 边界

- 只供测试基准使用；`MODULES.AFTERSAVE_SP` 等旧钩子字段**不**因此恢复，运行期不会走遗留链。
- 新增/修改本目录的文件要说明来源与依据；不要顺手把别的旧对象搬进来。
