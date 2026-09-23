import { useMemo } from 'react'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import {
  parseDocumentActionParams,
  serializeDocumentActionParams,
  type DocumentActionParamField,
} from './documentActionConfig'

/**
 * 自定义按钮的入参声明编辑器（PARAM_STRUCT）。
 * 声明的是"点击后要用户填什么"（键/标题/类型/必填/长度），服务端按同一份声明校验提交值，
 * 所以这里只允许产出服务端认得的结构 `{"fields":[{key,label,type,required,maxLength}]}`。
 */
const PARAM_TYPES: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'string', label: '文本' },
  { value: 'number', label: '数字' },
  { value: 'date', label: '日期' },
  { value: 'bool', label: '是/否' },
]

export function DocumentActionParamsEditor({
  json,
  onChange,
}: {
  json: string | null
  onChange: (json: string | null) => void
}) {
  const fields = useMemo(() => parseDocumentActionParams(json), [json])
  const commit = (next: DocumentActionParamField[]) => onChange(serializeDocumentActionParams(next))
  const patch = (index: number, changes: Partial<DocumentActionParamField>) =>
    commit(fields.map((field, i) => (i === index ? { ...field, ...changes } : field)))
  const remove = (index: number) => commit(fields.filter((_, i) => i !== index))
  const add = () =>
    commit([...fields, { key: '', label: '', type: 'string', required: false, maxLength: 50 }])

  return (
    <div>
      {fields.length === 0 ? (
        <div className="text-secondary small mb-1">该按钮点击时不需要用户填写参数。</div>
      ) : (
        <div className="d-flex flex-column gap-1 mb-1">
          {fields.map((field, index) => (
            <div className="row g-1 align-items-center" key={index}>
              <div className="col-3">
                <input className="form-control form-control-sm" placeholder="参数键（字母开头）" value={field.key}
                  aria-label={`参数 ${index + 1} 键`}
                  onChange={(event) => patch(index, { key: event.target.value })} />
              </div>
              <div className="col-3">
                <input className="form-control form-control-sm" placeholder="界面标签" value={field.label}
                  aria-label={`参数 ${index + 1} 标签`}
                  onChange={(event) => patch(index, { label: event.target.value })} />
              </div>
              <div className="col-2">
                <select className="form-select form-select-sm" value={field.type}
                  aria-label={`参数 ${index + 1} 类型`}
                  onChange={(event) => patch(index, { type: event.target.value })}>
                  {PARAM_TYPES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                </select>
              </div>
              <div className="col-2">
                <input type="number" min={1} max={400} className="form-control form-control-sm" placeholder="最大长度"
                  value={field.maxLength ?? ''} disabled={field.type !== 'string'}
                  aria-label={`参数 ${index + 1} 最大长度`}
                  onChange={(event) => patch(index, { maxLength: event.target.value === '' ? null : Number(event.target.value) })} />
              </div>
              <div className="col-1 form-check ms-2">
                <input id={`doc-action-param-required-${index}`} className="form-check-input" type="checkbox"
                  checked={field.required}
                  onChange={(event) => patch(index, { required: event.target.checked })} />
                <label className="form-check-label small" htmlFor={`doc-action-param-required-${index}`}>必填</label>
              </div>
              <div className="col-1">
                <Button size="sm" variant="danger" icon={<IconTrash size={14} />}
                  aria-label={`删除参数 ${index + 1}`} onClick={() => remove(index)}>
                  删除
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}
      <div className="d-flex align-items-center gap-2">
        <Button size="sm" icon={<IconPlus size={14} />} onClick={add} disabled={fields.length >= 10}>新增参数</Button>
        <span className="text-secondary small">最多 10 个；提交值由服务端按此声明校验（缺必填、超长、未声明键都会被拒）。</span>
      </div>
    </div>
  )
}
