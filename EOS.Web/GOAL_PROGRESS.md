# EOS.Web 工程交付就绪目标 - 进度记录

> 目标：整体行覆盖率 ≥ 60%；核心文件行覆盖率 ≥ 70%；lint/build/test 全绿。
> 边界：只改 EOS.Web/，不改业务语义，不触碰仓库根 IM 未提交改动，不 commit/push。

## 基线（2026-08-08）

- 覆盖率：Statements 25.16% / Branches 23.34% / Functions 23.38% / **Lines 27.73%**（1975 语句，1518 行）
- 核心文件基线：
  - useColumnResize.ts：37.06%（语句）/ 25.58%（分支）
  - ErpTable.tsx：67.31%（语句）/ 63.74%（分支）
  - ErpColumnSelector.tsx：76.84%
  - ErpQueryBuilder.tsx：80.95%
  - DocumentWorkbenchPage / FormEditorPage / FieldEditorModal / AppShell / PurchaseOrdersPage / FieldAdminPage / UserAdminPage / LoginPage / AuthProvider / mockTransport / client / httpTransport：0%
- lint：0 error，8 条 fast-refresh warning
- build：通过，主 chunk 560.83 kB（>500kB 告警）
- test：13 文件 / 77 测试全绿

## 轮次记录

### 轮次 1：覆盖率基建 + 首批测试（API/认证/校验/渲染器/列宽）

- 安装 `@vitest/coverage-v8`（devDependency），`vitest.config.ts` 增加 v8 coverage 配置，`package.json` 新增 `test:coverage` 脚本。
- 新增 9 个测试文件：`client`、`httpTransport`、`mockTransport`、`AuthProvider`、`LoginPage`、`RouteGuards`、`formValidation`、`FormFieldRenderer`、`useColumnResize`。
- 修复 React 19 + jsdom 下受控 checkbox 需用 `click` 驱动 onChange 的测试写法。

### 轮次 2：核心页面测试（DocumentWorkbenchPage / FormEditorPage / FieldEditorModal）

- `DocumentWorkbenchPage`：定义/主表加载与错误态、行选联动明细、导出（全部/所选/失败弹窗）、新增/编辑跳转、搜索与排序参数、高级查询应用/清空、选择列保存、字段设置弹窗、紧凑行高、页码钳制、空数据、权限链接。
- `FormEditorPage`：新增/编辑模式、默认值初始化、客户端校验、保存成功回列表、400 字段错误、明细增删、选择器回填、beforeunload 脏提示（桩 confirm）。
- `FieldEditorModal`：新增/编辑、加载态/失败态、全分区控件编辑并保存、tables/modules 端点、保存失败、load 返回 null。

### 轮次 3：剩余页面与小组件

- 新增 `AppShell`、`PurchaseOrdersPage`、`FieldAdminPage`、`UserAdminPage`、`ProfilePage`、`TableAdminPage`、`DashboardPage`、`LegacyModulePage`、`ErrorPage`、`PageHeader`、`SearchPanel`、`AsyncState`、`App`、`purchaseOrders api` 测试。

### 轮次 4：lint 0 警告（fast-refresh 全部消除）

- 拆出 `queryCondition.ts`（QueryCondition/queryOperators/emptyQueryCondition）、`formFieldKind.ts`（inputKind）、`authContext.ts`（AuthContext/useAuth），消除 ErpQueryBuilder/FormFieldRenderer/AuthProvider 的混导警告。
- 拆出 `routeElements.tsx` 与 `suspense.tsx`，router.tsx 只导出 `router`，消除 fast-refresh 警告。
- 结果：`npm run lint` 0 error 0 warning。

### 轮次 5：路由级代码分割

- 新增 `lazyRoutes.tsx`，LoginPage/DashboardPage/采购/字段维护/用户/个人设置等改为 `React.lazy`，`routeElements.tsx` 内工作台/表单/字段页按需加载，路由元素包 `Suspense`。
- 主 chunk 560.83 kB → 327.76 kB，构建不再出现 >500kB 告警。

## 最终状态（2026-08-08）

- lint：0 error 0 warning
- test：39 文件 / 247 测试全绿
- coverage：Statements 82.65% / Branches 77.13% / Functions 81.59% / **Lines 86.65%**
- 核心文件行覆盖率：DocumentWorkbenchPage 72.18 / FormEditorPage 82.95 / FieldEditorModal 93.5 / AppShell 96.05 / useColumnResize 99.48 / ErpTable 76.64 / ErpColumnSelector 81.33 / ErpQueryBuilder 80 / PurchaseOrdersPage 95.45 / FieldAdminPage 90.74 / UserAdminPage 87.75 / LoginPage 96.29 / AuthProvider 98.03 / mockTransport 97.77 / client 100 / httpTransport 100
- build：通过，主 chunk 327.76 kB（无 >500kB 告警）
- 边界遵守：只改动 EOS.Web/（package.json 仅新增测试相关 devDependency）；未改 EOS.API；未触碰仓库根 IM 改动；未执行 git commit/push
