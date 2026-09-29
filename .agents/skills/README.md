# Agent Skills（EOS 团队技能库）

**权威源**：`.agents/skills/`（Agent Skills 开放标准，agentskills.io）——被 Codex、OpenCode、Grok Build
原生发现；用 Cursor 时需自行运行 `scripts/sync-agent-skills.ps1` 把它镜像到 `.cursor/skills/`
（junction，不入库；**未运行时该目录不存在，这是正常的，不是缺文件**）。

AGENTS.md 是常驻规范，技能是**按需加载的可执行流程**，二者不重复：技能触发时正文只注入一次。

> ⚠️ **技能没有新鲜度门禁**：`docs/guide/_tools/check-freshness.ps1` 只守 `docs/guide/`，
> 技能既不在 `docs/guide/_map.md` 里也没有脚本守着。**引用脚本、目录、命令前请先确认它还在**
> （曾经出现过引用已删除的发布脚本、已退役的 `EOS.Database/` 的情况）。

## 来源与纪律

| 技能 | 来源 | 说明 |
|---|---|---|
| `eos-verify` / `eos-commit` / `eos-db-objects` / `eos-security-check` | 自建（本仓库） | EOS 验证、提交、库对象/迁移、安全边界四大可执行流程 |
| `eos-publish` | 自建（本仓库） | ⚠️ **已退役**：原双仓发布机制的墓碑说明，只解释发生了什么、不执行任何动作（脚本已删除，见该技能正文） |
| `erp-ui` | 自建（本仓库） | ⚠️ **依赖本机排除目录** `ui-reference/`（不入版本控制），因此对本机之外的开发者不可用 |
| `tdd` / `diagnosing-bugs` / `code-review` / `grill-me` / `grilling` / `handoff` | [mattpocock/skills](https://github.com/mattpocock/skills)，MIT | 精选子集，pin 到 commit `6654f6b`（2026-08-24）；已在 `THIRD-PARTY-NOTICES.md` 登记 |

- 外部技能只选子集、逐文件审读后入仓；安装新技能前先跑本目录校验（name 与目录名一致、无恶意脚本）。
- 社区技能含可执行脚本，启用前必须审读 `scripts/`；本仓库 `diagnosing-bugs` 的脚本为只读提示模板（见其文件头）。
- 工具专属扩展保留在各技能子目录（如 `agents/openai.yaml` 供 Codex 消费），不污染共享正文。
- 移除或替换技能后重跑 `scripts/sync-agent-skills.ps1` 同步 Cursor 镜像。

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