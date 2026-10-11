import { describe, expect, it } from 'vitest'
import type { NavigationItem } from '../../features/auth/types'
import { searchMenu } from './menuSearch'

/** 夹具刻意包含三种节点：纯目录分组（无落点）、叶子、以及"既是分组又有自己页面"的模块 */
const navigation: NavigationItem[] = [
  { id: 'dashboard', label: '首页', route: '/dashboard', icon: 'dashboard' },
  {
    id: 'module-23',
    label: '系统管理',
    icon: 'settings',
    children: [
      {
        id: 'module-2311',
        label: '数据表维护',
        icon: 'settings',
        children: [
          { id: 'module-2313', label: '日志管理', alias: 'LOGS', route: '/admin/logs', icon: 'settings', moduleId: 2313 },
          { id: 'module-129802', label: 'BOM展开表', route: '/bom-expand', icon: 'settings', moduleId: 129802 },
          { id: 'module-23012', label: '接口日志', route: '/admin/interface-logs', icon: 'settings', moduleId: 23012 },
        ],
      },
      {
        id: 'module-230101',
        label: '模块管理',
        route: '/admin/menus',
        icon: 'settings',
        moduleId: 230101,
        children: [
          { id: 'module-2302', label: '数据表、字段维护', route: '/admin/tables', icon: 'settings', moduleId: 2302 },
        ],
      },
    ],
  },
]

describe('searchMenu 菜单搜索', () => {
  it('按模块编号命中叶子并给出完整面包屑', () => {
    expect(searchMenu(navigation, '129802')).toEqual([
      expect.objectContaining({ breadcrumb: '系统管理 / 数据表维护 / BOM展开表' }),
    ])
  })

  it('既是分组又有自己页面的模块同样按编号命中', () => {
    // ADR-033：模块管理与它的父目录不再是同一个节点——精确输入模块自己的编号只命中它
    expect(searchMenu(navigation, '230101').map((hit) => hit.breadcrumb)).toEqual([
      '系统管理 / 模块管理',
    ])
  })

  it('编号前缀命中：目录不参与，同前缀的叶子仍搜得到', () => {
    const breadcrumbs = searchMenu(navigation, '2301').map((hit) => hit.breadcrumb)
    expect(breadcrumbs).toContain('系统管理 / 模块管理')
    expect(breadcrumbs).toContain('系统管理 / 数据表维护 / 接口日志')
  })

  it('纯目录分组没有落点，不参与匹配（按编号与按名称都不命中）', () => {
    expect(searchMenu(navigation, '2311')).toEqual([])
    expect(searchMenu(navigation, '数据表维护')).toEqual([])
  })

  it('按显示名与别名命中，且大小写不敏感', () => {
    expect(searchMenu(navigation, 'BOM')).toHaveLength(1)
    expect(searchMenu(navigation, 'logs')).toHaveLength(1)
    expect(searchMenu(navigation, '首页')).toEqual([expect.objectContaining({ breadcrumb: '首页' })])
  })

  it('空查询无结果，命中数受 limit 限制', () => {
    expect(searchMenu(navigation, '   ')).toEqual([])
    expect(searchMenu(navigation, '0', 2)).toHaveLength(2)
  })
})
