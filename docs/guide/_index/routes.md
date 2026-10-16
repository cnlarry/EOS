<!-- code-map-fingerprint: ea39115e4aed16b3 -->
# 代码地图（路由与页面）

> 本文件由 [`scripts/gen-code-map.ps1`](../../../scripts/gen-code-map.ps1) **生成**，不要手改；
> `pwsh scripts/gen-code-map.ps1 -Check` 在 CI 里校验它与源码一致（改了控制器或前端路由表后没重新生成即红）。
>
> 两张表都是**确定性提取**：后端来自 `EOS.API/Controllers/*.cs` 的类级 `Route` + 方法级 `[Http*]`；
> 前端来自 `EOS.Web/src/app/workspaceRoutes.tsx` 的 `path:` / `element:` 与包住它的 `RequirePermission`。
> **权限模块号只在前端表里**——后端模块号是运行时从路由参数或请求体取的，属性上没有，猜出来只会是假的。
> 需要"谁调用了谁""改这里会动到哪"时用编辑器/LSP 的引用与实现导航，本表不管调用关系。

## 一、后端路由

- 控制器 41 个、方法级路由属性 283 处、解析出 283 条（两者必须相等，否则 `-Check` 报 FAIL）。

| 方法 | 路径 | 处理函数 | 文件 |
|---|---|---|---|
| GET | `api/v1/admin/assistant/kb/collections` | ListKbCollections | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/kb/documents` | ListKbDocuments | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/kb/documents/{docId:long}` | DeleteKbDocument | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/mechanism` | GetMechanism | `AssistantAdminController.cs` |
| POST | `api/v1/admin/assistant/models` | CreateModel | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/models/{modelId:int}` | DeleteModel | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/models/{modelId:int}` | UpdateModel | `AssistantAdminController.cs` |
| POST | `api/v1/admin/assistant/models/{modelId:int}/activate` | ActivateModel | `AssistantAdminController.cs` |
| POST | `api/v1/admin/assistant/models/active/clear` | ClearActiveModel | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/models/discover` | DiscoverModels | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/presets` | ListPresets | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/providers` | ListProviders | `AssistantAdminController.cs` |
| POST | `api/v1/admin/assistant/providers` | CreateProvider | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/providers/{providerId:int}` | DeleteProvider | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/providers/{providerId:int}` | UpdateProvider | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/providers/{providerId:int}/key` | SetProviderKey | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/sessions` | ListSessions | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/sessions/{sessionId:long}` | DeleteSession | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/sessions/{sessionId:long}/archive` | ArchiveSession | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/sessions/owners` | ListOwners | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/settings` | ListSettings | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/settings/{key}` | ResetSetting | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/settings/{key}` | UpdateSetting | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/settings/scopes` | ListScopes | `AssistantAdminController.cs` |
| PUT | `api/v1/admin/assistant/settings/scopes` | UpsertScope | `AssistantAdminController.cs` |
| DELETE | `api/v1/admin/assistant/settings/scopes/{scopeType}/{scopeKey}` | DeleteScopeLayer | `AssistantAdminController.cs` |
| GET | `api/v1/admin/assistant/usage` | GetModelUsage | `AssistantAdminController.cs` |
| GET | `api/v1/admin/business-flow/domains/{fromRootIdx:int}/links/{toRootIdx:int}` | DomainLink | `BusinessFlowController.cs` |
| GET | `api/v1/admin/business-flow/domains/{rootIdx:int}` | Domain | `BusinessFlowController.cs` |
| GET | `api/v1/admin/business-flow/overview` | Overview | `BusinessFlowController.cs` |
| GET | `api/v1/admin/depot-stock-policy` | List | `DepotStockPolicyController.cs` |
| DELETE | `api/v1/admin/depot-stock-policy/{depotId}` | Delete | `DepotStockPolicyController.cs` |
| PUT | `api/v1/admin/depot-stock-policy/{depotId}` | Save | `DepotStockPolicyController.cs` |
| GET | `api/v1/admin/depot-stock-policy/{depotId}/locations` | Locations | `DepotStockPolicyController.cs` |
| POST | `api/v1/admin/depot-stock-policy/{depotId}/relocate` | Relocate | `DepotStockPolicyController.cs` |
| GET | `api/v1/admin/depot-stock-policy/depots` | Depots | `DepotStockPolicyController.cs` |
| GET | `api/v1/admin/depot-stock-policy/tiers` | Tiers | `DepotStockPolicyController.cs` |
| POST | `api/v1/admin/fields` | Create | `FieldAdminController.cs` |
| DELETE | `api/v1/admin/fields/{table}/{field}` | Delete | `FieldAdminController.cs` |
| GET | `api/v1/admin/fields/{table}/{field}` | Field | `FieldAdminController.cs` |
| PUT | `api/v1/admin/fields/{table}/{field}` | Update | `FieldAdminController.cs` |
| GET | `api/v1/admin/fields/{table}/{field}/history` | FieldHistory | `FieldAdminController.cs` |
| GET | `api/v1/admin/fields/expressions/overview` | ExpressionOverview | `FieldAdminController.cs` |
| POST | `api/v1/admin/fields/expressions/parse` | ParseExpression | `FieldAdminController.cs` |
| GET | `api/v1/admin/fields/expressions/registry` | ExpressionRegistry | `FieldAdminController.cs` |
| POST | `api/v1/admin/fields/expressions/rescan` | RescanExpressions | `FieldAdminController.cs` |
| GET | `api/v1/admin/form-layout/{moduleId:int}` | Get | `FormLayoutController.cs` |
| PUT | `api/v1/admin/form-layout/{moduleId:int}` | Save | `FormLayoutController.cs` |
| POST | `api/v1/admin/form-layout/{moduleId:int}/reset` | Reset | `FormLayoutController.cs` |
| GET | `api/v1/admin/form-layout/{moduleId:int}/templates` | Templates | `FormLayoutController.cs` |
| GET | `api/v1/admin/groups` | Groups | `RightsAdminController.cs` |
| POST | `api/v1/admin/groups` | CreateGroup | `RightsAdminController.cs` |
| DELETE | `api/v1/admin/groups/{groupId}` | DeleteGroup | `RightsAdminController.cs` |
| PUT | `api/v1/admin/groups/{groupId}` | UpdateGroup | `RightsAdminController.cs` |
| GET | `api/v1/admin/groups/{groupId}/button-rights` | GroupButtonRights | `RightsAdminController.cs` |
| PUT | `api/v1/admin/groups/{groupId}/button-rights` | SaveGroupButtonRights | `RightsAdminController.cs` |
| GET | `api/v1/admin/groups/{groupId}/members` | GroupMembers | `RightsAdminController.cs` |
| PUT | `api/v1/admin/groups/{groupId}/members` | SaveGroupMembers | `RightsAdminController.cs` |
| GET | `api/v1/admin/groups/{groupId}/rights` | GroupRights | `RightsAdminController.cs` |
| PUT | `api/v1/admin/groups/{groupId}/rights` | SaveGroupRights | `RightsAdminController.cs` |
| GET | `api/v1/admin/lookups/modules` | LookupModules | `FieldAdminController.cs` |
| GET | `api/v1/admin/menus` | Modules | `MenuAdminController.cs` |
| POST | `api/v1/admin/menus` | Create | `MenuAdminController.cs` |
| DELETE | `api/v1/admin/menus/{id:int}` | Delete | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/{id:int}` | Module | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}` | Update | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/{id:int}/default-columns` | DefaultColumns | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}/default-columns` | SaveDefaultColumns | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}/enabled` | SetEnabled | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}/move` | Move | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}/rename` | Rename | `MenuAdminController.cs` |
| PUT | `api/v1/admin/menus/{id:int}/sort` | Reorder | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/{id:int}/versions` | Versions | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/capabilities` | Capabilities | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/fields` | Fields | `MenuAdminController.cs` |
| GET | `api/v1/admin/menus/tables` | Tables | `MenuAdminController.cs` |
| GET | `api/v1/admin/module-business-config/{moduleId:int}` | Get | `ModuleBusinessConfigController.cs` |
| PUT | `api/v1/admin/module-business-config/{moduleId:int}` | Save | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/{moduleId:int}/action-authorization` | ActionAuthorization | `ModuleBusinessConfigController.cs` |
| POST | `api/v1/admin/module-business-config/{moduleId:int}/actions/disable-all` | DisableAllActions | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/{moduleId:int}/field-labels` | FieldLabels | `ModuleBusinessConfigController.cs` |
| POST | `api/v1/admin/module-business-config/{moduleId:int}/publish` | Publish | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/{moduleId:int}/relations` | Relations | `ModuleBusinessConfigController.cs` |
| POST | `api/v1/admin/module-business-config/{moduleId:int}/simulate` | Simulate | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/{moduleId:int}/target` | Target | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/meta` | Meta | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/overview` | Overview | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-business-config/schemas` | Schemas | `ModuleBusinessConfigController.cs` |
| GET | `api/v1/admin/module-buttons/{moduleId:int}` | Buttons | `ModuleButtonAdminController.cs` |
| PUT | `api/v1/admin/module-buttons/{moduleId:int}` | Save | `ModuleButtonAdminController.cs` |
| GET | `api/v1/admin/module-buttons/{moduleId:int}/action-authorization` | ActionAuthorization | `ModuleButtonAdminController.cs` |
| GET | `api/v1/admin/module-buttons/meta` | Meta | `ModuleButtonAdminController.cs` |
| GET | `api/v1/admin/module-buttons/overview` | Overview | `ModuleButtonAdminController.cs` |
| GET | `api/v1/admin/module-groups/{moduleId:int}` | Groups | `ModuleGroupAdminController.cs` |
| POST | `api/v1/admin/module-groups/{moduleId:int}` | Create | `ModuleGroupAdminController.cs` |
| DELETE | `api/v1/admin/module-groups/{moduleId:int}/{groupId:int}` | Delete | `ModuleGroupAdminController.cs` |
| PUT | `api/v1/admin/module-groups/{moduleId:int}/{groupId:int}` | Update | `ModuleGroupAdminController.cs` |
| POST | `api/v1/admin/module-groups/{moduleId:int}/{groupId:int}/move` | Move | `ModuleGroupAdminController.cs` |
| GET | `api/v1/admin/module-groups/{moduleId:int}/fields` | Fields | `ModuleGroupAdminController.cs` |
| GET | `api/v1/admin/module-groups/overview` | Overview | `ModuleGroupAdminController.cs` |
| GET | `api/v1/admin/rights/fields` | ModuleFields | `RightsAdminController.cs` |
| GET | `api/v1/admin/tables` | Tables | `FieldAdminController.cs` |
| POST | `api/v1/admin/tables` | CreateTable | `FieldAdminController.cs` |
| DELETE | `api/v1/admin/tables/{table}` | DeleteTable | `FieldAdminController.cs` |
| GET | `api/v1/admin/tables/{table}` | Table | `FieldAdminController.cs` |
| PUT | `api/v1/admin/tables/{table}` | UpdateTable | `FieldAdminController.cs` |
| GET | `api/v1/admin/tables/{table}/columns` | TableColumns | `FieldAdminController.cs` |
| GET | `api/v1/admin/tables/{table}/fields` | Fields | `FieldAdminController.cs` |
| POST | `api/v1/admin/tables/{table}/fields/batch` | CreateUnmanagedFields | `FieldAdminController.cs` |
| POST | `api/v1/admin/tables/{table}/fields/ghosts/cleanup` | CleanupGhostFields | `FieldAdminController.cs` |
| GET | `api/v1/admin/tables/{table}/relations` | TableRelations | `FieldAdminController.cs` |
| POST | `api/v1/admin/tables/from-physical` | RegisterPhysicalTable | `FieldAdminController.cs` |
| GET | `api/v1/admin/users` | Users | `UserAdminController.cs` |
| POST | `api/v1/admin/users` | CreateUser | `UserAdminController.cs` |
| GET | `api/v1/admin/users/{userId}/button-rights` | UserButtonRights | `RightsAdminController.cs` |
| PUT | `api/v1/admin/users/{userId}/button-rights` | SaveUserButtonRights | `RightsAdminController.cs` |
| GET | `api/v1/admin/users/{userId}/effective-rights` | EffectiveRights | `RightsAdminController.cs` |
| GET | `api/v1/admin/users/{userId}/groups` | UserGroups | `RightsAdminController.cs` |
| PUT | `api/v1/admin/users/{userId}/groups` | SaveUserGroups | `RightsAdminController.cs` |
| PUT | `api/v1/admin/users/{userId}/password` | SetPassword | `UserAdminController.cs` |
| GET | `api/v1/admin/users/{userId}/rights` | UserRights | `RightsAdminController.cs` |
| PUT | `api/v1/admin/users/{userId}/rights` | SaveUserRights | `RightsAdminController.cs` |
| PUT | `api/v1/admin/users/{userId}/status` | SetStatus | `UserAdminController.cs` |
| GET | `api/v1/app/bootstrap` | Bootstrap | `ApplicationController.cs` |
| POST | `api/v1/assistant/apply-changeset` | ApplyChangeSet | `AssistantController.cs` |
| POST | `api/v1/assistant/approval-requests/confirm` | Confirm | `AssistantApprovalRequestController.cs` |
| POST | `api/v1/assistant/approval-requests/preview` | Preview | `AssistantApprovalRequestController.cs` |
| GET | `api/v1/assistant/availability` | Availability | `AssistantController.cs` |
| POST | `api/v1/assistant/config-changes/apply` | Apply | `AssistantConfigController.cs` |
| POST | `api/v1/assistant/config-changes/preview` | Preview | `AssistantConfigController.cs` |
| POST | `api/v1/assistant/kb/collections/ensure` | EnsureCollection | `KbController.cs` |
| POST | `api/v1/assistant/kb/documents` | IngestDocument | `KbController.cs` |
| DELETE | `api/v1/assistant/kb/documents/{docId:long}` | DeleteDocument | `KbController.cs` |
| GET | `api/v1/assistant/kb/documents/{docId:long}` | GetDocument | `KbController.cs` |
| POST | `api/v1/assistant/kb/search` | Search | `KbController.cs` |
| GET | `api/v1/assistant/memory` | ListMemory | `AssistantController.cs` |
| POST | `api/v1/assistant/memory` | SaveMemory | `AssistantController.cs` |
| DELETE | `api/v1/assistant/memory/{memoryId:long}` | DeleteMemory | `AssistantController.cs` |
| DELETE | `api/v1/assistant/memory/all` | ForgetMe | `AssistantController.cs` |
| GET | `api/v1/assistant/memory/pending` | ListPendingMemory | `AssistantController.cs` |
| POST | `api/v1/assistant/memory/pending/{memoryId:long}/resolve` | ResolvePendingMemory | `AssistantController.cs` |
| PUT | `api/v1/assistant/memory/preferences` | SavePreferences | `AssistantController.cs` |
| PUT | `api/v1/assistant/messages/{messageId:long}/feedback` | SetMessageFeedback | `AssistantController.cs` |
| GET | `api/v1/assistant/metrics` | Metrics | `AssistantController.cs` |
| POST | `api/v1/assistant/record-actions/apply` | Apply | `AssistantRecordActionController.cs` |
| POST | `api/v1/assistant/record-actions/preview` | Preview | `AssistantRecordActionController.cs` |
| GET | `api/v1/assistant/sessions` | ListSessions | `AssistantController.cs` |
| POST | `api/v1/assistant/sessions` | CreateSession | `AssistantController.cs` |
| DELETE | `api/v1/assistant/sessions/{sessionId:long}` | DeleteSession | `AssistantController.cs` |
| PUT | `api/v1/assistant/sessions/{sessionId:long}/archive` | ArchiveSession | `AssistantController.cs` |
| POST | `api/v1/assistant/sessions/{sessionId:long}/chat` | Chat | `AssistantController.cs` |
| GET | `api/v1/assistant/sessions/{sessionId:long}/messages` | ListMessages | `AssistantController.cs` |
| PUT | `api/v1/assistant/sessions/{sessionId:long}/rename` | RenameSession | `AssistantController.cs` |
| GET | `api/v1/assistant/situation` | GetSituation | `AssistantController.cs` |
| GET | `api/v1/assistant/usage` | MyUsage | `AssistantController.cs` |
| POST | `api/v1/auth/login` | Login | `AuthenticationController.cs` |
| POST | `api/v1/auth/logout` | Logout | `AuthenticationController.cs` |
| PUT | `api/v1/auth/password` | ChangePassword | `AuthenticationController.cs` |
| GET | `api/v1/bom/expand` | Expand | `BomExpandController.cs` |
| GET | `api/v1/car-summary` | Query | `CarSummaryController.cs` |
| POST | `api/v1/chooser/query` | Query | `ChooserController.cs` |
| POST | `api/v1/client-errors` | Report | `ClientErrorController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/action/{actionKey}` | RunAction | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/actions` | UserActions | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/approve` | Approve | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/attachments` | List | `AttachmentController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/attachments` | Upload | `AttachmentController.cs` |
| DELETE | `api/v1/document-workbench/{moduleId:int}/attachments/{id:long}` | Delete | `AttachmentController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/attachments/{id:long}/download` | Download | `AttachmentController.cs` |
| PUT | `api/v1/document-workbench/{moduleId:int}/attachments/{id:long}/remark` | UpdateRemark | `AttachmentController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/column-editor` | ColumnEditor | `DocumentWorkbenchController.cs` |
| PUT | `api/v1/document-workbench/{moduleId:int}/column-widths` | UpdateColumnWidths | `DocumentWorkbenchController.cs` |
| DELETE | `api/v1/document-workbench/{moduleId:int}/columns` | ResetColumns | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/columns` | Columns | `DocumentWorkbenchController.cs` |
| PUT | `api/v1/document-workbench/{moduleId:int}/columns` | SaveColumns | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/deapprove` | Deapprove | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/definition` | Definition | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/details` | Details | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/endcase` | EndCase | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/export` | Export | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/export-selected` | ExportSelected | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/field-settings` | FieldSettingsList | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/field-settings/{fieldKey}` | FieldSettings | `DocumentWorkbenchController.cs` |
| PUT | `api/v1/document-workbench/{moduleId:int}/field-settings/{fieldKey}` | UpdateFieldSettings | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/field-settings/lookups/{kind}` | FieldSettingsLookups | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/form-chooser/{fieldKey}` | FormChooser | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/form-definition` | FormDefinition | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/query` | Query | `DocumentWorkbenchController.cs` |
| DELETE | `api/v1/document-workbench/{moduleId:int}/record` | DeleteRecord | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/record` | Record | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/record` | CreateRecord | `DocumentWorkbenchController.cs` |
| PUT | `api/v1/document-workbench/{moduleId:int}/record` | UpdateRecord | `DocumentWorkbenchController.cs` |
| GET | `api/v1/document-workbench/{moduleId:int}/records` | Records | `DocumentWorkbenchController.cs` |
| POST | `api/v1/document-workbench/{moduleId:int}/unendcase` | UnEndCase | `DocumentWorkbenchController.cs` |
| POST | `api/v1/import/parse` | Parse | `ImportController.cs` |
| GET | `api/v1/import/targets` | Targets | `ImportController.cs` |
| GET | `api/v1/import/targets/{moduleId:int}/definition` | Definition | `ImportController.cs` |
| POST | `api/v1/import/targets/{moduleId:int}/execute` | Execute | `ImportController.cs` |
| GET | `api/v1/import/targets/{moduleId:int}/mapping` | Mapping | `ImportController.cs` |
| PUT | `api/v1/import/targets/{moduleId:int}/mapping` | SaveMapping | `ImportController.cs` |
| POST | `api/v1/import/targets/{moduleId:int}/preview` | Preview | `ImportController.cs` |
| GET | `api/v1/import/targets/{moduleId:int}/readiness` | Readiness | `ImportController.cs` |
| GET | `api/v1/import/targets/{moduleId:int}/template` | Template | `ImportController.cs` |
| POST | `api/v1/jobs/attendance-adjust-wage` | AttendanceAdjustWage | `JobsController.cs` |
| POST | `api/v1/jobs/attendance-calc` | AttendanceCalc | `JobsController.cs` |
| POST | `api/v1/jobs/attendance-generate` | AttendanceGenerate | `JobsController.cs` |
| POST | `api/v1/jobs/card-batch` | CardBatch | `JobsController.cs` |
| POST | `api/v1/jobs/mrp-recalc` | MrpRecalc | `JobsController.cs` |
| POST | `api/v1/layout-designer/{moduleId:int}/binding` | SaveBinding | `LayoutDesignerController.cs` |
| GET | `api/v1/layout-designer/{moduleId:int}/definition` | Definition | `LayoutDesignerController.cs` |
| POST | `api/v1/layout-designer/{moduleId:int}/preview` | Preview | `LayoutDesignerController.cs` |
| POST | `api/v1/layout-designer/{moduleId:int}/save` | Save | `LayoutDesignerController.cs` |
| GET | `api/v1/layout-designer/{moduleId:int}/versions` | Versions | `LayoutDesignerController.cs` |
| POST | `api/v1/layout-designer/{moduleId:int}/versions/{version:int}/restore` | RestoreVersion | `LayoutDesignerController.cs` |
| GET | `api/v1/layout-designer/headers` | Headers | `LayoutDesignerController.cs` |
| POST | `api/v1/layout-designer/headers` | SaveHeader | `LayoutDesignerController.cs` |
| GET | `api/v1/layout-designer/templates` | Templates | `LayoutDesignerController.cs` |
| GET | `api/v1/layout-designer/templates/{formatId}/layout` | TemplateLayout | `LayoutDesignerController.cs` |
| GET | `api/v1/navigation/{moduleId:int}/groups` | Groups | `NavigationGroupsController.cs` |
| GET | `api/v1/navigation/{moduleId:int}/groups/{groupId:int}/values` | Values | `NavigationGroupsController.cs` |
| POST | `api/v1/print/{moduleId:int}/pdf` | Pdf | `PrintController.cs` |
| GET | `api/v1/report` | ByModule | `ReportResourceController.cs` |
| GET | `api/v1/report-admin/field-options` | FieldOptions | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/formats` | Formats | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/headers` | Headers | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/modules` | Modules | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/reports` | Reports | `ReportAdminController.cs` |
| POST | `api/v1/report-admin/reports` | CreateReport | `ReportAdminController.cs` |
| DELETE | `api/v1/report-admin/reports/{id}` | DeleteReport | `ReportAdminController.cs` |
| PUT | `api/v1/report-admin/reports/{id}` | UpdateReport | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/sorts` | Sorts | `ReportAdminController.cs` |
| POST | `api/v1/report-admin/sorts` | CreateSort | `ReportAdminController.cs` |
| DELETE | `api/v1/report-admin/sorts/{reportId}/{serialNo:int}` | DeleteSort | `ReportAdminController.cs` |
| PUT | `api/v1/report-admin/sorts/{reportId}/{serialNo:int}` | UpdateSort | `ReportAdminController.cs` |
| GET | `api/v1/report-admin/tails` | Tails | `ReportAdminController.cs` |
| GET | `api/v1/report-center/catalog` | Catalog | `ReportCenterController.cs` |
| POST | `api/v1/report-center/favorite` | SaveFavorite | `ReportCenterController.cs` |
| GET | `api/v1/report-center/inbox` | ListInbox | `ReportInboxController.cs` |
| GET | `api/v1/report-center/inbox/{id:int}/download` | Download | `ReportInboxController.cs` |
| POST | `api/v1/report-center/inbox/{id:int}/read` | MarkRead | `ReportInboxController.cs` |
| POST | `api/v1/report-center/reorder` | Reorder | `ReportCenterController.cs` |
| GET | `api/v1/report-center/subscriptions` | ListSubscriptions | `ReportInboxController.cs` |
| POST | `api/v1/report-center/subscriptions` | CreateSubscription | `ReportInboxController.cs` |
| DELETE | `api/v1/report-center/subscriptions/{id:int}` | DeleteSubscription | `ReportInboxController.cs` |
| PUT | `api/v1/report-center/subscriptions/{id:int}` | UpdateSubscription | `ReportInboxController.cs` |
| POST | `api/v1/report-center/touch` | Touch | `ReportCenterController.cs` |
| GET | `api/v1/report-metrics` | List | `ReportMetricController.cs` |
| GET | `api/v1/report/{reportId}` | Identity | `ReportResourceController.cs` |
| GET | `api/v1/report/{reportId}/condition-options/{serialNo:int}` | ConditionOptions | `ReportResourceController.cs` |
| GET | `api/v1/report/{reportId}/definition` | Definition | `ReportResourceController.cs` |
| POST | `api/v1/report/{reportId}/export` | Export | `ReportResourceController.cs` |
| POST | `api/v1/report/{reportId}/pdf` | Pdf | `ReportResourceController.cs` |
| POST | `api/v1/report/{reportId}/query` | Query | `ReportResourceController.cs` |
| GET | `api/v1/reports/{moduleId:int}/condition-options/{serialNo:int}` | ConditionOptions | `ReportController.cs` |
| GET | `api/v1/reports/{moduleId:int}/definition` | Definition | `ReportController.cs` |
| POST | `api/v1/reports/{moduleId:int}/export` | Export | `ReportController.cs` |
| POST | `api/v1/reports/{moduleId:int}/pdf` | Pdf | `ReportController.cs` |
| GET | `api/v1/reports/{moduleId:int}/print-settings` | PrintSettings | `ReportController.cs` |
| POST | `api/v1/reports/{moduleId:int}/print-settings` | SavePrintSettings | `ReportController.cs` |
| POST | `api/v1/reports/{moduleId:int}/query` | Query | `ReportController.cs` |
| GET | `api/v1/search-center/{moduleId:int}/definition` | Definition | `SearchCenterController.cs` |
| POST | `api/v1/search-center/{moduleId:int}/query` | Query | `SearchCenterController.cs` |
| GET | `api/v1/search-center/modules` | Modules | `SearchCenterController.cs` |
| GET | `api/v1/settings/{scope}` | GetSettings | `SettingsController.cs` |
| PUT | `api/v1/settings/{scope}` | UpdateSettings | `SettingsController.cs` |
| GET | `api/v1/table-data/field-audit/{kind}` | FieldAudit | `TableDataController.cs` |
| POST | `api/v1/workbench-definitions/backfill` | Backfill | `WorkbenchDefinitionController.cs` |
| POST | `api/v1/workbench-definitions/publish` | Publish | `WorkbenchDefinitionController.cs` |
| GET | `api/v1/workbench-definitions/staleness` | Staleness | `WorkbenchDefinitionController.cs` |
| GET | `api/v1/workbench-definitions/status` | Status | `WorkbenchDefinitionController.cs` |
| POST | `api/v1/workbench-definitions/validate` | Validate | `WorkbenchDefinitionController.cs` |
| GET | `api/v1/workflow/{moduleId:int}/history` | History | `WorkflowController.cs` |
| GET | `api/v1/workflow/definitions` | List | `FlowDefinitionController.cs` |
| DELETE | `api/v1/workflow/definitions/{moduleId:int}` | Delete | `FlowDefinitionController.cs` |
| GET | `api/v1/workflow/definitions/{moduleId:int}` | Detail | `FlowDefinitionController.cs` |
| POST | `api/v1/workflow/definitions/{moduleId:int}` | Save | `FlowDefinitionController.cs` |
| GET | `api/v1/workflow/definitions/people` | People | `FlowDefinitionController.cs` |
| GET | `api/v1/workflow/monitor` | Monitor | `WorkflowController.cs` |
| GET | `api/v1/workflow/monitor/{wfId:long}` | MonitorDetail | `WorkflowController.cs` |
| GET | `api/v1/workflow/my-started` | MyStarted | `WorkflowController.cs` |
| GET | `api/v1/workflow/my-tasks` | MyTasks | `WorkflowController.cs` |
| POST | `api/v1/workflow/tasks/{myTaskId:long}/approve` | ApproveTask | `WorkflowController.cs` |
| POST | `api/v1/workflow/withdraw` | Withdraw | `WorkflowController.cs` |

## 二、前端页面路由

- 源文件 `path:` 50 处、解析出 50 条（两者必须相等）。权限模块号来自包住该路由的
  `RequirePermission permission={moduleReadPermission(N)}`；空表示该页面不需要模块读权限（如登录后即可见的助手页）。

| 路径 | 页面 | 权限模块 |
|---|---|---|
| `/*` | `UnknownRoutePage` | — |
| `/admin/assistant/kb` | `KbAdminPage` | 3103 |
| `/admin/assistant/mechanism` | `MechanismOverviewPage` | 3104 |
| `/admin/assistant/models` | `ModelAdminPage` | 3102 |
| `/admin/assistant/sessions` | `AdminSessionsPage` | 3101 |
| `/admin/assistant/settings` | `AssistantSettingsPage` | 3105 |
| `/admin/business-flow` | `BusinessFlowPage` | 2314 |
| `/admin/depot-stock-policy` | `DepotStockPolicyPage` | 110310 |
| `/admin/field-audit` | `FieldAuditPage` | 2303 |
| `/admin/fields/:tableId/:fieldId` | `FieldEditorRoute` | 2302 |
| `/admin/groups` | `UserGroupAdminPage` | 2305 |
| `/admin/groups/:groupId/button-rights` | `GroupButtonRightsPage` | 2305 |
| `/admin/groups/:groupId/members` | `GroupMembersPage` | 2305 |
| `/admin/groups/:groupId/rights` | `GroupRightsPage` | 2305 |
| `/admin/logs` | `LogAdminPage` | 2313 |
| `/admin/menus` | `MenuAdminPage` | 230101 |
| `/admin/module-actions` | `ModuleActionsPage` | 230104 |
| `/admin/module-buttons` | `ModuleButtonsPage` | 230103 |
| `/admin/module-groups` | `ModuleGroupsPage` | 230102 |
| `/admin/report-setup` | `ReportAdminRoute` | — |
| `/admin/tables` | `TableAdminPage` | 2302 |
| `/admin/tables/:tableId/fields` | `FieldAdminRoute` | 2302 |
| `/admin/users` | `UserAdminPage` | 2306 |
| `/admin/users/:userId/button-rights` | `UserButtonRightsPage` | 2306 |
| `/admin/users/:userId/rights` | `UserRightsPage` | 2306 |
| `/assistant` | `AssistantPage` | 2103 |
| `/assistant/sessions` | `SessionAdminPage` | 2103 |
| `/bom-expand` | `BomExpandPage` | — |
| `/car-summary` | `CarSummaryPage` | 199901 |
| `/dashboard` | `DashboardPage` | — |
| `/fallback/modules/:moduleId` | `FallbackModulePage` | — |
| `/import` | `ImportPage` | 230902 |
| `/jobs` | `JobPage` | — |
| `/layout-designer/:moduleId` | `LayoutDesignerPage` | — |
| `/my-tasks` | `MyTasksPage` | 2102 |
| `/print/:moduleId` | `PrintViewPage` | — |
| `/report-center` | `ReportCenterPage` | — |
| `/report-center/inbox` | `ReportInboxPage` | — |
| `/report/:reportId` | `ReportIdentityRoute` | — |
| `/reports/:moduleId` | `ReportViewerRoute` | — |
| `/search-center/:moduleId?` | `SearchCenterPage` | — |
| `/settings/:table` | `SystemSettingsPage` | — |
| `/settings/profile` | `ProfilePage` | — |
| `/workbench/:moduleId` | `WorkbenchRoute` | — |
| `/workbench/:moduleId/copy` | `FormEditorRoute` | — |
| `/workbench/:moduleId/edit/*` | `FormEditorRoute` | — |
| `/workbench/:moduleId/new` | `FormEditorRoute` | — |
| `/workbench/:moduleId/view/*` | `FormEditorRoute` | — |
| `/workflow/design` | `FlowDesignPage` | 2101 |
| `/workflow/monitor` | `FlowMonitorPage` | 2103 |

