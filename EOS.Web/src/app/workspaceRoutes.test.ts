import { matchRoutes } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { WORKSPACE_ROUTES } from './workspaceRoutes'

describe('WORKSPACE_ROUTES', () => {
  it('工作区各地址都能匹配到路由', () => {
    const paths = [
      '/',
      '/dashboard',
      '/workbench/1606',
      '/workbench/1606/new',
      '/workbench/1606/edit/A001',
      '/workbench/1606/view/A001',
      '/workbench/1606/copy',
      '/reports/1606',
      '/report-center',
      '/search-center',
      '/admin/tables',
      '/admin/tables/PRODUCT/fields',
      '/admin/users',
      '/admin/menus',
      '/admin/module-groups',
      '/admin/module-buttons',
      '/admin/module-actions',
      '/my-tasks',
      '/jobs',
      '/import',
      '/print/1606',
      '/car-summary',
      '/workflow/design',
      '/workflow/monitor',
      '/settings/profile',
      '/settings/PRODUCT',
      '/fallback/modules/1606',
      // 产品 BOM（1204）的三个定制视图：列表 / 反查 / 结构树
      '/bom/product',
      '/bom/product/new',
      '/bom/product/where-used/H15-05SZ-200',
      '/bom/product/H15-05SZ-200/01/edit',
      '/bom/product/H15-05SZ-200/01/tree',
      '/bom/product/H15-05SZ-200/01/compare/02',
    ]
    for (const path of paths) {
      expect(matchRoutes(WORKSPACE_ROUTES, path), `${path} 未匹配到路由`).not.toBeNull()
    }
  })

  it('未定义地址落到末位兜底路由（渲染 404 面板，不是空白面板）', () => {
    const matched = matchRoutes(WORKSPACE_ROUTES, '/不存在的页面')
    expect(matched).not.toBeNull()
    expect(matched?.at(-1)?.route.path).toBe('*')
  })

  // ADR-033 §11："只有 230103 权限的人进不去 230104，这是拆分的收益，要有用例钉住。"
  // 这里钉客户端一侧：三个配置面各自挂**自己**的读权限门，且链上不出现别的配置面的门
  // （串门 = 拆分的收益被抵消）。服务端一侧由 ConfigFacePermissionAnchorsTests 钉住——服务端才是权威。
  it('三个配置面各自挂自己的读权限门，互不串门', () => {
    const faces = [
      { path: '/admin/module-groups', moduleId: 230102 },
      { path: '/admin/module-buttons', moduleId: 230103 },
      { path: '/admin/module-actions', moduleId: 230104 },
    ]

    for (const face of faces) {
      const matched = matchRoutes(WORKSPACE_ROUTES, face.path)
      expect(matched, `${face.path} 未匹配到路由`).not.toBeNull()

      const permissions = (matched ?? [])
        .map((entry) => (entry.route.element as { props?: { permission?: unknown } } | undefined)?.props?.permission)
        .filter((value): value is string => typeof value === 'string')

      expect(permissions, `${face.path} 没有挂自己的读权限门`).toContain(`fallback-module.${face.moduleId}.read`)
      for (const other of faces.filter((item) => item.moduleId !== face.moduleId)) {
        expect(permissions, `${face.path} 挂上了别的配置面的门`).not.toContain(`fallback-module.${other.moduleId}.read`)
      }
    }
  })
})
