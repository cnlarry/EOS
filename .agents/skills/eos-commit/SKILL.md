---
name: eos-commit
description: EOS 项目提交纪律。提交前必须按此流程核对暂存范围，只提交本会话自己改动的内容。Use when the user asks to commit, stage, "提交/暂存", or before creating a commit/PR.
---

EOS 是多 Agent 并行开发的仓库，提交纪律是硬约束。权威规则以 `AGENTS.md`「提交规范」为准，本文是执行流程。

## 铁律

- **每个 Agent 只提交自己修改的文件/文档**：暂存与提交范围严格限定在本会话自己改动的内容。
- 其它会话、用户或并发进程的未提交改动一律**不暂存、不提交、不修改**，保持原样。
- 不提交数据库密码、旧密码密文、Cookie 密钥或任何敏感凭据。

> `docs/status.md`、`docs/plans/` 等**本机工作资产不随仓库分发**——下文提到它们时，
> 在别处克隆的仓库里找不到属预期，不是缺文件。

## 执行流程

1. **盘点改动**：
   ```bash
   git status
   git diff            # 工作区未暂存改动
   git log --oneline -10
   ```

2. **确认暂存清单**：`git diff --cached` 核对暂存内容，确认不含他人改动。

3. **同一文件混有他人改动时**：按 hunk 精确暂存（`git apply --cached` 或等效方式），**不得整文件连带提交**。

4. **只暂存本会话涉及的文件**，例如：
   ```bash
   git add .agents/skills/... docs/status.md
   ```

5. **写提交信息**：遵循仓库既有风格，简明描述改动（如 `fix: ...` / `feat: ...` / `docs: ...`），不包含凭据。

6. **提交**：仅在用户明确要求时执行 `git commit`；提交失败或 hooks 拒绝时修复后新建提交，不 amend 失败提交。

## 收尾核对

- `git status` 干净且暂存清单 = 本会话改动清单；
- 任务收尾文档（如 `docs/status.md`）只写入自己负责的事项，不替他人补录或改写他人内容。