/**
 * 2301「行为动作」草稿的纯操作：克隆服务端配置、把来源模块的动作并入本地草稿。
 *
 * 与 React 无关，也与渲染无关（可读化渲染在 businessActionText），单独成模块是为了两件事：
 * 组件文件只导出组件（Fast Refresh 要求），以及这些判定可以直接被单测覆盖。
 */
import { MANUAL_EVENT } from './documentActionConfig'
import type { BusinessAction, BusinessActionOp } from './BusinessActionsPanel'

export const cloneOp = (op: BusinessActionOp): BusinessActionOp => ({ ...op })

/** 深拷贝一条动作（含公式行），避免草稿与服务端缓存共享引用。 */
export const cloneAction = (action: BusinessAction): BusinessAction => ({
  ...action,
  ops: (action.ops ?? []).map(cloneOp),
})

/**
 * 克隆追加：把来源模块的动作并入当前草稿。
 * 顺序号按"同事件在本模块内"重新顺延——唯一键是 (模块, 事件, 序号)，沿用来源模块的序号会撞库。
 * 自定义按钮在这里**再兜底过滤一次**：其授权是跨模块不迁移的 fail-closed 名单，带过来只会得到
 * 一批没人能点的按钮，而且失败得很安静。调用方（克隆列表）已经过滤过，这道兜底防的是将来新增调用方。
 */
export function mergeClonedActions(
  previous: BusinessAction[],
  incoming: BusinessAction[],
): BusinessAction[] {
  const next = [...previous]
  for (const action of incoming) {
    if (action.eventCode === MANUAL_EVENT) continue
    const seq = next
      .filter((item) => item.eventCode === action.eventCode)
      .reduce((max, item) => Math.max(max, item.seq), 0) + 1
    next.push(cloneAction({ ...action, seq }))
  }
  return next
}
