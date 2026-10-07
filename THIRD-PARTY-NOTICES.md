# 第三方组件声明

本项目以 **MIT** 许可发布（见 [LICENSE](LICENSE)）。以下第三方组件随本项目分发，
或在构建产物中被使用；各组件保留其原始许可与版权，不受本项目 MIT 许可覆盖。

> 本文件登记**直接依赖**。完整的传递依赖清单可由工具现算，不必手工维护：
>
> ```powershell
> dotnet list EOS.API/EOS.API.csproj package --include-transitive
> cd EOS.Web; npm ls --all
> ```

## 一、后端（NuGet，`EOS.API`）

| 组件 | 版本 | 许可 |
|---|---|---|
| Microsoft.Data.SqlClient | 7.1.1 | MIT |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT |
| Microsoft.OpenApi | 2.12.0 | MIT |
| DbUp（dbup-sqlserver / dbup-core） | 7.2.0 / 6.1.1 | MIT |
| System.Drawing.Common | 10.0.12 | MIT |
| ZXing.Net | 0.16.11 | **Apache-2.0** |
| QuestPDF | 2026.7.3 | **QuestPDF Community License**（见第四节） |

测试工程（`EOS.API.Tests`，不随产物分发）：

| 组件 | 版本 | 许可 |
|---|---|---|
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT |
| Microsoft.AspNetCore.SignalR.Client | 10.0.12 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 4.0.0 | Apache-2.0 |

## 二、前端（npm，`EOS.Web`）

运行时依赖：

| 组件 | 版本 | 许可 |
|---|---|---|
| react / react-dom | 19.3.0 | MIT |
| react-router-dom | 7.18.4 | MIT |
| @tanstack/react-query | 5.104.1 | MIT |
| @tanstack/react-table | 8.21.3 | MIT |
| @tabler/core | 1.6.1 | MIT |
| @tabler/icons-react | 3.48.0 | MIT |
| zod | 4.6.5 | MIT |
| @dnd-kit/core / @dnd-kit/modifiers | 6.3.1 / 9.0.0 | MIT |
| @xyflow/react（React Flow） | 12.12.0 | MIT |
| @dagrejs/dagre | 3.1.1 | MIT |

开发依赖（不随产物分发）：vite、vitest、oxlint、jsdom、@testing-library/*、@vitest/coverage-v8
均为 **MIT**；typescript 为 **Apache-2.0**。

## 三、随仓库分发的第三方资产

| 资产 | 许可 | 用途 |
|---|---|---|
| `EOS.API/Fonts/NotoSansCJKsc-Regular.otf` | **SIL Open Font License 1.1** | 报表 PDF 的中文字体 |
| `.agents/skills/` 下的 `tdd` / `diagnosing-bugs` / `code-review` / `grill-me` / `grilling` / `handoff` | **MIT** | Agent 技能（见下） |

**关于 `.agents/skills/`**：该目录下 11 个技能分两类——

- **自建**（`eos-verify` / `eos-commit` / `eos-db-objects` / `eos-security-check` / `eos-release`）：
  本项目原创，属本项目 MIT 许可范围，**不是**第三方资产。
  原 `erp-ui` 已于 2026-09-29 下线（其唯一事实源 `ui-reference/` 一并删除），不再随仓库分发；
  原 `eos-publish`（双仓快照发布机制）已随该机制退役一并删除，发布流程现由 `eos-release` 承接；
- **第三方**（上表列出的 6 个）：取自 **[mattpocock/skills](https://github.com/mattpocock/skills)**（MIT），
  **pin 到 commit `6654f6b`（2026-08-24）**，逐文件审读后收录的精选子集。各技能保留其原始许可与版权，
  不受本项目 MIT 许可覆盖。其中 `diagnosing-bugs/scripts/hitl-loop.template.sh` 为只读提示模板（见其文件头）。

升级或替换这 6 个技能时，请同步更新本行的 pin commit，并重跑 `scripts/sync-agent-skills.ps1`。

`EOS.Web/src/assets/react.svg`、`vite.svg` 是前端脚手架生成的示例资产（React 与 Vite 的品牌标识），
当前未被业务代码引用——如确认不需要，删除它们即可减少一处第三方标识的携带。

## 四、特别说明：QuestPDF

**QuestPDF 不是 OSI 认可的开源许可**，它是**双许可**模式（Community License / Professional /
Enterprise License），采用自定义许可文本而非 SPDX 表达式。使用本项目时请留意：

**Community License 的适用范围**（原文见 `LICENSE.md`，其条款以官方文本为准）：

- 个人使用（含商业项目）；
- 学习、评估、培训；
- 慈善机构、学术机构、**开源项目**；
- **年总营收低于 100 万美元**的小型企业（按最近一个完整财年、合并口径计算）；
- **公共部门实体、政府机构与上市公司不适用**，不论营收多少。

**对本项目的下游使用者**：本项目符合上述条件，但**你的使用是否合规，取决于你自己的情况**——
若你所在组织年营收超过 100 万美元，或属于公共部门/上市公司，则不能依据 Community License 使用
QuestPDF，需要向 QuestPDF 购买付费许可。失去资格时有 90 天过渡期。

QuestPDF 承担的责任上限为 100 美元，且不提供任何担保。

## 五、维护约定

- **新增依赖时同步登记本文件**：写清组件名、版本、许可；
- 许可不是 MIT 的（Apache-2.0、OFL、自定义许可等）**必须单列并说明约束**，
  不要让它们混在 MIT 列表里；
- 升级依赖版本时更新本文件的版本号；
- 优先选择 MIT / Apache-2.0 的组件；引入非标准许可前先评估约束能否被接受。
