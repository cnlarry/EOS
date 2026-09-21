import { useId, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ColumnPickerInput, TablePickerInput } from './BusinessActionPickers'
import { formatCondition, parseJsonObject, withTargetTable, type BusinessNameLookup } from './businessActionText'

/**
 * 条件结构化编辑器（闭式算子集）：
 * 把 `{"logic":"AND","items":[…]}` 渲染成逐行判据——系统开关 / 字段与值比较 / 字段与字段比较。
 * 含内层子查询的 `not-exists`（单号引用、完成度重算等）结构复杂，保留专家模式改写；
 * 表达不了的效果按设计走定制 C# 效果，不在这里扩条件语言。
 */

const CONDITION_SCOPES = ['TARGET', 'MASTER', 'DETAIL'] as const
const SCOPE_WORDS: Record<string, string> = {
  TARGET: '目标行',
  MASTER: '本单主表',
  DETAIL: '本单明细',
}
const COMPARE_OPS = ['EQ', 'NEQ', 'GT', 'GE', 'LT', 'LE'] as const
const COMPARE_SYMBOLS: Record<string, string> = {
  EQ: '=', NEQ: '<>', GT: '>', GE: '>=', LT: '<', LE: '<=',
}

type Json = Record<string, unknown>

interface ConditionItem extends Json {
  type?: unknown
}

/** 系统参数目录（`GET /settings/system`）：开关判据的键只能从已登记参数里选。 */
interface SystemParameter {
  key: string
  description?: string | null
  groupLabel?: string | null
}

interface SystemSettingsResponse {
  groups?: { groupLabel?: string | null; parameters?: SystemParameter[] }[]
}

export function ConditionEditor({
  value,
  names,
  targetTable,
  masterTable,
  detailTable,
  onChange,
}: {
  value: string | null
  names: BusinessNameLookup
  /** 公式行的目标表（TARGET 域字段从中选取）；动作级条件传 null。 */
  targetTable?: string | null
  masterTable: string | null
  detailTable: string | null
  onChange: (value: string | null) => void
}) {
  const [expert, setExpert] = useState(false)
  const listId = useId()
  // 开关键取数失败（无权限/接口异常）时只是没有候选项，仍可手填，不阻断编辑。
  const paramsQuery = useQuery({
    queryKey: ['system-parameters-switch-keys'],
    queryFn: () => apiClient.get<SystemSettingsResponse>('/settings/system'),
    staleTime: 5 * 60 * 1000,
  })
  const switchKeys = useMemo<SystemParameter[]>(() => {
    const groups = paramsQuery.data?.groups ?? []
    return groups
      .flatMap((group) => (group.parameters ?? []).map((item) => ({ ...item, groupLabel: group.groupLabel ?? null })))
      .filter((item) => typeof item.key === 'string' && item.key.trim() !== '')
      .sort((a, b) => a.key.localeCompare(b.key))
  }, [paramsQuery.data])
  const parsed = parseJsonObject(value)
  const items = Array.isArray(parsed?.items) ? (parsed!.items as ConditionItem[]) : null
  const invalid = value != null && value.trim() !== '' && parsed === null
  const unsupported = parsed !== null && items === null
  const logic = typeof parsed?.logic === 'string' ? parsed.logic.toUpperCase() : 'AND'

  const commit = (nextLogic: string, nextItems: ConditionItem[]) => {
    onChange(nextItems.length === 0 ? null : JSON.stringify({ logic: nextLogic, items: nextItems }))
  }
  const updateItem = (index: number, next: ConditionItem) =>
    commit(logic, (items ?? []).map((item, i) => (i === index ? next : item)))
  const removeItem = (index: number) => commit(logic, (items ?? []).filter((_, i) => i !== index))
  const appendItem = () => commit(logic, [...(items ?? []), { type: 'switch', key: '', value: true }])

  // TARGET 域指"正在被更新的那一行"，只有调用点知道它是哪张表。
  const scopedNames = useMemo(() => withTargetTable(names, targetTable), [names, targetTable])
  const readable = formatCondition(value, scopedNames)
  const tableOf = (scope: string | null | undefined) => {
    const key = (scope ?? '').trim().toUpperCase()
    if (key === 'TARGET') return targetTable ?? null
    if (key === 'MASTER') return masterTable
    if (key === 'DETAIL') return detailTable
    return null
  }

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2 mb-1">
        <span className="small text-secondary">
          条件＝这条步骤在什么情况下执行（闭式算子：系统开关 / 字段与值比较 / 字段与字段比较）
        </span>
        <div className="d-flex gap-1 align-items-center">
          {items && items.length > 1 ? (
            <select
              className="form-select form-select-sm"
              style={{ width: 130 }}
              value={logic}
              onChange={(event) => commit(event.target.value, items)}
            >
              <option value="AND">全部满足（AND）</option>
              <option value="OR">任一满足（OR）</option>
            </select>
          ) : null}
          <Button size="sm" icon={<IconPlus size={16} />} onClick={appendItem}>添加判据</Button>
          <Button size="sm" variant="ghost" onClick={() => setExpert(!expert)}>
            {expert ? '结构化编辑' : '专家模式（JSON）'}
          </Button>
        </div>
      </div>

      {invalid || unsupported ? (
        <div className="alert alert-warning py-1 small mb-2">
          {invalid
            ? '当前文本不是合法 JSON 对象，已切换到专家模式；修正后方可回到结构化编辑。'
            : '当前条件不是「logic + items」判据列表（含内层子查询或非标准结构），只能在专家模式下编辑。'}
        </div>
      ) : null}

      {expert || invalid || unsupported ? (
        <textarea
          className="form-control form-control-sm font-monospace"
          rows={3}
          spellCheck={false}
          value={value ?? ''}
          placeholder='{"logic":"AND","items":[{"type":"switch","key":"SEND_ORDER_TAG","value":true}]}'
          onChange={(event) => onChange(event.target.value || null)}
        />
      ) : (items ?? []).length === 0 ? (
        <div className="text-secondary small">未设置条件（该步骤无条件执行）。</div>
      ) : (
        <div className="d-flex flex-column gap-1">
          {(items ?? []).map((item, index) => (
            <div className="row g-1 align-items-center" key={index}>
              <div className="col-11">
                <ConditionItemEditor
                  item={item}
                  tableOf={tableOf}
                  names={names}
                  switchKeys={switchKeys}
                  switchListId={listId}
                  onChange={(next) => updateItem(index, next)}
                />
              </div>
              <div className="col-1 text-end">
                <Button
                  size="sm"
                  variant="ghost"
                  icon={<IconTrash size={14} />}
                  title="删除该判据"
                  aria-label="删除该判据"
                  onClick={() => removeItem(index)}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      {!expert && !invalid && !unsupported && readable ? (
        <div className="small text-secondary mt-1">条件读作：{readable}</div>
      ) : null}
      <datalist id={listId}>
        {switchKeys.map((item) => (
          <option key={item.key} value={item.key}>
            {[item.description, item.groupLabel].filter(Boolean).join(' · ')}
          </option>
        ))}
      </datalist>
    </div>
  )
}

function ConditionItemEditor({
  item,
  tableOf,
  names,
  switchKeys,
  switchListId,
  onChange,
}: {
  item: ConditionItem
  tableOf: (scope: string | null | undefined) => string | null
  names: BusinessNameLookup
  switchKeys: SystemParameter[]
  switchListId: string
  onChange: (next: ConditionItem) => void
}) {
  const type = String(item.type ?? '').toLowerCase()
  const patch = (next: Json) => onChange({ ...item, ...next } as ConditionItem)
  const currentKey = typeof item.key === 'string' ? item.key : ''
  const knownKey = switchKeys.some((item) => item.key.toUpperCase() === currentKey.toUpperCase())

  if (type === 'switch') {
    return (
      <div className="input-group input-group-sm">
        <span className="input-group-text">系统开关</span>
        <input
          className={`form-control form-control-sm font-monospace${currentKey !== '' && !knownKey && switchKeys.length > 0 ? ' is-invalid' : ''}`}
          list={switchListId}
          value={currentKey}
          placeholder="选择或输入已登记的系统参数键"
          title={switchKeys.length === 0
            ? '系统参数目录不可读（无权限或接口异常），可手填键名'
            : '未登记的参数键会在执行期被拒绝'}
          onChange={(event) => patch({ key: event.target.value })}
        />
        <span className="input-group-text">=</span>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 110 }}
          value={item.value === true ? 'true' : 'false'}
          onChange={(event) => patch({ value: event.target.value === 'true' })}
        >
          <option value="true">开（true）</option>
          <option value="false">关（false）</option>
        </select>
      </div>
    )
  }

  if (type === 'value-eq' || type === 'value-neq') {
    return (
      <div className="input-group input-group-sm">
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 120 }}
          value={type === 'value-eq' ? 'value-eq' : 'value-neq'}
          onChange={(event) => patch({ type: event.target.value })}
        >
          <option value="value-eq">字段 =</option>
          <option value="value-neq">字段 ≠</option>
        </select>
        <OperandScope
          scope={scopeOf(item.field)}
          tableOf={tableOf}
          onScope={(scope) => patch({ field: { ...(item.field as Json ?? {}), scope } })}
        />
        <ColumnPickerInput
          table={tableOf(scopeOf(item.field))}
          value={fieldOf(item.field)}
          title="条件字段"
          names={names}
          onChange={(next) => patch({ field: { ...(item.field as Json ?? {}), field: next } })}
        />
        <input
          className="form-control form-control-sm font-monospace"
          style={{ maxWidth: 130 }}
          value={item.value === undefined ? '' : String(item.value)}
          placeholder="比较值"
          onChange={(event) => patch({ value: parseLiteral(event.target.value) })}
        />
      </div>
    )
  }

  if (type === 'field-compare') {
    return (
      <div className="d-flex align-items-center gap-1 flex-wrap">
        <div className="input-group input-group-sm" style={{ maxWidth: 380 }}>
          <OperandScope
            scope={scopeOf(item.left)}
            tableOf={tableOf}
            onScope={(scope) => patch({ left: { ...(item.left as Json ?? {}), scope } })}
          />
          <ColumnPickerInput
            table={tableOf(scopeOf(item.left))}
            value={fieldOf(item.left)}
            title="左字段"
            names={names}
            onChange={(next) => patch({ left: { ...(item.left as Json ?? {}), field: next } })}
          />
        </div>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 90 }}
          value={String(item.op ?? 'EQ')}
          onChange={(event) => patch({ op: event.target.value })}
        >
          {COMPARE_OPS.map((op) => <option key={op} value={op}>{COMPARE_SYMBOLS[op]}</option>)}
        </select>
        <div className="input-group input-group-sm" style={{ maxWidth: 380 }}>
          <OperandScope
            scope={scopeOf(item.right)}
            tableOf={tableOf}
            onScope={(scope) => patch({ right: { ...(item.right as Json ?? {}), scope } })}
          />
          <ColumnPickerInput
            table={tableOf(scopeOf(item.right))}
            value={fieldOf(item.right)}
            title="右字段"
            names={names}
            onChange={(next) => patch({ right: { ...(item.right as Json ?? {}), field: next } })}
          />
        </div>
      </div>
    )
  }

  if (type === 'not-exists') {
    const inner = (item.condition ?? {}) as Json
    const innerTable = String(item.targetTable ?? '')
    const matches = Array.isArray(item.match) ? (item.match as Json[]) : []
    const outerTable = tableOf('TARGET')
    // 子查询里的 TARGET 指"子查询表的当前行"，与外层的 TARGET 不是同一张表。
    const innerTableOf = (scope: string | null | undefined) =>
      (scope ?? '').trim().toUpperCase() === 'TARGET' ? (innerTable || null) : tableOf(scope)
    const setMatch = (next: Json[]) => onChange({
      ...item,
      match: next.map((entry) => {
        const source = (entry.source ?? {}) as Json
        return { target: String(entry.target ?? ''), source: { ...source, field: String(source.field ?? '') } }
      }),
    })
    return (
      <div className="border rounded p-2 d-flex flex-column gap-1">
        <div className="d-flex align-items-center gap-1 flex-wrap">
          <span className="small text-secondary">关联子查询：子查询表</span>
          <select
            className="form-select form-select-sm"
            style={{ maxWidth: 170 }}
            value={item.negate === true ? 'exists' : 'not-exists'}
            onChange={(event) => patch({ negate: event.target.value === 'exists' })}
          >
            <option value="not-exists">不存在（NOT EXISTS）</option>
            <option value="exists">存在（EXISTS）</option>
          </select>
          <div style={{ minWidth: 260 }}>
            <TablePickerInput
              value={innerTable}
              title="选择子查询表"
              onChange={(next) => patch({ targetTable: next })}
            />
          </div>
        </div>

        <div className="small text-secondary">
          关联条件：[本单目标行列] = [子查询表列]（子查询里 TARGET 指子查询表的当前行）
        </div>
        <div className="d-flex flex-column gap-1">
          {matches.map((entry, index) => {
            const source = (entry.source ?? {}) as Json
            return (
              <div className="row g-1 align-items-center" key={index}>
                <div className="col-5">
                  <ColumnPickerInput
                    table={outerTable}
                    value={String(entry.target ?? '')}
                    title="本单目标行列"
                    names={names}
                    onChange={(next) => setMatch(matches.map((current, i) => (i === index ? { ...current, target: next } : current)))}
                  />
                </div>
                <div className="col-1 text-center small text-secondary">=</div>
                <div className="col-5">
                  <ColumnPickerInput
                    table={innerTable}
                    value={String(source.field ?? '')}
                    title="子查询表列"
                    names={names}
                    onChange={(next) => setMatch(matches.map((current, i) => (
                      i === index ? { ...current, source: { ...(current.source ?? {}), field: next } } : current
                    )))}
                  />
                </div>
                <div className="col-1 text-end">
                  <Button
                    size="sm"
                    variant="ghost"
                    icon={<IconTrash size={14} />}
                    title="删除该关联键"
                    aria-label="删除该关联键"
                    onClick={() => setMatch(matches.filter((_, i) => i !== index))}
                  />
                </div>
              </div>
            )
          })}
        </div>
        <Button
          size="sm"
          variant="ghost"
          icon={<IconPlus size={14} />}
          disabled={innerTable === ''}
          onClick={() => setMatch([...matches, { target: '', source: { field: '' } }])}
        >
          添加关联键
        </Button>

        <div className="small text-secondary mt-1">且子查询行满足：</div>
        <ConditionItemEditor
          item={inner as ConditionItem}
          tableOf={innerTableOf}
          names={names}
          switchKeys={switchKeys}
          switchListId={switchListId}
          onChange={(next) => patch({ condition: next })}
        />
      </div>
    )
  }

  // 无 type 的比较结构：not-exists 的内层比较写法之一（{left, op, right}）。
  // 编译器对它只放行 TARGET 域（其余域解析不到别名），所以两侧字段都锁在子查询表上。
  if (!type && item.left !== undefined && item.op !== undefined) {
    const innerTable = tableOf('TARGET')
    const rightIsValue = item.right !== undefined && (item.right as Json).value !== undefined
    return (
      <div className="d-flex align-items-center gap-1 flex-wrap">
        <div style={{ minWidth: 260 }}>
          <ColumnPickerInput
            table={innerTable}
            value={fieldOf(item.left)}
            title="子查询表左字段"
            names={names}
            onChange={(next) => patch({ left: { scope: 'TARGET', field: next } })}
          />
        </div>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 90 }}
          value={String(item.op ?? 'EQ')}
          onChange={(event) => patch({ op: event.target.value })}
        >
          {COMPARE_OPS.map((op) => <option key={op} value={op}>{COMPARE_SYMBOLS[op]}</option>)}
        </select>
        <div style={{ minWidth: 260 }}>
          <ColumnPickerInput
            table={innerTable}
            value={fieldOf(item.right)}
            title="子查询表右字段"
            names={names}
            onChange={(next) => patch({ right: { scope: 'TARGET', field: next } })}
          />
        </div>
        <div className="form-check form-check-inline ms-1">
          <input
            className="form-check-input"
            type="checkbox"
            id={`${switchListId}-right-value`}
            checked={rightIsValue}
            onChange={(event) => patch({
              right: event.target.checked
                ? { value: String((item.right as Json | undefined)?.field ?? '') }
                : { scope: 'TARGET', field: '' },
            })}
          />
          <label className="form-check-label small" htmlFor={`${switchListId}-right-value`}>右侧比常量</label>
        </div>
        {rightIsValue ? (
          <input
            className="form-control form-control-sm font-monospace"
            style={{ maxWidth: 130 }}
            value={String((item.right as Json).value ?? '')}
            placeholder="比较值"
            onChange={(event) => patch({ right: { value: parseLiteral(event.target.value) } })}
          />
        ) : null}
      </div>
    )
  }

  // 其它结构：交给专家模式，这里只显示人话摘要。
  return (
    <div className="alert alert-light border py-1 px-2 small mb-0">
      该判据（{type || '未知类型'}）结构特殊，请在专家模式下编辑。
    </div>
  )
}

function OperandScope({
  scope,
  tableOf,
  onScope,
}: {
  scope: string
  tableOf: (scope: string | null | undefined) => string | null
  onScope: (scope: string) => void
}) {
  const value = (scope || 'TARGET').toUpperCase()
  const disabled = !tableOf(value)
  return (
    <select
      className="form-select form-select-sm"
      style={{ maxWidth: 120 }}
      value={value}
      onChange={(event) => onScope(event.target.value)}
      title={disabled ? '当前域在本模块不可用' : undefined}
    >
      {CONDITION_SCOPES.map((item) => (
        <option key={item} value={item} disabled={!tableOf(item)}>{SCOPE_WORDS[item]}</option>
      ))}
    </select>
  )
}

function scopeOf(operand: unknown): string {
  if (operand && typeof operand === 'object' && typeof (operand as Json).scope === 'string') {
    return String((operand as Json).scope).toUpperCase()
  }
  return 'TARGET'
}

function fieldOf(operand: unknown): string {
  if (operand && typeof operand === 'object' && typeof (operand as Json).field === 'string') {
    return String((operand as Json).field)
  }
  return ''
}

/** 常量输入：true/false、整数、小数按字面量类型回写，其余按字符串。 */
function parseLiteral(text: string): unknown {
  const trimmed = text.trim()
  if (trimmed === 'true') return true
  if (trimmed === 'false') return false
  if (trimmed !== '' && !Number.isNaN(Number(trimmed)) && /^-?\d+(\.\d+)?$/.test(trimmed)) return Number(trimmed)
  return text
}
