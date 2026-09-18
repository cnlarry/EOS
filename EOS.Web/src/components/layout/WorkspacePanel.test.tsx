import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useLocation, useSearchParams, type RouteObject } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { WorkspacePanel } from './WorkspacePanel'
import { EMPTY_TAB_CRUMB } from './workspaceTabs'

/** 页面内探针：读取自己的路由上下文，并提供一次会写地址的参数变更 */
function Probe() {
  const location = useLocation()
  const [params, setParams] = useSearchParams()
  return (
    <div>
      <span data-testid="inner-location">{location.pathname}{location.search}</span>
      <span data-testid="inner-page">{params.get('page') ?? '-'}</span>
      <button type="button" onClick={() => setParams({ page: '9' })}>改页码</button>
    </div>
  )
}

function LocationProbe() {
  const location = useLocation()
  return <span data-testid="browser-location">{location.pathname}{location.search}</span>
}

const routes: RouteObject[] = [{ path: 'a', element: <Probe /> }, { path: 'b', element: <Probe /> }]

function renderPanels() {
  const onTabUrl = vi.fn()
  render(
    <MemoryRouter initialEntries={['/b?page=1']}>
      <LocationProbe />
      <WorkspacePanel
        id="t1"
        url="/a?page=3"
        routes={routes}
        active={false}
        crumb={EMPTY_TAB_CRUMB}
        onCrumbChange={vi.fn()}
        onTabUrl={onTabUrl}
      />
      <WorkspacePanel
        id="t2"
        url="/b?page=1"
        routes={routes}
        active
        crumb={EMPTY_TAB_CRUMB}
        onCrumbChange={vi.fn()}
        onTabUrl={vi.fn()}
      />
    </MemoryRouter>,
  )
  return { onTabUrl }
}

describe('WorkspacePanel', () => {
  it('隐藏面板按自身地址渲染，不读活动标签的地址', () => {
    renderPanels()
    const inner = screen.getAllByTestId('inner-location')
    // 顺序：t1（隐藏，/a?page=3）在前，t2（活动，/b?page=1）在后
    expect(inner[0]).toHaveTextContent('/a?page=3')
    expect(inner[1]).toHaveTextContent('/b?page=1')
    expect(screen.getAllByTestId('inner-page')[0]).toHaveTextContent('3')
  })

  it('隐藏面板内的导航不改浏览器地址与历史，只写回自身地址', () => {
    const { onTabUrl } = renderPanels()
    fireEvent.click(screen.getAllByText('改页码')[0])
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/b?page=1')
    expect(onTabUrl).toHaveBeenCalledWith('t1', '/a?page=9')
  })

  it('活动面板内的导航按正常语义走浏览器地址', () => {
    renderPanels()
    fireEvent.click(screen.getAllByText('改页码')[1])
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/b?page=9')
  })
})
