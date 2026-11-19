import type { VerticalSplit } from '../../hooks/useVerticalSplit'

/**
 * 主表 / 明细之间的高度分割条。
 *
 * 整条叠在两卡之间的 10px 间隙里（`margin-block` 为负），**不额外占高度**，
 * 所以调用方只要把它插到两张卡之间即可，页面骨架与默认高度分配原样生效。
 * 拖动、方向键、双击复位与持久化都在 `useVerticalSplit` 里。
 */
export function ErpSplitHandle({ split, label = '调整主表与明细高度' }: { split: VerticalSplit; label?: string }) {
  const { handleRef, dragging, percent, onPointerDown, onPointerMove, onPointerUp, onFocus, onKeyDown, onReset } = split
  return (
    <div
      ref={handleRef}
      className={`erp-split-handle${dragging ? ' erp-split-handle-active' : ''}`}
      role="separator"
      aria-orientation="horizontal"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={percent ?? undefined}
      title={`拖动${label}，双击恢复默认`}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      onFocus={onFocus}
      onKeyDown={onKeyDown}
      onDoubleClick={onReset}
    />
  )
}
