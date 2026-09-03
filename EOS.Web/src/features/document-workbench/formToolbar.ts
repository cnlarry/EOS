import type { ErpCommandItem } from '../../components/common/ErpCommandBar'
import type { FormDefinition } from './formDefinition'

/**
 * 浏览态工具栏单据级动作构建：
 * 原 FORM_BUTTONS 白名单分支与回退集在 FormEditorPage 内近似双份拷贝，
 * 状态禁用条件（isFinished/isConfirmed/flowInProgress）重复 8 遍；抽纯函数后可单测顺序断言。
 * 返回全部 approve/deapprove/endcase/unendcase/print 项，由调用方按固定顺序 filter 插入。
 */
export interface ViewToolbarState {
  master: Record<string, unknown> | undefined
  isConfirmed: boolean
  isFinished: boolean
  flowInProgress: boolean
  /** 路由主键参数（存在性即控制单据级动作显示）。 */
  keyParam: string | null
}

export interface ViewToolbarHandlers {
  openApprove: () => void
  deapprove: () => void
  endcase: () => void
  unendcase: () => void
  openPrint: () => void
  workflowPending: boolean
  finishPending: boolean
}

export function buildViewToolbarItems(
  form: FormDefinition,
  state: ViewToolbarState,
  handlers: ViewToolbarHandlers,
): ErpCommandItem[] {
  const { master, isFinished, flowInProgress, keyParam } = state
  if (form.buttons && form.buttons.length > 0)
    return form.buttons.flatMap((button): ErpCommandItem[] => {
      switch (button.action) {
        case 'approve':
          return form.hasWorkflow && form.canApprove && keyParam && master && master.CONFIRM_TAG !== true && !flowInProgress
            ? [{ action: 'approve', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.openApprove }]
            : []
        case 'deapprove':
          return form.hasWorkflow && form.canDeapprove && keyParam && master && master.CONFIRM_TAG === true
            ? [{ action: 'deapprove', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.deapprove }]
            : []
        case 'endcase':
          return keyParam && form.canEndCase && master && master.FINISHED_TAG !== true
            ? [{ action: 'endcase', loading: handlers.finishPending, onClick: handlers.endcase }]
            : []
        case 'unendcase':
          return keyParam && form.canUnEndCase && master && master.FINISHED_TAG === true
            ? [{ action: 'unendcase', loading: handlers.finishPending, onClick: handlers.unendcase }]
            : []
        case 'print':
          return keyParam ? [{ action: 'print', onClick: handlers.openPrint }] : []
        default:
          return []
      }
    })

  // 未配置 FORM_BUTTONS 的回退集（保持既有行为：工作流/结案/打印）
  return [
    ...(form.hasWorkflow && keyParam && master && master.CONFIRM_TAG !== true && !flowInProgress && form.canApprove
      ? [{ action: 'approve', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.openApprove } satisfies ErpCommandItem]
      : []),
    ...(form.hasWorkflow && keyParam && master && master.CONFIRM_TAG === true && form.canDeapprove
      ? [{ action: 'deapprove', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.deapprove } satisfies ErpCommandItem]
      : []),
    ...(keyParam && form.canEndCase && master && master.FINISHED_TAG !== true
      ? [{ action: 'endcase', loading: handlers.finishPending, onClick: handlers.endcase } satisfies ErpCommandItem]
      : []),
    ...(keyParam && form.canUnEndCase && master && master.FINISHED_TAG === true
      ? [{ action: 'unendcase', loading: handlers.finishPending, onClick: handlers.unendcase } satisfies ErpCommandItem]
      : []),
    ...(keyParam ? [{ action: 'print', onClick: handlers.openPrint } satisfies ErpCommandItem] : []),
  ]
}
