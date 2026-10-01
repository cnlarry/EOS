# 发布清单与三种特殊情形

> 主流程见 [`SKILL.md`](../SKILL.md)（同技能目录的正文）。本文只装**首次发布**、**并行 Agent**、
> **发布前检查清单**与**判定性验收**这些不适合塞进主流程的细节。

## 一、发布前检查清单（逐条打勾再动手）

- [ ] `git status --porcelain` 为空；没有把别的 Agent 的未提交改动混进来
- [ ] `git rev-list --left-right --count origin/main...HEAD` 右列为 0（或已先推送）
- [ ] 远端没有本地缺失的提交（有则先 `git pull --rebase`）
- [ ] `pwsh scripts/release.ps1 -DryRun -BuildOutput <仓库外目录>` 通过，且**版本判定符合预期**
- [ ] 记住预演里的「含迁移 A–B（共 N 个）」——稍后要与 CHANGELOG 核对
- [ ] 编译门禁**真跑过**（`-BuildOutput`），不是 `-SkipBuild`
- [ ] 前端 `npm run lint` + `npm run build` 通过
- [ ] 手册新鲜度：本次范围内的篇目已补（`check-freshness -Strict` 落后 0 或已列出不相干项）
- [ ] CHANGELOG 草稿已定稿：删内部条目、补版本链接、破坏性变更单列 **BREAKING**
- [ ] 定稿提交**先于**打标签
- [ ] 标签指向的提交 = CHANGELOG 定稿那笔（并核对 CHANGELOG 正文里写的提交号与之一致）
- [ ] 推送顺序：先 `main` 后标签
- [ ] Release 说明取的是**该版本**那一节，附件是 `MANIFEST.txt` + `SHA256SUMS.txt`
- [ ] `docs/status.md` 已登记（含门禁实际情况与跳过的项）
- [ ] 结论里写明「运行中的 API 待重启才与 v<版本> 一致」

## 二、首次发布（仓库无任何 `v*` 标签）

`release.ps1` 对这种情况走**基线版分支**：

- **不生成全历史草稿**——把几百条提交堆进首节既不可读、也不是"这一版新增了什么"；
- **版本取 `version.json` 现值、不涨位**——首发标记的是"系统当前可用状态"，
  不是"相对上一版多了什么"，没有上一版可比较；
- 首节**由人撰写**：写清这是什么（基线/可用状态）、由什么构成、以及版本号与迁移编号的关系；
- 标签打在**首节定稿的那笔提交**上。

发布后的下一个版本开始回归常规：按自上个标签以来的提交类型涨位。

> 教训（真实发生过）：首次发布时如果先按"提交类型涨一位"发布，会得到一个**说明里写着上一版不存在**
> 的版本号；而且后来人看到 `0.2.0` 却没有 `0.1.0`，会以为丢了版本。

## 三、并行 Agent 情形（本仓常态）

- **只提交/推送自己的东西**：发布时若工作区有他人未提交改动，**不要**替他们提交，
  也不要 `git checkout`/`git stash` 抹掉；发布的前提是"干净"，不干净就**等或问**。
- **发布前先 `git fetch`**：并行会话可能刚推了提交，你的标签可能落在旧基线上。
- **`docs/status.md` 等共享文件先取锁**：在 `.agents/reservations/` 下按「共享文件路径里 `/` 换成 `__`」
  建锁文件，写完释放。锁被占就等，不要强改。
- **标签是全局唯一的**：另一个 Agent 同时发版会撞版本号。发现"预期版本号的标签已存在"时**停下问人**，
  不要自动跳到下一个号——那会让两个人的发布记录错位。
- **发布动作本身不要并行**：`release.ps1` 改 `version.json` 与 `CHANGELOG.md`，两个进程同时跑必然冲突。

## 四、判定性验收（怎么算"真发布成功了"）

| 判据 | 命令 | 期望 |
|---|---|---|
| 标签在本地 | `git tag -n1 v<版本>` | 显示标签与说明 |
| 标签在远端 | `git ls-remote --tags origin v<版本>` | 两行：标签对象 + 解引用后的提交 |
| 标签指向正确提交 | `git rev-list -n1 v<版本>` | 等于 CHANGELOG 定稿那笔的 `rev-parse HEAD` |
| 提交已在主分支 | `git branch -r --contains <commit>` | 含 `origin/main` |
| Release 已建且非草稿 | `gh release view v<版本> --repo cnlarry/EOS --json isDraft,assets` | `isDraft=false`，附件两个 |
| 代码已同步 | `git rev-list --left-right --count origin/main...HEAD` | `0	0` |
| 工作区干净 | `git status --porcelain` | 空 |
| 产物在位 | `Get-ChildItem artifacts/release-<版本>` | `MANIFEST.txt`、`SHA256SUMS.txt` |

**不算判据的**：`release.ps1` 打印 PASS、健康检查 Healthy、`/health/version` 显示新号。
前两个只说明脚本跑完了；第三个要**用户重启服务**才会变。
