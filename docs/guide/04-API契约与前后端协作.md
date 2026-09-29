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
  需要模块 11 的 `CanSetup` 权限；读日志与诊断信息只需 `CanBrowse`。

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
- 错误统一走 `client` 的解析，拿到 `code` 后再决定提示或字段级错误回填。

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
