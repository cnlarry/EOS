# 技能 ↔ 引用资产

`scripts/check-agent-skills.ps1` 的输入。技能正文里引用到的仓库内路径**默认必须存在**；
本表只登记**技能清单**与**例外**，不逐条抄写正常引用——避免本表自身变成第二处真源。

抽取口径：形如 `目录/...` 且**带文件扩展名**或**以 `/` 结尾**的路径算候选；不带扩展名也不带尾斜杠的
片段（如 `docs/decisions/ADR-`）视为行文片段，不参与校验。

## 一、技能清单

| 技能 | 来源 | 状态 |
|---|---|---|
| `eos-verify` | 自建 | 在用 |
| `eos-commit` | 自建 | 在用 |
| `eos-db-objects` | 自建 | 在用 |
| `eos-security-check` | 自建 | 在用 |
| `eos-release` | 自建 | **在用（唯一发布技能）**——判定版本号 → CHANGELOG 定稿 → 打标签 → 推送 → 建 GitHub Release；取代已删除的 `eos-publish` |
| `tdd` | [mattpocock/skills](https://github.com/mattpocock/skills) @ `6654f6b` | 在用（第三方，MIT） |
| `diagnosing-bugs` | 同上 | 在用（第三方） |
| `code-review` | 同上 | 在用（第三方） |
| `grill-me` | 同上 | 在用（第三方） |
| `grilling` | 同上 | 在用（第三方） |
| `handoff` | 同上 | 在用（第三方） |
| ~~`erp-ui`~~ | 自建 | **已下线（2026-09-29）**——技能几乎未被使用，其唯一事实源 `ui-reference/` 已删除；正文见 git 历史 |

## 二、引用资产例外

| 资产 | 口径 | 说明 |
|---|---|---|
| `docs/status.md` | 本机 | 现状事实源；`docs/` 未整体纳入版本控制，别人克隆里没有 |
| `AGENTS.local.md` | 本机 | 维护者本机指引（含发布相关的多 Agent 并行规范）；由 `.git/info/exclude` 排除，不随仓库分发 |
| `scripts/publish-release.ps1` | 相对 | 实际位于 `.agents/skills/eos-release/scripts/`，正文与 README 按技能目录相对书写（文件名带连字符，抽取正则会把长名截断，故登记口径） |
| `.agents/reservations/` | 本机 | 共享文件编辑锁目录（运行时协调状态，被 `.git/info/exclude` 排除，不随仓库分发） |
| `docs/plans/` | 本机 | 内部计划目录，不随仓库分发 |
| `docs/migrations/update.sql` | 本机 | 已冻结的库升级史，不随仓库分发 |
| `logs/goal/regression/` | 本机 | 回归报告落盘位置，运行时产物 |
| `docs/oss-release/README.md` | 已退役 | 随双仓发布机制一并删除 |
| `scripts/publish-oss.ps1` | 已退役 | 同上（删除提交 `428630d`） |
| `scripts/export-oss-seed.ps1` | 已退役 | 同上 |
| `docs/agents/issue-tracker.md` | 外部 | 第三方技能 `code-review` 的 setup 占位，须运行其自身 setup 才有 |
| `scripts/hitl-loop.template.sh` | 相对 | 实际位于 `.agents/skills/diagnosing-bugs/scripts/`，正文按技能目录相对书写 |
| `agents/openai.yaml` | 相对 | 技能子目录内的工具专属扩展，正文按技能目录相对书写 |
| `../SKILL.md` | 相对 | 技能内 `references/` 正文回指同技能 `SKILL.md` 的写法，不指向仓库根 |
| `ui-reference` | 已退役 | 参考系统截图/资料目录，已于 2026-09-29 删除；技能 README 只在"已删除"的陈述里提到它 |

**口径含义**：

- **（无登记）**：默认口径——随仓库分发，**必须存在**，缺失即 FAIL；
- **本机**：不随仓库分发。本机缺失只 WARN；存在则照常参与陈旧检测；
- **已退役**：**断言其确实不存在**——若哪天又出现，说明退役被回退，报 FAIL 让人确认；
- **外部 / 相对**：不参与存在性校验。

## 三、忽略的顶层片段

门禁抽取**任意**形如 `片段/…` 的路径，再按顶层片段判定。下列片段不是仓库资产（命令、协议、工具名等），
不参与校验——**不在此表、又不存在的顶层片段会被报出**（这正是防「删了目录但技能还在引用」的那道关）。

| 片段 | 说明 |
|---|---|
| `git` | 命令行工具 |
| `origin` | git 远端名（如 `origin/main`），不是仓库路径 |
| `bin` | 构建产物目录（如项目内的 `bin/Debug/net10.0/`），不随仓库分发 |
| `gh` | GitHub CLI |
| `npm` | 前端包管理器 |
| `pnpm` | 前端包管理器 |
| `yarn` | 前端包管理器 |
| `node` | 运行时 |
| `vite` | 构建工具 |
| `vitest` | 测试运行器 |
| `dotnet` | .NET CLI |
| `pwsh` | PowerShell |
| `sqlcmd` | SQL Server 命令行 |
| `http` | 协议 |
| `https` | 协议 |
| `mailto` | 协议 |
| `machine` | 第三方技能正文里的占位域名（如 `machine/skills`） |
| `images` | 第三方技能正文里的泛化示例路径 |
| `assets` | 同上 |
| `src` | 同上 |
| `.cursor` | IDE 专属目录，不随仓库分发 |
| `.opencode` | 工具专属目录，不随仓库分发 |
| `.claude` | 工具专属目录，不随仓库分发 |
| `.codex` | 工具专属目录，不随仓库分发 |
| `.grok` | 工具专属目录，不随仓库分发 |

> 顶层片段必须是 ASCII（`^[A-Za-z._]`）才参与判定；含中文的片段一律视为行文，不校验。

## 四、维护

- 新增技能、或技能开始引用新资产 → 更新「技能清单」；属本机/已退役/外部/相对的，登记进「引用资产例外」；
- **漏登记会被报出**：引用了未登记的例外类资产（本机缺失、或已退役却存在）即 FAIL；
- 报出「未识别的顶层片段」时，二选一：它是外部工具/命令 → 补进本节；它是仓库资产 → 修引用或按第二节登记口径；
- 改动技能或其所引用的资产后，跑一次 `pwsh scripts/check-agent-skills.ps1`；
- **陈旧（STALE）只提示、不阻断**：它是 git 时间戳代理，资产一改就命中（改 `check-agent-skills.ps1`
  自己也会让本目录 README 命中）。报出来是让人**复核**，不是让人空改文档。
