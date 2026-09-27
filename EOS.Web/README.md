# EOS.Web

`EOS.Web` 是 EOS ERP 的前端工作台：React + TypeScript + Vite 实现的经典桌面式 ERP 界面，
通过 `EOS.API` 访问数据。

## 技术栈

- React + TypeScript + Vite；
- React Router；
- TanStack Query；
- Tabler / `@tabler/icons-react`；
- React Hook Form + Zod；
- Vitest + Testing Library；
- ESLint（Oxlint）与 Prettier。

## 目录结构

```text
src/
├─ app/                         Provider、路由和应用入口
├─ components/
│  ├─ ui/                       基础 UI 组件
│  ├─ layout/                   AppShell、页头、导航
│  └─ common/                   通用 ERP 组件（电子表格、选择器、列选择等）
├─ features/
│  ├─ auth/                     登录、会话恢复、路由守卫
│  ├─ dashboard/                首页
│  ├─ document-workbench/       通用单表/主子表工作台（统一表单）
│  ├─ field-admin/              数据表/字段维护
│  ├─ menu-admin/               模块菜单维护
│  ├─ user-admin/               用户与权限管理
│  └─ ...                       各领域页面
├─ services/api/                类型化 API client
└─ types/
```

## 数据流

```text
页面组件 → TanStack Query → services/api 类型化 client → HTTP → EOS.API
```

组件不直接 `fetch`；服务端数据、缓存与失效交给 TanStack Query；列表统一使用
元数据驱动的通用表格组件（选择、排序、列宽拖拽、复制、键盘导航为默认能力）。

## 路由

- `/login`、`/dashboard`；
- `/workbench/:moduleId`：通用工作台；
- `/workbench/:moduleId/new|edit/:key|view/:key|copy`：统一表单单据操作；
- `/admin/*`：系统管理（用户、权限、菜单、表字段、报表设置、用户组）；
- `/reports`、`/search-center`、`/detail-query/:moduleId`：报表与查询中心；
- `/settings/*`：个人资料与系统参数。

路由保护分认证与权限两级；前端权限只改善体验，后端仍会对每个请求重新授权。

## 本地开发

```powershell
npm install
npm run dev        # 开发服务器把 /api 代理到 EOS.API（默认 http://localhost:5261）
```

质量检查：

```powershell
npm run lint
npm run build
npm test
```
