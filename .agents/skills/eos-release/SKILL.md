---
name: eos-release
description: EOS 发布流程（唯一发布技能）。当用户说「发布一个新版本」「发版」「打版本」「release」「出货」时按本技能执行：判定版本号 → 生成并定稿 CHANGELOG → 提交 → 打标签 → 推送 → 建 GitHub Release → 收尾核对。发布是不可逆动作（推标签、建公开 Release），任何一步不确定就先停下问人。
---

# EOS 发布一个新版本

> **本技能是 EOS 唯一的发布流程**。原 `eos-publish`（双仓快照）已退役并删除，不要再从 git 历史里
> 复活那套机制。权威规则见 [`docs/decisions/ADR-026-版本管理与发布流程.md`](../../../docs/decisions/ADR-026-版本管理与发布流程.md)，
> 可执行脚本见 [`scripts/release.ps1`](../../../scripts/release.ps1)。

## 一、先记住四条硬规则

1. **工作区必须干净**：只发布已提交的内容。
   `docs/status.md`、`docs/plans/`、`AGENTS.local.md` 是**本机文件**（被 `.git/info/exclude` 排除），
   它们不污染工作区——你仍然要更新它们（见第九节），只是它们不进这次发布提交。
2. **迁移门禁不许跳过**：`-SkipMigrationGate` 只在"确认过库不可达"时用，并在结论里写明跳过了什么。
   迁移与台账不一致时发布 = 把一个将来建不起新库的仓库标签出去。
3. **CHANGELOG 是给人的，必须人工定稿**：脚本只生成**草稿**（按提交类型分组）。你要把面向内部的条目
   删掉、把技术措辞改写成用户能懂的话。**已发布的历史版本节一个字都不要改**。
4. **版本号不复用、标签不重打**：同一个版本号只出现一次；标签一旦推送，回滚要走第七节的流程，
   不要 `git tag -f` 了当没发生。

## 二、开工前：确认"能发"（只读，不改任何东西）

```powershell
git status --porcelain                     # 必须为空
git log --oneline -1                       # 记住这个提交
git rev-list --left-right --count origin/main...HEAD   # 左=远端新提交，右=本地待推送
git tag --list                             # 现有版本标签
Get-Content version.json                   # 当前版本号
```

**若有本地待推送提交**：那要先推送（`git push origin main`）——发布脚本会拒绝在"本地领先远端"时打标签，
因为标签会指向远端不存在的提交。

**若远端有本地没有的提交**：先 `git pull --rebase`（多 Agent 并行时常见），否则你会在旧基线上发版。

然后做一次**只读预演**，它会告诉你"这次会涨到什么版本、会不会带迁移"：

```powershell
pwsh scripts/release.ps1 -DryRun -BuildOutput "$env:TEMP\eos-release-build"
```

预演输出里要确认两件事：

- **`版本：X → Y（依据：minor/patch）`** —— 判定依据是自上个标签以来的**提交类型**
  （`feat`→MINOR；`fix`/`perf`→PATCH；`refactor`/`docs`/`chore` 等不单独涨）。
  判定不合预期时不要硬改，先看是不是有提交写错了类型。
- **`本版含迁移 A–B（共 N 个）`** 或 **`本版不含迁移`** —— 这行会进 CHANGELOG 草稿与 Release 说明。

**首次发布（仓库无任何 `v*` 标签）**：脚本走"基线版"分支——不生成全历史草稿、版本取 `version.json` 现值，
由人写基线节。这种情况先读 [`references/release-checklist.md`](references/release-checklist.md) 的"首次发布"一节。

## 三、门禁：服务在跑时用 `-BuildOutput`

`EOS.API` 正在运行时，`bin/Debug/net10.0/EOS.API.exe` 被进程占用，默认 `dotnet build` 会报
`MSB3027`/`MSB3021`——**那是拷贝产物失败，不是编译失败**。用 `-BuildOutput` 把产物落到仓库外：

```powershell
pwsh scripts/release.ps1 -Bump auto -Tag -BuildOutput "$env:TEMP\eos-release-build"
```

为什么必须这么写而不是 `-SkipBuild`：`-SkipBuild` 会让"编译门禁"整条消失，真正的编译错误就拦不住了；
`-BuildOutput` 保留判别力。**不要用 `-SkipBuild` 发布**，除非你另行确认过编译通过并在结论里写明。

手册新鲜度（第 3 步）默认**非阻断**：它是"该改没改文档"的提示，文档在别的提交里落后不该拦住一次
已就绪的发布。**但这不代表可以不管**——见第六节。

## 四、CHANGELOG 定稿（人在环，不可省）

脚本给出的草稿长这样，头部两行是脚本算出来的事实：

```markdown
## [0.2.0] - 2026-10-01

> 草稿：由提交信息按类型分组生成（自 v0.1.0 起，共 12 条）。
> 数据库：本版含迁移 **294–296**（共 3 个），部署时由 EOS.API 启动自动执行。
```

定稿时：

- 保留 `## [版本] - 日期` 与「数据库」那一行（**迁移信息是硬要求**：ADR-026 §1.2 规定发布说明必须
  标注是否含迁移与编号区间）；
- 删掉「草稿」那一行与所有面向内部的条目（重构、门禁调整、文档订正）；
- 分类沿用 Keep a Changelog：`Added` / `Changed` / `Fixed` / `Removed` / `Security`；
- 破坏性变更**必须**另起 **BREAKING** 小节说明影响与迁移方式；
- 版本链接引用（`[0.2.0]: https://github.com/cnlarry/EOS/releases/tag/v0.2.0`）要补上。

## 五、提交 → 打标签 → 推送

**顺序很重要**：标签必须打在"CHANGELOG 定稿"那个提交上。

```powershell
# 1) 定稿并提交（这一步不能和脚本的 -Tag 混在一起：脚本打标签时工作区必须干净）
git add CHANGELOG.md
git commit -m "docs(release): v0.2.0 更新日志定稿"

# 2) 推送代码，再推送标签
git push origin main
git push origin v0.2.0
```

> **关于 `-Tag` 的时机**：脚本的 `-Tag` 会在**门禁通过后**打标签，但那时 CHANGELOG 还是草稿、
> 工作区还脏（脚本的产物目录 `artifacts/` 已 gitignore，不算脏）。因此**推荐两段式**：
> 先用 `-Tag` 让脚本走完门禁并把草稿落进 `CHANGELOG.md`，再按本节定稿提交，最后 `git tag -a v<版本> -m "EOS v<版本>"`。
> 首次发布时脚本的 `-Tag` 分支本身就把版本、节、标签一并处理了，按它提示走即可。

网络抖动时推送会失败（本机走代理）。用发布助手重试，它只在第一次成功后继续：

```powershell
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0            # 推送 main + 标签
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0 -CreateRelease
```

## 六、手册同步（第 3 步提示了"落后 N 篇"时）

落后 = 某篇 `docs/guide/*.md` 的最后提交**早于**它映射的源码的最后提交（映射见 `docs/guide/_map.md`）。

- **按本次发布涉及的改动范围**判断要不要补：改了接口/路由/错误码/配置项/权限键/公共组件 props/库对象结构
  ⇒ 对应篇目必须补，并**并入这次发布提交**（篇目与源码同一次提交是硬规则）；
- 与本次改动无关的历史落后项：**不要在发布里顺手改**（会混进版本节，也会踩到别的 Agent 的段落）；
  在结论里列出"已知落后篇目"让人另行处理；
- 收尾时 `check-freshness -Strict` 应回到「落后 0」才说明你这次该改的都改了。

## 七、建 GitHub Release（公开动作，最后做）

```powershell
pwsh <.agents/skills/eos-release/scripts/publish-release.ps1> -Version 0.2.0 -CreateRelease
```

助手会：用 `CHANGELOG.md` 里该版本的节作为 Release 说明、把 `artifacts/release-<版本>/MANIFEST.txt`
与 `SHA256SUMS.txt` 作为附件挂上去、建完再核对标签指向与附件。

**回滚（发布错了怎么办）**——按影响面从小到大：

| 情况 | 动作 |
|---|---|
| 只是 Release 说明写错 | `gh release edit v<版本> --notes-file <新说明>` |
| 标签打错提交、还没被人拉取 | `gh release delete v<版本> --yes` → `git push --delete origin v<版本>` → `git tag -d v<版本>` → 修好后重打 |
| 版本号不该涨 / 内容有严重问题 | 同上删标签，并 `git revert` 那笔 release 提交；**不要改历史**（共享分支） |

回滚后必须同步 `docs/status.md`：写清"哪一版被撤回、为什么"。

## 八、收尾核对（缺一不可）

```powershell
git tag -n1 v<版本>                       # 标签存在
gh release view v<版本> --repo cnlarry/EOS --json tagName,isDraft,assets --jq '{tag:.tagName,draft:.isDraft,assets:[.assets[].name]}'
git rev-list --left-right --count origin/main...HEAD   # 应为 0  0
git status --porcelain                    # 应为空
```

还要**核对标签指向的提交就是 CHANGELOG 定稿那笔**（并顺手确认 CHANGELOG 里写的提交号与之一致——
这个错误真实发生过一次）。

**运行中的服务仍跑旧版本**：`/health/version` 报的是 `version.json` + 构建时的 commit。发布后它一定落后，
**重启 `EOS.API` 是用户的事**（长驻服务不代劳），在结论里明确写"待重启使运行实例与 v<版本> 一致"。

## 九、登记（本机文件，不入库但必须写）

在 [`docs/status.md`](../../../docs/status.md) 顶部追加一条状态更新，写清：

- 版本号与标签、`gh release` 链接、发布所基于的提交；
- **本次门禁的实际情况**：迁移门禁结果（脚本数量/编号区间）、编译与前端检查是否真跑过、
  有没有跳过项（跳了什么、为什么）；
- 若运行了首次发布分支：说明为什么首发取当前 `version.json` 而不是涨一位；
- 下一个版本的判定依据（当前最新标签 + 后续提交类型）。

编辑 `docs/status.md` **前先取锁**：在 `.agents/reservations/` 下按「共享文件路径里 `/` 换成 `__`」建一个
锁文件（多 Agent 并行规范见 [`AGENTS.local.md`](../../../AGENTS.local.md)；本机文件，别人克隆里没有），
写完释放。

## 十、常见症状

| 症状 | 原因与处置 |
|---|---|
| `dotnet build` 报 MSB3027/MSB3021 | 运行中的 API 锁住 exe。用 `-BuildOutput "$env:TEMP\eos-release-build"`，**不要**用 `-SkipBuild` |
| 脚本说"本地领先 origin/main N 个提交" | 先 `git push origin main` 再发布（标签要指向远端存在的提交） |
| 脚本说"台账里有 N 个脚本在仓库中已不存在" | 有人改名/删过已执行迁移（新库会建不起来）。**停下修**，不要跳过门禁 |
| 脚本说"仓库有 N 个迁移尚未落库" | 让用户重启 `EOS.API` 跑 DbUp，确认落库后再发布 |
| `git push` / `gh` 报 `EOF`、`SSL connection could not be established` | 本机代理（Clash/mihomo）瞬断，重试即可；发布助手已带重试 |
| `gh: command not found`（Agent 会话内） | 装 gh 后老进程 PATH 未刷新，用绝对路径 `C:\Program Files\GitHub CLI\gh.exe` |
| 手册新鲜度报"落后 N 篇" | 见第六节：按本次范围补，不相干的列进结论 |
| `gh` 报 `error validating token: missing required scope 'read:org'` | `gh auth login` 的校验会读组织列表，**硬性要求 `read:org`**（与有没有组织无关）。令牌补上该 scope 再登录 |
| `gh auth login --with-token` 提示「GH_TOKEN environment variable is being used」 | 环境变量优先于 keyring，登录被拒。先清掉三处的 `GH_TOKEN`（`[Environment]::SetEnvironmentVariable('GH_TOKEN',$null,'User')` + `Remove-Item Env:GH_TOKEN`）并**新开终端**，再登录 |
| `gh auth status` 显示 `The token in keyring is invalid` | keyring 里留下了坏凭据（常见于在 `GH_TOKEN` 存在时执行 `--with-token`）。重新 `gh auth login --with-token` 覆盖即可，**不必**先 logout |
| 运行实例版本号没变 | 正常：要用户重启 `EOS.API` |

## 参考

- [`docs/decisions/ADR-026-版本管理与发布流程.md`](../../../docs/decisions/ADR-026-版本管理与发布流程.md)（版本真源、递增规则、发布物清单）
- [`scripts/release.ps1`](../../../scripts/release.ps1)（本技能唯一的发布脚本）
- [`references/release-checklist.md`](references/release-checklist.md)（首次发布、并行 Agent、发布前检查清单）
