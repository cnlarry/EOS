import { useMemo, useState } from 'react'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import { parseJsonObject } from './businessActionText'

/**
 * 结构化参数编辑器（效果参数与校验规则参数共用）：
 * - 根键取自服务端登记的 Schema 白名单（缺 Schema 时提示"不允许配置参数"）；
 * - 值按类型递归渲染：标量给控件、嵌套对象逐键展开、数组可增删项（以末项为模板）；
 * - 键集合由效果/校验模板定义，**不让用户发明键**；Schema 外的键给出警告（保存会被拒绝）；
 * - 专家模式保留原始 JSON 文本，供批量粘贴或结构异常时收手。
 */
/** 参数深 Schema 描述（服务端下发；没有描述的键回到"只显示根键"的老形态）。 */
export interface ParamFieldDescriptor {
  name: string
  type: string
  required: boolean
  enumValues?: string[] | null
  default?: string | null
  description?: string | null
  example?: string | null
}

export function StructuredParamsEditor({
  hint,
  rootKeys,
  descriptors,
  json,
  emptyHint,
  onChange,
}: {
  /** 顶部说明（如"效果键：field-accumulate"）。 */
  hint: string
  rootKeys: string[]
  /** 逐键描述（只登记了能确证语义的键，其余键不显示说明，也不假装知道）。 */
  descriptors?: ParamFieldDescriptor[]
  json: string | null
  emptyHint: string
  onChange: (json: string | null) => void
}) {
  const [expert, setExpert] = useState(false)
  const parsed = useMemo(() => {
    if (!json) return {}
    const value = parseJsonObject(json)
    return value ?? {}
  }, [json])
  const invalid = json != null && json.trim() !== '' && parseJsonObject(json) === null
  // Schema 外键只提示不隐藏：草稿里可能确有历史配置，保存期由服务端裁决。
  const unknownKeys = Object.keys(parsed).filter((key) => !rootKeys.includes(key))

  const commit = (next: Record<string, unknown>) => {
    const kept = Object.fromEntries(Object.entries(next).filter(([, value]) => value !== undefined && value !== ''))
    onChange(Object.keys(kept).length > 0 ? JSON.stringify(kept) : null)
  }
  const updateRoot = (key: string, next: unknown) => {
    const nextObject: Record<string, unknown> = { ...parsed }
    if (next === undefined || next === '') delete nextObject[key]
    else nextObject[key] = next
    commit(nextObject)
  }
  // 根键 = Schema 白名单 ∪ 现有键：缺 Schema 时仍能编辑既有内容，但不会凭空造键。
  const keys = useMemo(
    () => [...rootKeys, ...Object.keys(parsed).filter((key) => !rootKeys.includes(key))],
    [rootKeys, parsed],
  )
  const descriptorByKey = useMemo(
    () => new Map((descriptors ?? []).map((field) => [field.name, field])),
    [descriptors],
  )

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2 mb-1">
        <div className="small text-secondary">{hint}</div>
        <Button size="sm" variant="ghost" onClick={() => setExpert(!expert)}>
          {expert ? '结构化编辑' : '专家模式（JSON）'}
        </Button>
      </div>
      {unknownKeys.length > 0 ? (
        <div className="alert alert-warning py-1 small mb-2">
          存在 Schema 外根键（保存将被拒绝）：{unknownKeys.join('、')}
        </div>
      ) : null}
      {invalid ? (
        <div className="alert alert-warning py-1 small mb-2">
          当前文本不是合法 JSON 对象，已切换到专家模式；修正后方可回到结构化编辑。
        </div>
      ) : null}
      {expert || invalid ? (
        <textarea
          className="form-control form-control-sm font-monospace"
          rows={4}
          spellCheck={false}
          value={json ?? ''}
          placeholder='{"mode":"detail","checks":[]}'
          onChange={(event) => onChange(event.target.value || null)}
        />
      ) : keys.length === 0 ? (
        <div className="text-secondary small">{emptyHint}</div>
      ) : (
        <div className="d-flex flex-column gap-2">
          {keys.map((key) => {
            const descriptor = descriptorByKey.get(key)
            return (
              <div key={key} className="row g-1 align-items-start">
                <div className="col-3">
                  <label className="form-label small mb-0 text-nowrap font-monospace">
                    {key}
                    {descriptor?.required ? <span className="text-danger"> *</span> : null}
                  </label>
                  {descriptor?.description ? (
                    <div className="small text-secondary">{descriptor.description}</div>
                  ) : null}
                </div>
                <div className="col-9">
                  {descriptor?.enumValues && descriptor.enumValues.length > 0 ? (
                    <select
                      className="form-select form-select-sm"
                      value={typeof parsed[key] === 'string' ? String(parsed[key]) : ''}
                      onChange={(event) => updateRoot(key, event.target.value)}
                    >
                      <option value="">（未设置{descriptor.default ? `，默认 ${descriptor.default}` : ''}）</option>
                      {descriptor.enumValues.map((option) => (
                        <option key={option} value={option}>{option}</option>
                      ))}
                    </select>
                  ) : (
                    <ParamValueEditor value={parsed[key]} onChange={(next) => updateRoot(key, next)} />
                  )}
                  {descriptor?.example ? (
                    <div className="small text-secondary mt-1">示例：{descriptor.example}</div>
                  ) : null}
                </div>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

/**
 * 参数值编辑器（递归）：标量按类型渲染控件；嵌套对象只编辑已有键
 * （键集合由效果/校验模板定义，不由用户发明）；数组允许增删项，项结构以最后一项为模板。
 */
export function ParamValueEditor({
  value,
  onChange,
}: {
  value: unknown
  onChange: (next: unknown) => void
}) {
  if (typeof value === 'boolean') {
    return (
      <div className="form-check">
        <input className="form-check-input" type="checkbox" checked={value}
          onChange={(event) => onChange(event.target.checked)} />
        <label className="form-check-label small">{value ? 'true' : 'false'}</label>
      </div>
    )
  }
  if (typeof value === 'number') {
    return (
      <input type="number" className="form-control form-control-sm" value={value}
        onChange={(event) => onChange(event.target.value === '' ? undefined : Number(event.target.value))} />
    )
  }
  if (Array.isArray(value)) {
    const template = value.length > 0 ? value[value.length - 1] : ''
    return (
      <div className="border rounded p-1">
        <div className="d-flex flex-column gap-1">
          {value.map((item, index) => (
            <div className="d-flex align-items-start gap-1" key={index}>
              <span className="small text-secondary" style={{ minWidth: 18, lineHeight: '30px' }}>{index + 1}</span>
              <div className="flex-grow-1">
                <ParamValueEditor
                  value={item}
                  onChange={(next) => onChange(value.map((current, i) => (i === index ? next : current)))}
                />
              </div>
              <Button
                size="sm"
                variant="ghost"
                icon={<IconTrash size={14} />}
                title="删除该项"
                aria-label="删除该项"
                onClick={() => onChange(value.filter((_, i) => i !== index))}
              />
            </div>
          ))}
        </div>
        <Button
          size="sm"
          variant="ghost"
          icon={<IconPlus size={14} />}
          onClick={() => onChange([...value, template])}
        >
          添加一项
        </Button>
      </div>
    )
  }
  if (value !== null && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
    if (entries.length === 0) {
      return (
        <input className="form-control form-control-sm font-monospace" value="" readOnly
          placeholder="空对象（结构由模板定义，无需填写）" />
      )
    }
    return (
      <div className="border rounded p-1 d-flex flex-column gap-1">
        {entries.map(([key, item]) => (
          <div className="row g-1 align-items-start" key={key}>
            <div className="col-4">
              <label className="form-label small mb-0 text-nowrap font-monospace">{key}</label>
            </div>
            <div className="col-8">
              <ParamValueEditor
                value={item}
                onChange={(next) => onChange({ ...(value as Record<string, unknown>), [key]: next })}
              />
            </div>
          </div>
        ))}
      </div>
    )
  }
  return (
    <input
      className="form-control form-control-sm font-monospace"
      value={typeof value === 'string' ? value : ''}
      placeholder="留空移除该键"
      onChange={(event) => onChange(event.target.value === '' ? undefined : event.target.value)}
    />
  )
}
