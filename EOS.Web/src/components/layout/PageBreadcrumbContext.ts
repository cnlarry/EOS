import { createContext, useContext } from 'react'

/** 定制页子页面包屑（由子页上抛，AppShell 渲染；如 系统管理 > 用户组管理 > 采购 组权限）。 */
export interface PageBreadcrumb {
  /** 面包屑前缀层级（不含叶子标题；叶子标题由页面 h1 承担）。 */
  leads: { label: string; to?: string }[]
  /** 页面标题（h1 与面包屑叶子）。 */
  title: string
}

export interface PageBreadcrumbContextValue {
  breadcrumb: PageBreadcrumb | null
  setBreadcrumb: (breadcrumb: PageBreadcrumb | null) => void
}

export const PageBreadcrumbContext = createContext<PageBreadcrumbContextValue>({
  breadcrumb: null,
  setBreadcrumb: () => {},
})

export function usePageBreadcrumb() {
  return useContext(PageBreadcrumbContext)
}
