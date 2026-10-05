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
      '/my-tasks',
      '/jobs',
      '/import',
      '/print/1606',
      '/bom-expand',
      '/car-summary',
      '/workflow/design',
      '/workflow/monitor',
      '/settings/profile',
      '/settings/PRODUCT',
      '/fallback/modules/1606',
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
})
