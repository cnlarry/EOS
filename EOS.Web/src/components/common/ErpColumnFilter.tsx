import { Button } from '../ui/Button'
import { queryOperators, type QueryCondition } from './queryCondition'

interface ErpColumnFilterProps {
  condition: QueryCondition
  onChange: (condition: QueryCondition) => void
  onApply: () => void
  onClear: () => void
}

/**
 * 列头快速筛选弹层：固定为该列字段，选择运算符 + 输入值（区间/为空等特殊处理）。
 * 条件由调用方（页面）合并进查询并提交给服务端白名单端点。
 */
export function ErpColumnFilter({ condition, onChange, onApply, onClear }: ErpColumnFilterProps) {
  return (
    <div className="d-grid gap-2">
      <select
        className="form-select form-select-sm"
        aria-label="筛选运算符"
        value={condition.operator}
        onChange={(event) => onChange({ ...condition, operator: event.target.value })}
      >
        {queryOperators.map(([value, label]) => (
          <option key={value} value={value}>{label}</option>
        ))}
      </select>
      <input
        className="form-control form-control-sm"
        aria-label="筛选值"
        placeholder="筛选值"
        disabled={condition.operator === 'empty' || condition.operator === 'notempty'}
        value={condition.value}
        onChange={(event) => onChange({ ...condition, value: event.target.value })}
      />
      {condition.operator === 'between' && (
        <input
          className="form-control form-control-sm"
          aria-label="筛选值上限"
          placeholder="至"
          value={condition.valueTo}
          onChange={(event) => onChange({ ...condition, valueTo: event.target.value })}
        />
      )}
      <div className="d-flex gap-2 justify-content-end">
        <Button size="sm" onClick={onClear}>清除</Button>
        <Button size="sm" variant="primary" onClick={onApply}>应用</Button>
      </div>
    </div>
  )
}
