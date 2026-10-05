# 文档 ↔ 源码映射

本表是 `_tools/check-freshness.ps1` 的输入，也是本手册的篇目清单。

- **文档**：本目录下的文件名。尚未创建的篇目照常登记，脚本会把它列进「待写」，不算落后。
- **状态**：人工维护（未开始 / 进行中 / 已完成）。
- **源码路径**：该文档所描述能力对应的源码位置，写**文件或目录**（git 会递归目录），**不要用通配符**；多个用 `,` 分隔；无对应源码（纯约定类）写 `-`。

新增文档必须在此登记，否则检测脚本扫不到它。

| 文档 | 状态 | 源码路径 |
|---|---|---|
| 00-术语表.md | 已完成 | - |
| 01-代码地图与仓库结构.md | 已完成 | - |
| 02-本地环境与运行.md | 已完成 | EOS.API/appsettings.json, EOS.Web/package.json, EOS.Web/vite.config.ts, db |
| 03-工程规范与硬约束.md | 已完成 | - |
| 04-API契约与前后端协作.md | 已完成 | EOS.API/Errors, EOS.API/Middleware, EOS.API/Models, EOS.Web/src/services |
| 05-数据库迁移与建库.md | 已完成 | EOS.API/Data/Migrations, EOS.API/Data/ErpDatabaseInitializer.cs, db |
| 06-测试与质量门禁.md | 已完成 | EOS.API.Tests, .github/workflows |
| 10-元数据模型.md | 已完成 | - |
| 11-元数据运维.md | 已完成 | EOS.API/Controllers/FieldAdminController.cs, EOS.API/Controllers/TableDataController.cs, EOS.API/Controllers/ModuleBusinessConfigController.cs, EOS.API/Controllers/BusinessFlowController.cs, EOS.API/Data/FieldAdminRepository.cs, EOS.API/Data/ModuleBusinessConfigRepository.cs, EOS.API/Features/BusinessFlow, EOS.Web/src/features/field-admin, EOS.Web/src/features/business-flow |
| 12-元数据消费.md | 已完成 | EOS.API/Data/WorkbenchFieldMetaMapper.cs, EOS.API/Data/WorkbenchDefinitionBuilder.cs, EOS.API/Data/ModuleBusinessMap.cs, EOS.API/Data/ModuleRouteValidator.cs |
| 20-认证与会话.md | 已完成 | EOS.API/Security, EOS.API/Controllers/AuthenticationController.cs, EOS.API/Data/AuthenticationRepository.cs, EOS.Web/src/features/auth |
| 21-授权模型.md | 已完成 | EOS.API/Security/PermissionService.cs, EOS.API/Security/IPermissionService.cs, EOS.API/Security/CurrentUserContext.cs, EOS.API/Security/PermissionAction.cs, EOS.API/Security/ModuleIds.cs |
| 22-权限配置后台.md | 已完成 | EOS.API/Controllers/RightsAdminController.cs, EOS.API/Controllers/UserAdminController.cs, EOS.API/Controllers/MenuAdminController.cs, EOS.API/Data/RightsAdminRepository.cs, EOS.API/Data/UserAdminRepository.cs, EOS.Web/src/features/rights-admin, EOS.Web/src/features/user-admin |
| 30-审计日志.md | 已完成 | EOS.API/Controllers/AuditController.cs, EOS.API/Data/WorkbenchAuditWriter.cs, EOS.API/Features/Diagnostics/LogsController.cs |
| 31-运行日志与诊断.md | 已完成 | EOS.API/Logging, EOS.API/Telemetry, EOS.API/Middleware, EOS.API/Health, EOS.API/Errors, EOS.API/Features/Diagnostics |
| 40-统一工作台.md | 已完成 | EOS.API/Controllers/DocumentWorkbenchController.cs, EOS.API/Data/Workbench, EOS.API/Data/DocumentWorkbenchRepository.cs, EOS.Web/src/features/document-workbench |
| 41-定义快照与发布.md | 已完成 | EOS.API/Data/WorkbenchDefinitionProvider.cs, EOS.API/Data/WorkbenchDefinitionSnapshotService.cs, EOS.API/Data/WorkbenchDirtyMarker.cs, EOS.API/Controllers/WorkbenchDefinitionController.cs |
| 42-统一表单.md | 已完成 | EOS.API/Data/Forms, EOS.API/Data/FormDefaultRules.cs, EOS.API/Data/FormFieldSelector.cs, EOS.API/Controllers/FormLayoutController.cs |
| 43-表单版式设计.md | 已完成 | EOS.API/Controllers/LayoutDesignerController.cs, EOS.API/Data/LayoutExceptions.cs, EOS.Web/src/features/layout-designer, EOS.Web/src/features/form-designer |
| 44-受控查询与条件模板.md | 已完成 | EOS.API/Data/Query, EOS.API/Data/ConditionTemplateParser.cs, EOS.API/Data/ChooserFilterCompiler.cs, EOS.Web/src/components/common/ErpQueryBuilder.tsx, EOS.Web/src/components/common/queryCondition.ts |
| 45-受限表达式与虚拟字段.md | 已完成 | EOS.API/Data/RestrictedExpressionService.cs, EOS.API/Data/VirtualColumnResolver.cs, EOS.API/Data/DataFilterParser.cs, EOS.API/Data/GroupExpressionParser.cs, EOS.API/Data/VirtualArithmeticParser.cs, EOS.API/Data/ConvertFunctionResolver.cs |
| 46-统一选择器.md | 已完成 | EOS.API/Controllers/ChooserController.cs, EOS.API/Data/ChooserRepository.cs, EOS.API/Data/ChooserConditionBuilder.cs, EOS.API/Data/ChooserFilterModel.cs, EOS.Web/src/components/common/UnifiedChooser.tsx, EOS.Web/src/components/common/chooserSource.ts |
| 47-单据行为效果引擎.md | 已完成 | EOS.API/Data/Effects, EOS.API/Data/DocumentActions, EOS.API/Data/BusinessActionCatalog.cs, EOS.API/Data/EffectStructSchemas.cs |
| 48-生命周期与审批流.md | 已完成 | EOS.API/Data/WorkflowEngine.cs, EOS.API/Data/WorkflowStates.cs, EOS.API/Data/WorkbenchApprovalService.cs, EOS.API/Data/FlowDefinitionService.cs, EOS.API/Controllers/WorkflowController.cs, EOS.API/Controllers/FlowDefinitionController.cs, EOS.Web/src/features/workflow |
| 50-统一报表.md | 已完成 | EOS.API/Controllers/ReportController.cs, EOS.API/Controllers/ReportCenterController.cs, EOS.API/Data/ReportRepository.cs, EOS.API/Data/ReportAggregateRegistry.cs, EOS.API/Data/ReportListGrouping.cs, EOS.Web/src/features/reports |
| 51-报表可视化设计器.md | 已完成 | EOS.API/Controllers/ReportAdminController.cs, EOS.API/Controllers/ReportResourceController.cs, EOS.API/Data/ReportAdminRepository.cs, EOS.API/Data/ReportFormLayoutRepository.cs, EOS.API/Data/ReportFormatRepository.cs |
| 52-报表打印版式.md | 已完成 | EOS.API/ReportFormats, EOS.API/Data/ILayoutRenderer.cs, EOS.API/Data/QuestPdfLayoutRenderer.cs, EOS.API/Data/PdfLayout.cs, EOS.API/Data/ReportPdfService.cs, EOS.API/Controllers/PrintController.cs, EOS.Web/src/features/print |
| 60-AI工作助手.md | 已完成 | EOS.API/Features/Assistant/ModelAccess, EOS.API/Features/Assistant/Memory, EOS.API/Features/Assistant/Kb, EOS.API/Features/Assistant/Metrics, EOS.API/Features/Assistant/Governance, EOS.API/Features/Assistant/Admin, EOS.API/Features/Assistant/ChatService.cs, EOS.API/Controllers/AssistantController.cs, EOS.API/Data/AssistantRepository.cs, EOS.API/Data/AssistantQueryBuilder.cs, EOS.API/Controllers/KbController.cs, EOS.Web/src/features/assistant/api.ts, EOS.Web/src/features/assistant/types.ts, EOS.Web/src/features/assistant/AssistantMemoryPanel.tsx |
| 61-工作助手的处境与诊断.md | 已完成 | EOS.API/Features/Assistant/Situation, EOS.API/Features/Assistant/Diagnosis, EOS.API/Features/Assistant/Config, EOS.API/Features/Assistant/Actions, EOS.API/Features/Assistant/Catalog, EOS.API/Data/KnowledgeRepository.cs, scripts/sync-guide-to-kb.ps1, EOS.API/Controllers/AssistantRecordActionController.cs, EOS.API/Controllers/AssistantApprovalRequestController.cs, EOS.API/Features/Assistant/Tools/ListMyCapabilitiesTool.cs, EOS.API/Features/Assistant/Tools/GetMyDigestTool.cs, EOS.API/Features/Assistant/Tools/DiagnoseRecordTool.cs, EOS.API/Features/Assistant/Tools/DiagnoseModuleTool.cs, EOS.API/Features/Assistant/Tools/GetModuleFlowTool.cs, EOS.API/Features/Assistant/Tools/RecordActionTools.cs, EOS.API/Features/Assistant/Tools/ApprovalRequestTools.cs, EOS.API/Features/Assistant/Tools/IToolCallContextTool.cs, EOS.API/Data/Workbench/LifecycleEditGuards.cs, EOS.Web/src/features/assistant/ActionCard.tsx, EOS.Web/src/features/assistant/ApprovalRequestCard.tsx, EOS.Web/src/features/assistant/AssistantDock.tsx, EOS.Web/src/features/assistant/situationSource.ts, EOS.Web/src/features/assistant/pageContext.ts, EOS.Web/src/features/assistant/useChatStream.ts |
| 62-嵌入模型与知识库接线.md | 已完成 | EOS.API/Features/Assistant/ModelAccess, EOS.API/Data/KnowledgeRepository.cs, EOS.API/Controllers/KbController.cs, scripts/sync-guide-to-kb.ps1 |
| 70-后台配置面总览.md | 已完成 | EOS.API/Controllers/MenuAdminController.cs, EOS.API/Controllers/NavigationGroupsController.cs, EOS.API/Controllers/SettingsController.cs, EOS.API/Data/SystemParameterService.cs, EOS.Web/src/features/menu-admin, EOS.Web/src/features/admin, EOS.Web/src/features/settings |
| 71-附件.md | 已完成 | EOS.API/Controllers/AttachmentController.cs, EOS.API/Data/AttachmentRepository.cs |
| 72-导入与导出.md | 已完成 | EOS.API/Controllers/ImportController.cs, EOS.API/Data/ImportService.cs, EOS.API/Data/RecordPayloadValidator.cs, EOS.Web/src/features/import |
| 73-作业与调度.md | 已完成 | EOS.API/Controllers/JobsController.cs, EOS.API/Services/ReportInboxScheduler.cs, EOS.API/Data/ReportInboxRepository.cs, EOS.API/Controllers/ReportInboxController.cs, EOS.Web/src/features/jobs |
| 74-搜索中心.md | 已完成 | EOS.API/Controllers/SearchCenterController.cs, EOS.API/Data/SearchCenterRepository.cs, EOS.Web/src/features/search-center |
