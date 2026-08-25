import { createContext, useContext } from 'react'

/** 统一表单当前打开单据的面包屑信息（由 FormEditorPage 上抛，AppShell 渲染）。 */
export interface FormBreadcrumb {
  /** 模块标题（如「采购订单」）。 */
  moduleTitle: string
  /** 单据编号：view/edit 为具体单号，new 为 null（新增不显示单号）。 */
  docNo: string | null
}

export interface FormBreadcrumbContextValue {
  breadcrumb: FormBreadcrumb | null
  setBreadcrumb: (breadcrumb: FormBreadcrumb | null) => void
}

export const FormBreadcrumbContext = createContext<FormBreadcrumbContextValue>({
  breadcrumb: null,
  setBreadcrumb: () => {},
})

/** 读取/设置当前表单面包屑（Provider 由 AppShell 以自身 state 提供）。 */
export function useFormBreadcrumb() {
  return useContext(FormBreadcrumbContext)
}