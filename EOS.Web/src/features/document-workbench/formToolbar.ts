import type { ErpCommandItem } from '../../components/common/ErpCommandBar'
import type { FormDefinition } from './formDefinition'
import { LIFECYCLE_KEYS } from './lifecycleKeys'

/**
 * 浏览态工具栏单据级动作构建：
 * 动作集**只由能力与权限决定**（批核能力 / 结案权限 / 单据状态 / 流程在途），
 * 曾经那套 `FORM_BUTTONS` 白名单分支已随迁移 320 退役——它全库为空，
 * 运行态一直走的都是下面这条判定。
 * 状态禁用条件（isFinished/flowInProgress）重复 8 遍，抽纯函数后可单测顺序断言。
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
  // 批核能力 = 工作流（过程/效果链/流程定义）或无副作用自动批核（与服务端同口径）；
  // 服务端已按"四者取并集"给出 hasApproveCapability，缺字段时回退到旧的两个标志。
  const canWorkflow = form.hasApproveCapability ?? (form.hasWorkflow || form.hasStatelessApprove)
  // 结案能力 = 主表有 FINISHED（服务端 hasFinishCapability）；旧契约缺该标志时
  // 按记录里是否返回了 FINISHED 回退，避免"没有结案列却显示结案按钮、点了必 404"。
  const canFinish = form.hasFinishCapability ?? (master?.[LIFECYCLE_KEYS.finished] !== undefined)
  return [
    ...(canWorkflow && keyParam && master && master[LIFECYCLE_KEYS.approved] !== true && !flowInProgress && form.canApprove
      ? [{ action: 'approve', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.openApprove } satisfies ErpCommandItem]
      : []),
    ...(canWorkflow && keyParam && master && master[LIFECYCLE_KEYS.approved] === true && form.canDeapprove
      ? [{ action: 'deapprove', disabled: isFinished, loading: handlers.workflowPending, onClick: handlers.deapprove } satisfies ErpCommandItem]
      : []),
    ...(canFinish && keyParam && form.canEndCase && master && master[LIFECYCLE_KEYS.finished] !== true
      ? [{ action: 'endcase', loading: handlers.finishPending, onClick: handlers.endcase } satisfies ErpCommandItem]
      : []),
    ...(canFinish && keyParam && form.canUnEndCase && master && master[LIFECYCLE_KEYS.finished] === true
      ? [{ action: 'unendcase', loading: handlers.finishPending, onClick: handlers.unendcase } satisfies ErpCommandItem]
      : []),
    ...(keyParam ? [{ action: 'print', onClick: handlers.openPrint } satisfies ErpCommandItem] : []),
  ]
}
