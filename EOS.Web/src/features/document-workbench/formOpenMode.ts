import { useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'

/**
 * 统一表单的打开方式（服务端 `MODULES.FORM_OPEN_MODE`，随定义下发）：
 *  - `TAB`：在当前工作区标签内打开（默认）；
 *  - `NEWTAB`：在工作区标签栏新开一个标签；
 *  - `DIALOG`：以固定尺寸的窗体打开——**地址与路由不变**，由表单页自己按
 *    `dialogWidth` / `dialogHeight` 把内容装进窗体（见 `FormEditorPage`）。
 *    这样弹窗只是"同一个表单页的另一种容器"，不引入第二套渲染路径。
 */
export type FormOpenMode = 'TAB' | 'NEWTAB' | 'DIALOG'

/** 弹窗未配尺寸时的兜底窗体大小（px）；与锚定在路由上的表单页同源。 */
export const DEFAULT_DIALOG_WIDTH = 720
export const DEFAULT_DIALOG_HEIGHT = 560

/** 打开方式规范化：未配置或无法识别一律回落本页签（呈现开关不该让表单打不开）。 */
export function normalizeFormOpenMode(value: string | null | undefined): FormOpenMode {
  switch ((value ?? '').trim().toUpperCase()) {
    case 'NEWTAB':
      return 'NEWTAB'
    case 'DIALOG':
      return 'DIALOG'
    default:
      return 'TAB'
  }
}

/** 弹窗尺寸规范化：未配置或越界回落默认值（服务端另有区间校验与库内 CHECK）。 */
export function resolveDialogSize(
  width: number | null | undefined,
  height: number | null | undefined,
): { width: number; height: number } {
  return {
    width: Number.isFinite(width) && width! > 0 ? Math.round(width!) : DEFAULT_DIALOG_WIDTH,
    height: Number.isFinite(height) && height! > 0 ? Math.round(height!) : DEFAULT_DIALOG_HEIGHT,
  }
}

/** 弹窗方式的"窗体提示"：随导航一起递给表单页，让它第一帧就知道该用窗体容器（含尺寸）。 */
export interface FormDialogShellHint {
  openMode: 'DIALOG'
  title: string
  width: number
  height: number
}

/** 从导航 state 里取窗体提示（非弹窗或形状不对一律返回 null；前端只当提示，定义到达后以定义为准）。 */
export function readDialogShellHint(state: unknown): FormDialogShellHint | null {
  const hint = (state as { formShell?: Partial<FormDialogShellHint> } | null | undefined)?.formShell
  if (!hint || normalizeFormOpenMode(hint.openMode) !== 'DIALOG') return null
  const size = resolveDialogSize(hint.width, hint.height)
  return { openMode: 'DIALOG', title: typeof hint.title === 'string' ? hint.title : '', ...size }
}

export interface OpenWorkbenchFormOptions {
  openMode?: string | null
  /** 弹窗方式下窗体标题（模块名）；列表定义里已有，顺手递给表单页做首帧容器。 */
  dialogTitle?: string | null
  /** 弹窗方式的窗体尺寸（px）：未配置回落默认，与服务端同口径。 */
  dialogWidth?: number | null
  dialogHeight?: number | null
  state?: unknown
}

/**
 * 按模块配置打开统一表单（供列表页等处使用）。
 *
 * 只改"怎么开"，不改"开哪去"：地址由调用方按既有路由契约给出；
 * 未配置打开方式的模块行为与从前逐字一致（当前标签内导航）。
 * 表单页**内部**的动作（新增/复制/编辑/上一条下一条）不走这里——它们留在已打开的
 * 窗体或标签里继续导航，否则每点一次新增都会再冒出一个标签。
 *
 * 弹窗方式额外把"窗体提示"放进导航 state：表单页首帧就能把加载态放进窗体内，
 * 而不是先闪一下整页加载态再收进窗体（见 FormEditorPage 的 inFormShell）。
 */
export function useOpenWorkbenchForm(): (url: string, options?: OpenWorkbenchFormOptions) => void {
  const navigate = useNavigate()
  const openTab = useOpenTab()
  return useCallback(
    (url: string, options?: OpenWorkbenchFormOptions) => {
      const mode = normalizeFormOpenMode(options?.openMode)
      if (mode === 'NEWTAB') {
        openTab(url)
        return
      }
      const state = mode === 'DIALOG'
        ? {
            ...(options?.state as Record<string, unknown> | undefined ?? {}),
            formShell: {
              openMode: 'DIALOG',
              title: options?.dialogTitle ?? '',
              ...resolveDialogSize(options?.dialogWidth, options?.dialogHeight),
            } satisfies FormDialogShellHint,
          }
        : options?.state
      navigate(url, state === undefined ? undefined : { state })
    },
    [navigate, openTab],
  )
}
