> 读者：前端、后端 ｜ 前置：[03-工程规范与硬约束](./03-工程规范与硬约束.md)

# 04 API 契约与前后端协作

## 一、路由

- 统一前缀 `api/v1/`，控制器上声明：

  ```csharp
  [ApiController, Authorize, Route("api/v1/document-workbench/{moduleId:int}")]
  ```

- 资源用复数、层级用路径段（`document-workbench/{moduleId}`），动作作为末段
  （`records`、`query`、`record`、`export`）；
- 模块化能力把 `moduleId` 放在路由里，而不是查询参数——它是权限判定的输入，
  放路由上便于日志与审计按模块归档；
- 路由必须登记在模块路由校验的白名单里，否则按未映射处理。

## 二、认证与授权

- 控制器默认带 `[Authorize]`；认证走会话 Cookie，登录入口在 `AuthenticationController`。
- **服务端每个请求重新授权**。控制器内按 `moduleId` 取当前用户权限，据此决定：

  | 判定失败 | 返回 |
  |---|---|
  | 未登录 | `401`（由认证中间件处理） |
  | 已登录但无该模块权限 | `403`（`Forbid()`） |
  | 模块/定义不存在、模块不开放该能力 | `404` |

  注意"无权限"与"不存在"要分开：前者 403，后者 404。前端据状态码区分提示。

- 权限对象同时携带字段级限制与数据范围，后者必须传进查询
  （见 [21-授权模型](./21-授权模型.md)）。**不要只在前端做字段过滤。**

## 三、错误契约

错误响应是 RFC 7807 `ProblemDetails`，关键信息在**顶层扩展属性**上：

```json
{
  "status": 400,
  "title": "导出行数需在 1~500 之间。",
  "type": "about:blank",
  "code": "INVALID_EXPORT_KEYS",
  "message": "导出行数需在 1~500 之间。",
  "fieldErrors": { "CODE": ["不能为空"] },
  "traceId": "...",
  "correlationId": "...",
  "clientId": "...",
  "moduleId": 1602
}
```

约定：

- **前端按 `code` 分支，`message` 只用于展示**。`message` 文案会变，`code` 是稳定契约。
- `fieldErrors` 用于表单字段级错误提示，键是字段名。
- `traceId` / `correlationId` / `clientId` / `moduleId` 是排障关联键，报障时带上它们能直接串起
  前后端日志与审计记录（见 [31-运行日志与诊断](./31-运行日志与诊断.md)）。
- **四个键现在是同一个值**：响应体、响应头 `X-Correlation-Id`、运行日志的 `correlation=` 与
  审计的 `CORRELATION_ID` 都取自 `RequestContext`（异常出口此前取 `TraceIdentifier`，已统一）。
  界面侧有"复制报障编号"入口；渲染期错误没有请求，编号由前端本地生成后随上报回传，
  因此**用户给的号一定能在服务端日志里搜到**。口径见
  [`docs/decisions/ADR-025`](../decisions/ADR-025-关联键单一真源与日志脱敏接入.md)。
- **诊断包是另一个入口**：`GET /api/v1/logs/bundle` 返回 zip（日志 + 环境元数据），
  需要模块 `2313`（日志管理）的 `CanSetup` 权限；读日志与诊断信息只需同一模块的 `CanBrowse`。

稳定错误码（`EOS.API/Errors/ApiErrorCodes.cs`）：

| 类别 | 码 |
|---|---|
| 通用 | `INVALID_ARGUMENT`、`INVALID_MODEL`、`NOT_FOUND`、`UNAUTHORIZED`、`FORBIDDEN`、`INTERNAL_ERROR` |
| 登录 | `LOGIN_INVALID_INPUT`、`LOGIN_USER_NOT_FOUND`、`LOGIN_INVALID_PASSWORD`、`LOGIN_DISABLED`、`LOGIN_LOCKED` |

域内还会定义更细的码（如 `INVALID_EXPORT_KEYS`、`INVALID_FORM_MODE`、`INVALID_RECORD_KEY`）。
新增码时同步加进 `ApiErrorCodes` 或就近定义，不要散落成裸字符串。

## 四、分页

列表接口统一返回四项：`items`、`total`、`page`、`pageSize`。各域沿用同域已有模型的命名，
例如工作台用 `WorkbenchData(Rows, Total, Page, PageSize)`，
管理类接口用 `XxxPageResult(Items, Total, Page, PageSize)`。

请求参数统一用查询串：`page`（从 1 起）、`pageSize`。导出类接口不分页，但会限制单次行数上限
（超出时返回 400 与明确错误码）。

## 五、数据约定

| 项 | 约定 |
|---|---|
| 主键 / ID | **前端一律按字符串处理**。复合主键按主键值的 JSON 数组编码传递，如 `["A","B"]` |
| 金额、数量 | 精度**以服务端为准**，前端不做二次舍入 |
| 日期时间 | 传输与格式化以服务端为准 |
| 内部行键 | `__rowKey` 之类的行键与显示列元数据分离，不进入显示列 |

排序参数约定：单列用 `sortField` / `sortDirection`，多列用 `sortFields` / `sortDirections`
（同名参数以逗号分隔）。**排序字段必须来自服务端定义的可见列**，不接受任意列名。

## 六、前端怎么调

```
页面组件 → TanStack Query → src/services/api（类型化 client）→ HTTP → EOS.API
```

- **组件不直接 `fetch`**；请求、缓存与失效交给 TanStack Query。
- `src/services/api/` 下按 transport 分层：`httpTransport` 负责真实 HTTP，
  `mockTransport` 用于无后端时的开发，`client` 是二者之上的类型化门面。
- **文件上传走 `client.postForm`（请求体是 `FormData`），不要自己 `fetch`**：`httpTransport` 对
  `FormData` **不设 `Content-Type`**（boundary 得由浏览器生成，手写会把 boundary 一起写错），
  这样上传与其余请求共用鉴权、`X-Correlation-Id` 与错误体解码——绕开传输层手写一遍 `fetch`，
  漏掉的正是这三样（基本资料导入的 `.csv` / `.xlsx` 上传即此路径）。
- 错误统一走 `client` 的解析，拿到 `code` 后再决定提示或字段级错误回填。
- **跨边界的枚举 / 编码字符串只允许一份定义，输出也必须走它**：模型用途在库里是
  `'CHAT'` / `'EMBEDDING'`（目录层的 `KindCode`），前端也按这两个字面量比对；接口曾用枚举默认的
  `ToString()` 下发（`"Chat"` / `"Embedding"`），于是前端把每一条模型都过滤掉了——
  **界面显示"这家还没有模型"，而库里明明有**。这类错不会有任何编译错误：两端各写一遍字面量时，
  对不上的那天只有运行时的静默错配。所以 ① 输出与入库共用同一个常量/函数；
  ② 配一条**回环用例**（编码 → 下发 → 解析回来）钉住它；
- **动作类端点要区分"对哪一类做这件事"时用查询串，不要塞进 body**：`client` 的 `post`
  与 `get` / `postFile` 一样支持 `query`（例：取消当前模型要说明是对话还是嵌入）。
  同一个参数在 GET 与 POST 两处用两种方式读，早晚有一处读漏；
  而读漏的那一处通常**不报错**，只是作用于另一个对象（清错了另一条"当前"）。

## 七、写接口的两个额外要求

### 幂等

重复提交（用户连点、网络重试）不应产生重复数据。列表外的写接口要有幂等保护，
已有实现在工作台命令路径上（幂等键 + 状态判定）。

### 并发

并发修改同一单据时不能静默覆盖。写请求带上记录的当前状态或时间戳，
服务端检测到已被他人改动时返回冲突，由前端提示刷新。

> 工作台侧的完整实现（幂等键、并发判定、事务与审计）见 [40-统一工作台](./40-统一工作台.md)。

## 八、改动 API 时的清单

1. 服务端：路由 + 权限门 + 参数校验 + 错误码（用 `ApiProblem`，不要手拼响应体）；
2. 前端：`services/api` 加方法 + 类型；
3. **同步更新本手册对应篇目**（模块、路由、错误码变更都属必同步项），
   并运行 `pwsh docs/guide/_tools/check-freshness.ps1 -Strict`；
4. 若新增/变更了对外可见的只读端点，确认端到端冒烟仍然全绿。

> 只读端点带**路径参数**时，冒烟脚本（`EOS.API.Tests/SmokeEndpoints.ps1`）要能给它填上**真实存在**的标识：
> 已映射 `table` / `field`，其余名字会退化成占位 `1`——那样端点是在"不存在的键"上空跑成功，
> 逐行读列的代码根本没执行，真正的 500 被静默放过。新增这类参数时到脚本的 `$pathParamValues` 里补一条。

## 收口（迁移 319~329）：模块级契约的三处变化

- **"能不能写"改由写名单判定**：`MODULES` 的 `NEW_URL` / `MODI_URL` / `HELP_URL` 三个路由列已删除，
  `M_URL` 只表示**承载页**（空 = 目录节点或未声明，单据模块默认落统一工作台）。外部若按旧列判权限需跟着改。
- **呈现配置随 `/form-definition` 下发**：`openMode`（`TAB` 本页签 / `NEWTAB` 新页签 / `DIALOG` 弹窗）、
  `dialogWidth`、`dialogHeight` 与 `tabs[].columns`（一行几列，**页签级**）。定义里**没有**模块级的 `columns`
  兜底段；历史快照缺页签列数时两端统一按 4 列兜底。非法打开方式与越界尺寸即 400，不会撞库约束变 500。
- **`/admin/menus` 的模块投影收口**：`MenuAdminModule` 不再返回 `FORM_OPEN_MODE` / `FORM_DIALOG_WIDTH` /
  `FORM_DIALOG_HEIGHT`（那三个字段只服务已删除的「统一表单」页签），新增 `REMARK`（模块备注，空串落 `NULL`）。
  模块管理只读写模块自身的字段。

## 收口（迁移 334/335）：`WorkbenchDefinition` 去掉两个归属列探测位

`hasOwnerColumn` / `hasOwnerGroupColumn` 两个布尔位（原先由 `sys.columns` 探测主表有没有 `OWNER` /
`OWNER_G` 列，供 `EXEC_TAG` 的 B/C/D/E 数据范围过滤使用）已随行归属三列全库下线一并删除。
响应里不再出现这两个字段；`execTag` 仍在，但只有 `Z` / `A` / 空 表示"无附加范围"，
`B`/`C`/`D`/`E` 与未知取值一律拒绝（fail-closed，不降级为全量查询）。前端若按这两个布尔位做过显隐，可删；
要按条件限制行可见性用 `DATA_FILTER`（见 [21-授权模型](./21-授权模型.md)）。

## 收口（迁移 340）：模块投影新增只读的节点形态

`MenuAdminModule` 新增只读字段 `NODE_KIND`（`WORKBENCH` / `CUSTOMPAGE` / `DIRECTORY`）：由服务端按承载页与
主表算出，**保存时忽略**（库里没有这一列，事实源是 SQL 视图 `dbo.V_MODULE_NODE`）。模块管理页据此决定
露出哪些配置项，见 [70-后台配置面总览](./70-后台配置面总览.md) 第二节。外部若要判"这个模块是不是工作台模块"，
用后端 `ModuleRouteValidator.ResolveKind` 或那个视图，不要自己拼 `M_URL` + `MASTER_TABLE` 的组合谓词
（口径见 [10-元数据模型](./10-元数据模型.md) 第一节）。

## 收口（迁移 341）：模块投影卸下分组列，分组筛选参数改名

- `MenuAdminModule` **不再返回**那 15 个分组字段（`GROUP1..5` / `GROUP_EXP1..5` / `GROUP_DESC1..5`）：
  列表分组已独立成 `MODULE_GROUPS` 表，配置面是 2315「模块分组」，模块记录上不再有这些列；
- 工作台的四个查询端点（`/records`、`/query`、`/export`、`/export-selected`）与
  `GET /navigation/{moduleId}/groups/{groupId}/values` 的分组参数由 **`groupIndex`（1~5 序号）改为
  `groupId`**（`MODULE_GROUPS.GROUP_ID`）。序号只决定下拉顺序，拿它当身份会让调序/删除后的旧链接
  指到另一个表达式上；`NavigationItem.groups[]` 的元素同步由 `{ index, description }` 改为
  `{ groupId, description }`（见 [20-认证与会话](./20-认证与会话.md)）。外部若按 `groupIndex` 拼过列表链接，需跟着改；
- 读不到该编号仍是 403 `GROUP_EXP_UNSUPPORTED`（与"表达式超出受控子集"共用同一响应，不区分两者——
  区分等于泄露别处配置的存在性）；细节见 [40-统一工作台](./40-统一工作台.md) 末节；
- 分组的**配置面**是 2315「模块分组」的定制页 `/admin/module-groups`（`ModuleGroupAdminController`，
  `api/v1/admin/module-groups`，读 `CanBrowse` / 写 `CanSetup`）：`POST/PUT/DELETE` 的校验失败一律
  400 `INVALID_ARGUMENT`，消息指名到列（如"列 X 不在主表 Y 的分组可用字段里"），便于界面直接显示。
  2315 是**自定义承载页**，不在统一表单写名单里——那份名单是写路径的门，只收工作台模块
  （见 [70-后台配置面总览](./70-后台配置面总览.md) 第二节）。
