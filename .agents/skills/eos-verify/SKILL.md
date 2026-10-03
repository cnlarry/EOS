---
name: eos-verify
description: EOS 项目验证与自检流程。完成任务或提交前必须按此流程运行质量检查。Use when the user asks to run checks, verify a change, "跑测试/lint/build/回归", or claims work is done.
---

EOS 是元数据驱动的 ERP 重构项目。任何改动落地前必须跑对应组件的验证命令；验证通过不等于可运行（本机存在企业 Code Integrity/WDAC 阻断，见下文）。权威约束以 `AGENTS.md`「验证命令」「Agent PowerShell 执行纪律」为准，本文只是可执行流程。

## 1. 组件验证命令（在对应目录执行）

- `EOS.Web`（前端）：
  ```bash
  cd EOS.Web
  npm run lint
  npm run build
  ```
- `EOS.API`（后端）：
  ```bash
  cd EOS.API
  dotnet build
  ```

注意：本机 WDAC 阻断，`dotnet build` 通过 **不等于** 可运行。需运行验证时先确认 DLL 能否加载（`/health/*` 端点或端口探测），不要假设构建成功即运行成功。

## 2. PowerShell 执行纪律（Agent 必须遵守）

- 文件修改一律用 Edit/Write 工具；PowerShell 仅用于运行程序与系统操作。禁止用内联多语句字符串替换改文件。
- 批量脚本先试点后放量：先在 1 个文件上执行并核对，确认无误再放行全量；脚本必须 `$ErrorActionPreference='Stop'` + try/catch + 明确 exit code。
- 编码固定 UTF-8：执行前置 `[Console]::OutputEncoding=[Text.Encoding]::UTF8`；脚本输出用 ASCII 标记（PASS/FAIL/SKIP）；读写文件显式 `-Encoding utf8`。
- 测试命令显式指 csproj：统一从仓库根执行
  `dotnet test EOS.API.Tests\EOS.API.Tests.csproj --filter ...`
  （在错误目录跑 `dotnet test` 会静默空跑且无输出）；结果重定向到 `%TEMP%\opencode\*.txt` 后读取文件，不依赖控制台流。
- 可重复环境操作优先走仓库既有脚本（`scripts/dev-services.ps1`、`scripts/regression.ps1`），不即兴造轮子。
- 服务/长驻进程脚本禁止在 Agent 会话内同步等待：`Start-Process` 无效（作业对象会等待全部后代进程退出）。一律用
  `Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = 'pwsh -NoProfile -File "脚本路径" 参数' }`
  发射；成功与否以 `/health/*` 端点、端口探测或落盘摘要文件为准；重复发射前先查旧实例防并发双跑。

## 3. 回归

> **关于本机工作资产**：本节与下一节提到的 `docs/status.md`、`docs/plans/`（含 `archive/`）
> **不随仓库分发**——它们只存在于采用这套工作流的工作副本里。在别处克隆时找不到它们属预期，
> 不是缺文件；相应地，"刷新 status.md""把完成计划移入 archive"这两条纪律**只在该工作副本内适用**。

- 一键回归：`scripts/regression.ps1`（单测 + 前端 lint/build/test + E2E 四链 + UI 冒烟），统一报告到 `logs/goal/regression/`。
- 任何阶段验收前必须回归全绿（基线数字见 `docs/status.md` §2）。
- 开工前运行 `pwsh scripts/check-docs.ps1`，确认 `docs/status.md` 未长期未刷新（该门禁按"落后多少提交"计，超阈值仅提示）、活跃计划未陈旧。
- 动手前先扫一眼仓库根 [`LESSONS.md`](../../../LESSONS.md)（可复用的坑清单，四段式：触发／症状 → 根因 → 处置 → 防线）——
  尤其写迁移、改元数据、写测试与门禁、碰真库数据时；收尾时把可复用的新坑补一条进去。

## 4. 收尾

- 任务结束按**结论**刷新 `docs/status.md` 的对应节（§2 基线 / §3 收口 / §4 进行中 / §5 待决策 / §6 技术债），
  **不追加「状态更新」流水条目**（该文件 §8 有明确纪律）；没有结论变化就不动它。
  完成计划移入 `docs/plans/archive/`；拍板事项写入 `docs/plans/decision-ledger.md`。
- 提交前先调用 `eos-commit` 技能核对暂存范围。