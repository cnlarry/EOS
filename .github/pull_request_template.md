## 改了什么

<!-- 一句话说清本 PR 做了什么、为什么这么做。 -->

## 自查

- [ ] `EOS.API`：`dotnet build` 通过；行为变更补了测试（真库用例标 `[Trait("Category", "Integration")]`）
- [ ] `EOS.Web`：`npm run lint`、`npm run build`、`npm test` 通过
- [ ] 结构变更走 `EOS.API/Data/Migrations/` 新增脚本（编号取现有最大 + 1，对象名全大写）
- [ ] 依赖库内元数据的改动，已在迁移里一并提供幂等的 INSERT/UPDATE
- [ ] 改了接口 / 路由 / 错误码 / 配置项 / 权限键 / 公共组件 props / 库对象 → 已同步 `docs/guide/` 对应篇目
- [ ] 本地跑过相关门禁脚本（至少 `pwsh scripts/check-di-registrations.ps1`）
- [ ] 第三方依赖有增减时已更新 `THIRD-PARTY-NOTICES.md`
- [ ] 提交信息符合 Conventional Commits（`pwsh scripts/check-commit-msg.ps1 -Message '...'`）
- [ ] 未提交任何连接串、口令、密钥与真实业务数据

## 说明

<!-- 跨模块或会长期约束实现方式的改动，请附上 docs/decisions/ 下的 ADR。
     大改动建议先开 issue 讨论设计，避免返工。 -->
