import { act, renderHook } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import type { ReactNode } from 'react'
import { WorkspaceNavContext } from '../../components/layout/WorkspaceNavContext'
import { WorkspaceTabContext } from '../../components/layout/workspaceDirty'
import {
  DEFAULT_DIALOG_HEIGHT,
  DEFAULT_DIALOG_WIDTH,
  normalizeFormOpenMode,
  resolveDialogSize,
  useOpenWorkbenchForm,
} from './formOpenMode'

describe('formOpenMode', () => {
  it('未配置或无法识别的取值一律回落本页签', () => {
    expect(normalizeFormOpenMode(null)).toBe('TAB')
    expect(normalizeFormOpenMode(undefined)).toBe('TAB')
    expect(normalizeFormOpenMode('')).toBe('TAB')
    expect(normalizeFormOpenMode('POPUP')).toBe('TAB')
  })

  it('识别新页签与弹窗（大小写与首尾空白不敏感）', () => {
    expect(normalizeFormOpenMode('TAB')).toBe('TAB')
    expect(normalizeFormOpenMode(' newtab ')).toBe('NEWTAB')
    expect(normalizeFormOpenMode('dialog')).toBe('DIALOG')
  })

  it('弹窗尺寸：配了就用配的，未配置或非正数回落默认', () => {
    expect(resolveDialogSize(900, 640)).toEqual({ width: 900, height: 640 })
    expect(resolveDialogSize(null, null)).toEqual({ width: DEFAULT_DIALOG_WIDTH, height: DEFAULT_DIALOG_HEIGHT })
    expect(resolveDialogSize(0, -10)).toEqual({ width: DEFAULT_DIALOG_WIDTH, height: DEFAULT_DIALOG_HEIGHT })
  })

  it('NEWTAB：已存在的单开对照窗口，新增/复制留在模块标签里', () => {
    const openTab = vi.fn()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter>
        <WorkspaceNavContext.Provider value={{ openTab, closeActiveTab: vi.fn() }}>
          <WorkspaceTabContext.Provider value="t1">{children}</WorkspaceTabContext.Provider>
        </WorkspaceNavContext.Provider>
      </MemoryRouter>
    )
    const { result } = renderHook(() => useOpenWorkbenchForm(), { wrapper })

    act(() => result.current('/workbench/1405/view/CHTZ/DD1', { openMode: 'NEWTAB', state: { navIndex: 0 } }))
    expect(openTab).toHaveBeenCalledWith('/workbench/1405/view/CHTZ/DD1', {
      asRecord: true,
      openedFrom: 't1',
      state: { navIndex: 0 },
    })

    openTab.mockClear()
    // 新增还没有主键 ⇒ 没有单据身份，不开对照窗口（否则每次新增都多一个标签）
    act(() => result.current('/workbench/1405/new', { openMode: 'NEWTAB' }))
    expect(openTab).not.toHaveBeenCalled()
  })
})
