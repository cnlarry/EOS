# Agent Skills（EOS 团队技能库）

**权威源**：`.agents/skills/`（Agent Skills 开放标准，agentskills.io）——被 Codex、OpenCode、Grok Build
原生发现；用 Cursor 时需自行运行 `scripts/sync-agent-skills.ps1` 把它镜像到 `.cursor/skills/`
（junction，不入库；**未运行时该目录不存在，这是正常的，不是缺文件**）。

AGENTS.md 是常驻规范，技能是**按需加载的可执行流程**，二者不重复：技能触发时正文只注入一次。

> **新鲜度门禁**：`pwsh scripts/check-agent-skills.ps1` 校验本目录四件事——
> ① frontmatter `name` 与目录名一致；② 引用资产**全部存在**（例外口径见 `_map.md`）；
> ③ **无未识别的顶层片段**（顶层目录既不在仓库根也未登记口径即报错——专防"目录删了但技能还在引用"）；
> ④ 引用资产未比技能更新（陈旧 STALE）。
>
> **①②③ 阻断（退出码 1），④ 只提示**：陈旧是 git 时间戳代理，资产一改就命中，拿来阻断只会逼出
> "为过门禁而空改文档"的动作。本脚本只读文件系统与 git 历史，不连库、不发网络请求。
>
> 为什么需要它：本目录曾出现引用**已被删除的发布脚本**与**已退役目录**的漂移，
> 而没有门禁的"不双写"纪律守不住。改动技能或其所引用的资产后请运行它。

## 来源与纪律

| 技能 | 来源 | 说明 |
|---|---|---|
| `eos-verify` / `eos-commit` / `eos-db-objects` / `eos-security-check` | 自建（本仓库） | EOS 验证、提交、库对象/迁移、安全边界四大可执行流程 |
| `eos-release` | 自建（本仓库） | **唯一的发布技能**：判定版本号 → CHANGELOG 定稿 → 打标签 → 推送 → 建 GitHub Release → 收尾核对；配套 `<scripts/publish-release.ps1>`（位于该技能 `scripts/` 下）带网络重试。原 `eos-publish`（双仓快照机制）已删除，不要再从历史里复活 |
| `tdd` / `diagnosing-bugs` / `code-review` / `grill-me` / `grilling` / `handoff` | [mattpocock/skills](https://github.com/mattpocock/skills)，MIT | 精选子集，pin 到 commit `6654f6b`（2026-08-24）；已在 `THIRD-PARTY-NOTICES.md` 登记 |

- 外部技能只选子集、逐文件审读后入仓；新增或改动技能后跑 `scripts/check-agent-skills.ps1` 校验
  （`name` 与目录名一致、`_map.md` 登记的引用存在、无恶意脚本靠人工审读）。
- 社区技能含可执行脚本，启用前必须审读 `scripts/`；本仓库 `diagnosing-bugs` 的脚本为只读提示模板（见其文件头）。
- 工具专属扩展保留在各技能子目录（如 `agents/openai.yaml` 供 Codex 消费），不污染共享正文。
- **引用的资产在 `_map.md` 里登记**：技能引用的脚本、目录、文件必须登记，否则门禁报漏登记。
- 移除或替换技能后：更新 `_map.md`，并重跑 `scripts/sync-agent-skills.ps1` 同步 Cursor 镜像
  （**未使用 Cursor 时可跳过**——该镜像目录本就不存在，不存在遗留胖 junction 的问题）。
- **`erp-ui` 已于 2026-09-29 下线**：技能本身几乎未被使用，其唯一事实源 `ui-reference/` 已一并删除。
  如需重建，请从 git 历史取回技能正文；`ui-reference/` 不在 git 内，需另行收集。

## 各工具发现路径（2026-08 核实）

| 工具 | 路径 | 说明 |
|---|---|---|
| Codex CLI | `.agents/skills/`（仓内）、`~/.agents/skills/` | `$skill-name` 调用；脚本默认沙箱执行 |
| OpenCode | `.opencode/skills/` + 兼容 `.agents/skills/`、`.claude/skills/` | 原生 `skill` 工具按需加载 |
| Cursor | `.cursor/skills/`（镜像 junction） | v2.4+；`.cursor/rules` 仍是常驻规则位 |
| Grok Build | 自动扫描 `.grok/`、`.agents/`、`.claude/`、`.cursor/` | Claude Code 配置生态兼容 |

## 新增技能规范

- `name` 全小写连字符，与目录名一致；frontmatter 只写标准 `name`+`description`（description 是触发词）。
- 正文 <500 行，长资料放 `references/`；带脚本必须 `scripts/` 子目录并声明失败路径。
- 技能只做**按需流程**，常驻规范一律留在 AGENTS.md，避免双写漂移。