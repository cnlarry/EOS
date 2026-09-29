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

- 一键回归：`scripts/regression.ps1`（单测 + 前端 lint/build/test + E2E 四链 + UI 冒烟），统一报告到 `logs/goal/regression/`。
- 任何阶段验收前必须回归全绿（基线数字见 `docs/status.md` §2）。
- 开工前运行 `pwsh scripts/check-docs.ps1`，确认 `docs/status.md` 与 HEAD 对齐、活跃计划未陈旧。

## 4. 收尾

- 任务结束必须刷新 `docs/status.md`（基线/收口/待决策/技术债），完成计划移入 `docs/plans/archive/`；拍板事项写入 `docs/plans/业务待定项决策清单.md`。
- 提交前先调用 `eos-commit` 技能核对暂存范围。