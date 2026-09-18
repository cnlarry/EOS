import { memo, useCallback, useContext, useLayoutEffect, useMemo, useRef } from 'react'
import {
  UNSAFE_LocationContext,
  UNSAFE_NavigationContext,
  createPath,
  parsePath,
  useNavigationType,
  useRoutes,
  type RouteObject,
  type To,
} from 'react-router-dom'
import { FormBreadcrumbContext, type FormBreadcrumb } from './FormBreadcrumbContext'
import { PageBreadcrumbContext, type PageBreadcrumb } from './PageBreadcrumbContext'
import { WorkspaceTabContext } from './workspaceDirty'
import type { TabCrumb, TabCrumbPatch } from './workspaceTabs'

interface WorkspacePanelProps {
  /** 标签标识 */
  id: string
  /** 该面板自身的地址：路径 + 查询串 */
  url: string
  /** 工作区路由表 */
  routes: RouteObject[]
  /** 是否为当前活动标签。非活动面板常驻不卸载，仅隐藏，以保留分页/选中/滚动/草稿等本地状态 */
  active: boolean
  /** 该标签自己的面包屑值 */
  crumb: TabCrumb
  onCrumbChange: (tabId: string, patch: TabCrumbPatch) => void
  /** 隐藏标签内部发起导航时，把目标地址写回该标签自身 */
  onTabUrl: (tabId: string, url: string) => void
}

/**
 * 单个标签的渲染面板：按自身地址求值路由，并把 location 上下文覆写为该面板自己的地址。
 *
 * 覆写不可省：useLocation / useSearchParams 默认读浏览器真实地址，隐藏面板若读到活动标签的
 * 地址并写回，会跨标签互相污染筛选、排序、分组参数。
 *
 * 隐藏标签另需把导航收口：隐藏态收不到用户事件，但 effect 与异步回调仍可能调用 navigate，
 * 直接放行会改掉浏览器地址与历史，因此把 push/replace 改写为该标签自身地址、go 置空。
 *
 * 用 memo 包一层：外壳因时钟/侧栏/菜单搜索等自身状态重渲染时，标签内容不必跟着重渲染；
 * 标签地址变化、或其消费的 context 变化时仍会正常更新。
 */
export const WorkspacePanel = memo(function WorkspacePanel({ id, url, routes, active, crumb, onCrumbChange, onTabUrl }: WorkspacePanelProps) {
  const navigationType = useNavigationType()
  const realNavigation = useContext(UNSAFE_NavigationContext)
  const panelRef = useRef<HTMLDivElement | null>(null)
  const scrollTopRef = useRef(0)

  const location = useMemo(() => {
    const parsed = parsePath(url)
    return { pathname: parsed.pathname || '/', search: parsed.search ?? '', hash: parsed.hash ?? '', state: null, key: 'tab' }
  }, [url])
  const locationContext = useMemo(() => ({ location, navigationType }), [location, navigationType])

  const containedNavigator = useMemo(() => ({
    ...realNavigation.navigator,
    go: () => {},
    push: (to: To) => onTabUrl(id, typeof to === 'string' ? to : createPath(to)),
    replace: (to: To) => onTabUrl(id, typeof to === 'string' ? to : createPath(to)),
  }), [realNavigation, id, onTabUrl])
  // 活动标签沿用真实导航上下文（值相同，不会引起子树额外渲染），隐藏标签换成收口后的导航器
  const navigationContext = useMemo(
    () => (active ? realNavigation : { ...realNavigation, navigator: containedNavigator }),
    [active, realNavigation, containedNavigator],
  )

  const setFormBreadcrumb = useCallback((value: FormBreadcrumb | null) => onCrumbChange(id, { form: value }), [id, onCrumbChange])
  const setPageBreadcrumb = useCallback((value: PageBreadcrumb | null) => onCrumbChange(id, { page: value }), [id, onCrumbChange])
  const formValue = useMemo(() => ({ breadcrumb: crumb.form, setBreadcrumb: setFormBreadcrumb }), [crumb.form, setFormBreadcrumb])
  const pageValue = useMemo(() => ({ breadcrumb: crumb.page, setBreadcrumb: setPageBreadcrumb }), [crumb.page, setPageBreadcrumb])

  // 显式传入本面板地址，匹配不依赖浏览器地址，隐藏面板也按自己的 URL 渲染
  const element = useRoutes(routes, location)

  // 恢复该标签的滚动位：隐藏期滚动容器的 scrollTop 会归零，位置由滚动回调持续留存；
  // 同时补发一次 resize，令表格虚拟窗口按新的可视高度重算（隐藏期它被判为不可滚而关闭）。
  useLayoutEffect(() => {
    if (!active) return
    const panel = panelRef.current
    if (!panel) return
    panel.scrollTop = scrollTopRef.current
    window.dispatchEvent(new Event('resize'))
  }, [active])

  return (
    <div
      className="erp-tab-panel"
      hidden={!active}
      ref={panelRef}
      onScroll={(event) => { scrollTopRef.current = event.currentTarget.scrollTop }}
    >
      <UNSAFE_LocationContext.Provider value={locationContext}>
        <UNSAFE_NavigationContext.Provider value={navigationContext}>
          <WorkspaceTabContext.Provider value={id}>
            <div className="container-fluid px-3 px-lg-4">
              <FormBreadcrumbContext.Provider value={formValue}>
                <PageBreadcrumbContext.Provider value={pageValue}>{element}</PageBreadcrumbContext.Provider>
              </FormBreadcrumbContext.Provider>
            </div>
          </WorkspaceTabContext.Provider>
        </UNSAFE_NavigationContext.Provider>
      </UNSAFE_LocationContext.Provider>
    </div>
  )
})
