# `scripts/` —— 仓库脚本目录

本目录放的是**可执行的工程约定**：门禁、运维工具、一次性批次脚本。

**每个脚本都在 [`docs/guide/_index/scripts.md`](../docs/guide/_index/scripts.md) 登记造册**（作用 / 类别 / 依赖 /
引用点数 / 最近改动；该表由 `script-inventory.ps1` 生成，CI 用 `-Check` 校验它与目录一致）。
本文件只写**制度与退役记录**——在册清单不在这里手抄，抄了必然两处漂移。

## 一、三个入口

| 我要做的事 | 跑什么 |
|---|---|
| 弄清"本次改动要同步哪些文档、必须同批跟改什么、跑哪些验证" | `pwsh scripts/impact.ps1`（`-Staged` 只看暂存，`-Json` 给机器读） |
| 查某个脚本是干什么的、属于哪一类、还有没有人在用 | 打开 [`docs/guide/_index/scripts.md`](../docs/guide/_index/scripts.md)，或 `pwsh scripts/script-inventory.ps1 -Json` |
| 按路径 / 能力反查入口符号（接口在哪个控制器、页面在哪个组件、归哪个权限模块） | 打开 [`docs/guide/_index/routes.md`](../docs/guide/_index/routes.md)，或 `pwsh scripts/gen-code-map.ps1 -Json` |
| 按决策反查（哪篇 ADR 管这件事、它现在是草案还是已收口、跟哪些 ADR 相关） | 打开 [`docs/guide/_index/adrs.md`](../docs/guide/_index/adrs.md)，或 `pwsh scripts/gen-adr-index.ps1 -Json` |
| 校验这三张索引是否健康（CI 跑这三条） | `pwsh scripts/script-inventory.ps1 -Check`、`pwsh scripts/gen-code-map.ps1 -Check`、`pwsh scripts/gen-adr-index.ps1 -Check` |

聚合入口另有两条：`regression.ps1`（本地全量回归：单测 + 前端 + E2E + UI 冒烟，需服务与库在线）、
`release.ps1`（发布：跑门禁 → 版本一致性 → CHANGELOG → 清单）。长驻服务的启动与停止由人操作
（`dev-services.ps1` 是给人的工具，不是给 Agent 的）。

## 二、目录约定

三类脚本，**判定靠证据**（推导规则写在 `script-inventory.ps1` 文件头）：

| 类别 | 判据 | 归宿 |
|---|---|---|
| **门禁** | 它守的约束**未来还可能被违反** | 长期保留：进 CI，或登记进 [`docs/guide/06`](../docs/guide/06-测试与质量门禁.md) 按需跑 |
| **工具** | 可重复使用的运维 / 导出 / 回归手段 | 长期保留 |
| **一次性批次** | 为某次迁移 / 收口写下，约束完成后不可能再被违反 | **用完即删**（见下） |

### 一次性脚本用完即废

- **落点**：新的一次性脚本一律放 `scripts/oneshot/`。**目录即状态**——这里随时可以整目录清空，
  不影响任何门禁；文件全文在 git 历史里，不需要留在工作区。
- **锚点**：`oneshot/` 下每个脚本头部写一行 `# 一次性｜锚点：<迁移 NNN / ADR-0xx / 模块号>`，
  说明它随哪件收口完成而失效。
- **棘轮**：脚本根目录的历史一次性脚本**只减不增**（基线写在 `script-inventory.ps1` 的
  `$LegacyOneshotBaseline`）——新的一次性脚本要么落 `oneshot/`，要么先删掉被它替代的那个。
  存量**不搬迁**：ADR 与手册引的是旧路径，搬动只会造出一批死链。
- **判据**：这条断言**未来还可能被违反吗**？会 ⇒ 它是**门禁**（登记进 06 篇、按需跑），不是一次性脚本。
- **两条例外**：① **事故回退 / 取证手段**（`rollback-` / `restore-` / `clean-` 一类）保留，
  并在台账写明它是回退手段——删了真出事时手里没有工具；② **低频巡检**（约束还活着、只是没人排班）
  按门禁对待，登记进 06 篇，而不是当一次性脚本删掉。
- **刻意不做"到期自动红"**：机器判不出"用完了没有"，把不确定的判定做成门禁只会得到一个假门禁
  （与 `check-freshness` 不阻断同一个理由）。台账列出待清理项，由人处置。

### `-Check` 断言什么

| 断言 | 不成立时 |
|---|---|
| 每个脚本都有可提取的说明（`.SYNOPSIS`，或文件头块注释，或头 `#` 注释） | FAIL |
| 每个脚本被引用过（CI / 手册 / 技能 / 其它脚本），或登记进独立工具白名单 | FAIL |
| `oneshot/` 下的一次性脚本都写了锚点；脚本根目录的一次性脚本数不超基线 | FAIL |
| 台账与脚本目录一致 | FAIL |
| 本节第四节列的已退役脚本**确实不存在**，且不在任何白名单里 | FAIL |
| 说明不是 `.SYNOPSIS`、门禁类未在 CI / 06 篇登记、白名单条目已可移除 | 只提示 |

## 三、受影响时怎么改

- 改脚本内容：跑它自己的 `-SelfTest`（多数门禁自带正反自检）；改门禁口径要同步
  [`docs/guide/06`](../docs/guide/06-测试与质量门禁.md) 的表格。
- 新增 / 改名 / 删除脚本：重跑 `pwsh scripts/script-inventory.ps1` 并提交生成的台账。
- 退役脚本：删文件 → 在下面第四节补一行（写明它守什么、为什么可以退役、曾被谁引用）→
  重跑台账。**ADR 与已应用的迁移不追改**（它们是历史记录），第四节是"那个脚本哪儿去了"的唯一应答处。

## 四、已退役脚本

下列脚本已从工作区删除，正文在 git 历史里（`git log -- scripts/<名>`，或
`git show <祖先提交>:scripts/<名>`）。它们曾被 ADR、已应用的迁移与产品代码注释引用为
"守某条不变式的门禁"——那些引用是**历史事实**，不追改；但它们共同的出处是 ADR-012 / 014 / 016 / 018 /
020 / 021 的正文，按名检索即可定位。**不变式本身没有随之消失**，"现在谁管它"一列写清它还剩什么覆盖。

| 已退役脚本 | 它守的不变式 | 现在谁管它 / 为什么可以退役 | 曾被引用于 |
|---|---|---|---|
| `check-capability-ledger.ps1` | 库存策略能力台帐：档位声明"已实现"，就必须指出它的运行期消费方 | **S1（能力档位）已迁成离线用例**：`EOS.API.Tests/DepotStockPolicyCapabilityLedgerTests.cs`（解析 `Tiers` 声明 → 核对台帐分类 → 跑运行期消费方探针，进 CI）；原门禁的 **S2（库存域库对象两侧探针）与 S3（配置面库存写入旁路棘轮）未迁**，判据见 `git show <祖先提交>:scripts/check-capability-ledger.ps1` | 迁移 257 的注释 |
| `check-domain-rule-registry.ps1` | 保存后行为登记：「目录承接」模块必须有启用的 SAVE 校验规则、快照不得残留已拆的领域规则族名、`reference-exists` 引用列必须是物理列 | 三条断言迁成**真库用例**：`EOS.API.Tests/SaveBehaviorRegistrationLiveTests.cs`（随本地全量跑，不进 CI） | `LESSONS.md` L36、`docs/status.md` |
| `check-e2e-hygiene.ps1` | E2E 脚本卫生：错误体解码必须齐、硬编码日期只减不增 | 判据对象 `EOS.API.Tests/E2e*.ps1` **本机不入库**，在别人的克隆里它永远空跑 | `docs/status.md` |
| `check-effect-catalog-consistency.ps1` | 库内效果配置 ↔ 代码目录 ↔ 快照三方一致（ADR-012） | 代码侧由 `EOS.API.Tests/EffectContractCatalogTests.cs` 覆盖；"ADR 文档表 ↔ 代码"这一层无等价覆盖 | `ADR-012` |
| `check-effect-descriptions.ps1` | 每个效果键与每种反向 kind 都必须有非空人话说明 | 说明层由 `EOS.API.Tests/EffectContractCatalogTests.cs` 同名断言覆盖；"库内在用键在目录内"无等价覆盖 | `LESSONS.md` L33 |
| `check-effect-module-branches.ps1` | 效果引擎运行时保持数据驱动，不得按模块硬分支 | 静态架构不变式迁成**离线用例**：`EOS.API.Tests/StaticArchitectureGuardTests.cs`（扫 `EOS.API/Data/Effects`，进 CI） | `ADR-012` |
| `check-inventory-availability.ps1` | 可用量恒等式 `USEABLE_QTY = QTY − Σ有效冻结 − Σ有效预留`（全库逐键） | 全库逐键 + 存列卫生 + "人为分叉必被抓"的判别性注入：`EOS.API.Tests/InventoryLedgerIdentityLiveTests.cs`；逐案分支另有 `InventoryAvailabilityReadPathLiveTests` / `InventoryAvailabilityServiceLiveTests` | `InventoryQueryService.cs`、`InventoryAvailabilityService.cs`、`InventoryAvailabilityReadPathLiveTests.cs`、`ADR-014`、`ADR-020` |
| `check-inventory-balance-identity.ps1` | 库存账务自证：余额逐键对流水累计恒平 | 报表档逐键恒平、流水数量 / 方向卫生、**全库档棘轮**（冻结清单外的键即失败）：`EOS.API.Tests/InventoryLedgerIdentityLiveTests.cs`——清单就是退役时那 54 个键 | `InventoryQueryService.cs`、迁移 236 的注释 |
| `check-inventory-read-hosts.ps1` | 库存读取收口：生产代码与配置面不得再出现直读库存表的宿主 | **两半都迁了**：代码侧表名白名单 → `StaticArchitectureGuardTests`（离线，进 CI）；配置侧引用白名单（含引擎维护列只读、盘点单明细判重键）、写路径交叉断言（库存主表模块不得进统一表单写名单）→ `InventoryConfigFaceLiveTests`（真库） | `InventoryQueryService.cs`、`MasterFieldWriteHandler.cs`、迁移 240 的注释 |
| `check-month-snapshot-recon.ps1` | 月结快照对账：`V_INV_MONTH_SNAPSHOT_RECON` 差异 0 行（无已批核快照时判 N/A） | 判别性证据在真库用例 `MonthSnapshotReconLiveTests`（**脚本自己写明的**）；整库巡检按需另查视图 | `InventoryQueryService.cs`、`ADR-020` |
| `check-report-resource-model.ps1` | 汇总报表的聚合列必须逐列声明字段级权限位 | 由 `ReportAggregateRegistryTests`（+ `ReportAggregateFieldPrivilegeLiveTests`）覆盖；`/reports` 迁移形态门禁无等价覆盖 | — |
| `check-stock-depot-level-consistency.ps1` | 库别级字段同键一致、策略档位取值合法、哨兵列尾随空格、库别级字段不得被聚合读取、策略求值口径唯一、半成品账三键齐全且不与主账重叠 | **全部档位都迁了**：①②③⑤配置侧⑥ → `InventoryConfigFaceLiveTests`（真库）；④ 库别级字段不得被聚合读取 → `StaticArchitectureGuardTests`（离线，进 CI）；⑤ 的代码侧同样在 `StaticArchitectureGuardTests`。逐类语义另有 `InventoryQueryServiceTests`、`DepotStockPolicy*` 三族、`HalfStockMoveHandlerTests` 等 | `ADR-014`、`ADR-020` |
| `verify-field-relation-edges.ps1` | `MODULE_BUSINESS_ACTION_OP` 的定位键必须有已登记的 `EFFECT` 边 | 保存期 lint 同口径强制（`ModuleBusinessConfigRepository.ValidatePhysicalAsync`） | `BusinessFlowRepository.cs`、`DocumentChainGuard.cs`、`docs/guide/11`、`docs/status.md` |
| `adr012-b1-verify.ps1` | ADR-012 D9 落地后的验证串（B1 批） | 依赖的门禁已退役，验证串跑不完整；收口已完成（后续由 ADR-034 / 035 收敛效果引擎开关与批核模型） | `ADR-012` |
| `adr012-b2-verify.ps1` | ADR-012 B2 重叠族（hr-contract / hr-safe / hr-certify）落地验证串 | 同上 | `ADR-012` |
| `adr012-coverage-report.ps1` | ADR-012 两级覆盖率汇报（统一管线 / 可视化配置） | 一次性的收口报告；报告产物原本就落本机计划区 | `ADR-012` |
| `adr012-failure-scenario.ps1` | 失败分支场景跑批（Status → Setup → 影子失败运行 → Teardown → Status 复核） | 一次性对拍，收口已完成 | `ADR-012` |
| `adr012-fixture-sweep.ps1` | 用夹具单据给"账本无证据"的模块补齐 A/B 证据 | 一次性跑批，收口已完成 | `ADR-012` |
| `adr012-republish-modules.ps1` | 定向重发布指定模块的 Definition 快照（让工作区校验配置进入运行时定义） | **能力重复**：同一个接口由 `publish-workbench-snapshots.ps1 -ModuleIds` 提供，且原调用方（B1/B2 验证串）已退役 | `ADR-012` |

> **同族保留两个，别当一次性脚本删掉**：`adr012-acceptance-ledger.ps1`（ADR-012 验收账本）仍被
> `republish-all-whitelist.ps1` 的验收口径与 `EOS.API.Tests/Tools/EffectShadowSweep.cs` 的
> "同分母"对照引用；`adr012-dead-sproc-report.ps1`（旧过程安全下线集扫描）是
> `restore-legacy-groundtruth-sprocs.ps1` 的权威扫描入口。删它们会连带打断在用的资产。

> 退役的判据是"**没人会跑到它**"：这 13 个既不在 CI、也没在 `docs/guide/06` 里登记，
> 等于事实上废弃——留在目录里只会让"这里有防线"变成一句无法验证的话。
> 代价如实记在"现在谁管它"一列。**已补回三批覆盖**：
>
> - **账实恒等式**（库存账实恒平、可用量恒等）→ `EOS.API.Tests/InventoryLedgerIdentityLiveTests.cs`
>   （真库；含"人为分叉必被抓"的判别性注入）；
> - **静态架构不变式**（效果引擎数据驱动、库存读宿主的代码侧表名白名单、策略求值口径唯一代码侧）
>   → `EOS.API.Tests/StaticArchitectureGuardTests.cs`（离线，**进 CI**，扫描器自带正反控制）；
> - **保存后行为登记**（目录承接模块必须有启用的 SAVE 规则 / 快照不得残留族名 /
>   `reference-exists` 引用列必须是物理列）→ `EOS.API.Tests/SaveBehaviorRegistrationLiveTests.cs`（真库）。
>
> - **库存配置面**（配置引用白名单、引擎维护列只读、盘点单明细判重键、写路径交叉断言、
>   库存表当主表 / 明细表的模块不得进写名单、库别级字段同键不分裂、策略档位合法、位置号无尾随空格、
>   半成品账三键齐全且不与主账重叠、策略表不得出现在配置面）→ `EOS.API.Tests/InventoryConfigFaceLiveTests.cs`（真库）。
>
> - **能力台帐的 S1**（"声称=实现"：档位声明已实现就必须指出运行期消费方）→
>   `EOS.API.Tests/DepotStockPolicyCapabilityLedgerTests.cs`（离线，**进 CI**，解析器自带正反控制）。
>
> 至此这 13 条**全部有了等价覆盖**；唯一未迁的是能力台帐原门禁的 **S2（库存域库对象两侧探针）与
> S3（配置面库存写入旁路棘轮）**那两段（探针要连库），需要时按
> `git show <祖先提交>:scripts/check-capability-ledger.ps1` 取回。
