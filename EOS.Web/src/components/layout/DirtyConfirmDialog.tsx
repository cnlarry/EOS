import { Modal } from '../ui/Modal'
import { Button } from '../ui/Button'

interface DirtyConfirmDialogProps {
  /** 涉及未保存改动的标签标题 */
  labels: string[]
  /** 离开当前标签还是关闭标签（文案不同） */
  mode: 'leave' | 'close'
  /** 保存进行中 */
  busy: boolean
  onSave: () => void
  onDiscard: () => void
  onCancel: () => void
}

/** 未保存改动的三选一确认：保存 / 不保存 / 取消。取消与保存失败都停留在原处。 */
export function DirtyConfirmDialog({ labels, mode, busy, onSave, onDiscard, onCancel }: DirtyConfirmDialogProps) {
  const action = mode === 'leave' ? '离开' : '关闭'
  const title = labels.length === 1 ? `「${labels[0]}」有未保存的修改` : `有 ${labels.length} 个标签存在未保存的修改`
  return (
    <Modal
      title={title}
      onClose={onCancel}
      ariaLabel="未保存改动确认"
      footer={(
        <>
          <Button variant="secondary" onClick={onCancel} disabled={busy}>取消</Button>
          <Button variant="danger" onClick={onDiscard} disabled={busy}>不保存并{action}</Button>
          <Button variant="primary" onClick={onSave} loading={busy}>保存并{action}</Button>
        </>
      )}
    >
      {labels.length === 1
        ? `保存后可继续${action}；选择「不保存并${action}」将丢弃本次修改。`
        : `依次保存这些标签后可继续${action}；选择「不保存并${action}」将丢弃全部修改。`}
    </Modal>
  )
}
