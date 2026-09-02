import { Button } from '../ui/Button'
import { Modal } from '../ui/Modal'
import { emptyQueryCondition, queryOperators, type QueryCondition } from './queryCondition'

interface ErpQueryBuilderProps {
  open: boolean
  title?: string
  /** 可查询字段白名单（页面从服务端 Definition 提取，组件不感知 API） */
  fields: { key: string; label: string }[]
  conditions: QueryCondition[]
  onChange: (conditions: QueryCondition[]) => void
  /** 应用当前条件（由页面提交查询并关闭弹窗） */
  onApply: () => void
  /** 清空已应用条件（由页面清空并关闭弹窗） */
  onClear: () => void
  onClose: () => void
}

/**
 * 通用「高级查询」弹窗：结构化条件编辑器（字段 + 运算符 + 值/区间 + AND/OR 逻辑）。
 *
 * 受控组件：条件数组由页面持有（跨开关弹窗保留草稿），组件只负责渲染与增量修改；
 * 字段白名单由页面从服务端 Definition 提取，组件不感知 API 与权限。
 */
export function ErpQueryBuilder({
  open,
  title = '查询条件设定',
  fields,
  conditions,
  onChange,
  onApply,
  onClear,
  onClose,
}: ErpQueryBuilderProps) {
  if (!open) return null

  const canApply = conditions.length > 0 && conditions.every((item) => item.field)
  const update = (index: number, patch: Partial<QueryCondition>) =>
    onChange(conditions.map((item, i) => (i === index ? { ...item, ...patch } : item)))

  return (
    <Modal
      title={title}
      onClose={onClose}
      size="lg"
      footer={<>
        <Button onClick={onClear}>清空</Button>
        <Button variant="primary" disabled={!canApply} onClick={onApply}>应用查询</Button>
      </>}
    >
            <div className="d-grid gap-2">
              {conditions.map((condition, index) => (
                <div className="row g-2 align-items-center" key={index}>
                  {index > 0 && (
                    <div className="col-2">
                      <select
                        className="form-select"
                        aria-label={`条件${index + 1}逻辑`}
                        value={condition.logic}
                        onChange={(event) => update(index, { logic: event.target.value })}
                      >
                        <option value="and">并且</option>
                        <option value="or">或者</option>
                      </select>
                    </div>
                  )}
                  <div className={index > 0 ? 'col-3' : 'col-5'}>
                    <select
                      className="form-select"
                      aria-label={`条件${index + 1}字段`}
                      value={condition.field}
                      onChange={(event) => update(index, { field: event.target.value })}
                    >
                      <option value="">选择字段</option>
                      {fields.map((field) => (
                        <option key={field.key} value={field.key}>{field.label}</option>
                      ))}
                    </select>
                  </div>
                  <div className="col-2">
                    <select
                      className="form-select"
                      aria-label={`条件${index + 1}运算符`}
                      value={condition.operator}
                      onChange={(event) => update(index, { operator: event.target.value })}
                    >
                      {queryOperators.map(([value, label]) => (
                        <option key={value} value={value}>{label}</option>
                      ))}
                    </select>
                  </div>
                  <div className="col">
                    <input
                      className="form-control"
                      aria-label={`条件${index + 1}值`}
                      disabled={condition.operator === 'empty' || condition.operator === 'notempty'}
                      value={condition.value}
                      onChange={(event) => update(index, { value: event.target.value })}
                    />
                  </div>
                  {condition.operator === 'between' && (
                    <div className="col">
                      <input
                        className="form-control"
                        aria-label={`条件${index + 1}值上限`}
                        value={condition.valueTo}
                        onChange={(event) => update(index, { valueTo: event.target.value })}
                      />
                    </div>
                  )}
                  <div className="col-auto">
                    <button
                      type="button"
                      className="btn btn-ghost-danger"
                      disabled={conditions.length === 1}
                      onClick={() => onChange(conditions.filter((_, i) => i !== index))}
                    >
                      删除
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <button type="button" className="btn btn-ghost-primary mt-3" onClick={() => onChange([...conditions, emptyQueryCondition()])}>
              添加条件
            </button>
    </Modal>
  )
}
