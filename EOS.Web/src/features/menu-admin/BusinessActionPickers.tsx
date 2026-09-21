import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import {
  formatMatch,
  labelWithCode,
  matchItemsFromRelation,
  parseMatchItems,
  serializeMatchItems,
  type BusinessNameLookup,
  type MatchGroupPreset,
  type MatchItem,
} from './businessActionText'

/**
 * 2301 行为动作配置的受控输入：
 * 表名/列名一律经统一选择器（服务端注册数据源）选取，不再要求手打标识符；
 * 定位键按本模块已登记的效果关系边组一键生成，仍落回同一 MATCH_STRUCT 结构。
 */

/** 表选择（menu-admin.tables）：文本框保持可手改，右侧提供选择器。 */
export function TablePickerInput({
  value,
  title,
  disabled,
  placeholder,
  onChange,
}: {
  value: string
  title: string
  disabled?: boolean
  placeholder?: string
  onChange: (value: string) => void
}) {
  const [open, setOpen] = useState(false)
  return (
    <div className="input-group input-group-sm">
      <input
        className="form-control form-control-sm font-monospace"
        value={value}
        placeholder={placeholder ?? '点右侧选择或直接输入'}
        onChange={(event) => onChange(event.target.value.toUpperCase())}
      />
      <Button size="sm" onClick={() => setOpen(true)} disabled={disabled}>选择</Button>
      <UnifiedChooser
        open={open}
        title={title}
        source={{ kind: 'sourceKey', key: 'menu-admin.tables' }}
        mode="single"
        getRowId={(row) => String(row.T_ID)}
        onPick={(rows) => {
          const row = rows[0]
          if (row) onChange(String(row.T_ID))
          setOpen(false)
        }}
        onClose={() => setOpen(false)}
        searchPlaceholder="搜索表名/描述…"
        emptyText="没有匹配的表。"
      />
    </div>
  )
}

/** 列选择（menu-admin.columns）：按所属表取物理列，并用字段元数据补中文名；未选表时禁用。 */
export function ColumnPickerInput({
  table,
  value,
  title,
  disabled,
  names,
  onChange,
}: {
  table: string | null | undefined
  value: string
  title: string
  disabled?: boolean
  names?: BusinessNameLookup
  onChange: (value: string) => void
}) {
  const [open, setOpen] = useState(false)
  const tableId = (table ?? '').trim()
  const blocked = disabled || tableId === ''
  return (
    <div className="input-group input-group-sm">
      <input
        className="form-control form-control-sm font-monospace"
        value={value}
        placeholder={tableId === '' ? '先选择表' : '点右侧选择或直接输入'}
        title={names && tableId !== '' && value !== '' ? names.field(tableId, value) : undefined}
        onChange={(event) => onChange(event.target.value.toUpperCase())}
      />
      <Button size="sm" onClick={() => setOpen(true)} disabled={blocked}>选择</Button>
      <UnifiedChooser
        open={open}
        title={`${title}：${tableId}`}
        source={{ kind: 'sourceKey', key: 'menu-admin.columns', args: { tableId } }}
        mode="single"
        getRowId={(row) => String(row.COLUMN_NAME)}
        onPick={(rows) => {
          const row = rows[0]
          if (row) onChange(String(row.COLUMN_NAME))
          setOpen(false)
        }}
        onClose={() => setOpen(false)}
        searchPlaceholder="搜索列名/中文名…"
        emptyText="该表没有匹配的列。"
        columnRenderers={names
          ? { COLUMN_NAME: (row) => `${names.field(tableId, String(row.COLUMN_NAME))}` }
          : undefined}
      />
    </div>
  )
}

/** 已登记的效果关系边组（服务端按模块 + 目标表解析）。 */
interface RelationGroup {
  relationId: number
  relationName?: string | null
  sourceScope?: string | null
  keys: { fromTable: string; fromColumn: string; toTable: string; toColumn: string }[]
}

const MATCH_SCOPES = ['MASTER', 'DETAIL', 'TABLE'] as const

/**
 * 定位键编辑器：
 * - 「按已登记关系填入」把一条效果关系边组直接展开成定位键（选出来的键必然通过保存期校验）；
 * - 结构化行编辑逐键指定 目标列 ← 来源范围.来源列；
 * - 专家模式保留原始 JSON 文本，供批量粘贴/结构调整。
 */
export function MatchEditor({
  moduleId,
  masterTable,
  detailTable,
  targetTable,
  contextTable,
  value,
  scopeLabels,
  names,
  presets,
  onApplyToSiblings,
  onChange,
}: {
  moduleId: number
  masterTable: string | null
  detailTable: string | null
  targetTable: string
  contextTable?: string | null
  value: string | null
  scopeLabels: Record<string, string>
  names: BusinessNameLookup
  /** 本模块已用过的定位键组（按 JSON 去重，一处配好、多处复用）。 */
  presets: MatchGroupPreset[]
  /** 把当前定位键批量应用到同一目标表的其余步骤（返回应用了几步）。 */
  onApplyToSiblings?: (json: string) => void
  onChange: (value: string | null) => void
}) {
  const parsed = useMemo(() => parseMatchItems(value), [value])
  const invalid = value != null && value.trim() !== '' && parsed === null
  const [expert, setExpert] = useState(false)
  const items = parsed ?? []

  const target = targetTable.trim()
  const relationsQuery = useQuery({
    queryKey: ['module-business-config-relations', moduleId, target, contextTable ?? ''],
    queryFn: () => apiClient.get<RelationGroup[]>(
      `/admin/module-business-config/${moduleId}/relations`,
      { query: { targetTable: target, contextTable: contextTable ?? undefined } },
    ),
    enabled: moduleId > 0 && target !== '',
  })
  const groups = relationsQuery.data ?? []

  const scopeText = (scope: string) => labelWithCode((code) => scopeLabels[String(code).toUpperCase()] ?? String(code), scope)

  const commit = (next: MatchItem[]) => onChange(serializeMatchItems(next))
  const updateItem = (index: number, next: MatchItem) => commit(items.map((item, i) => (i === index ? next : item)))
  const removeItem = (index: number) => commit(items.filter((_, i) => i !== index))
  const appendItem = () => commit([...items, { target: '', source: { scope: 'DETAIL', field: null, table: null } }])
  const readableMatch = formatMatch(value, names)

  const applyRelation = (relationId: string) => {
    const group = groups.find((item) => String(item.relationId) === relationId)
    if (!group) return
    const merged = matchItemsFromRelation(group.keys, masterTable, detailTable)
    // 同一目标表的既有键保留：边组只覆盖同名键，避免误删用户已配的其它键。
    const byTarget = new Map(items.map((item) => [item.target.toUpperCase(), item]))
    for (const item of merged) byTarget.set(item.target.toUpperCase(), item)
    commit([...byTarget.values()])
  }

  const sourceTableOf = (scope: string | null | undefined) => {
    const key = (scope ?? '').trim().toUpperCase()
    if (key === 'MASTER') return masterTable
    if (key === 'DETAIL') return detailTable
    if (key === 'TABLE') return contextTable ?? null
    return null
  }

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2 mb-1">
        <span className="small text-secondary">
          定位键＝目标表的行按哪些键匹配本单（无键且无条件的跨表写入会被保存校验拒绝）
        </span>
        <div className="d-flex gap-1 align-items-center">
          <select
            className="form-select form-select-sm"
            style={{ width: 260 }}
            value=""
            disabled={target === '' || presets.length === 0}
            onChange={(event) => {
              const preset = presets.find((item) => item.json === event.target.value)
              if (preset) onChange(preset.json)
            }}
            title="复用本模块其它步骤已经配好的同一套定位键"
          >
            <option value="">
              {presets.length === 0 ? '本模块尚无可复用的定位键组' : `复用已配好的定位键组（${presets.length}）`}
            </option>
            {presets.map((preset) => (
              <option key={preset.json} value={preset.json}>
                {preset.labels.join('+')}（{preset.count} 步在用）
              </option>
            ))}
          </select>
          <select
            className="form-select form-select-sm"
            style={{ width: 260 }}
            value=""
            disabled={target === '' || groups.length === 0}
            onChange={(event) => applyRelation(event.target.value)}
            title={target === '' ? '先选择目标表' : '从已登记的效果关系边组填入'}
          >
            <option value="">
              {target === ''
                ? '先选择目标表'
                : groups.length === 0
                  ? '该目标表无已登记关系可填'
                  : `按已登记关系填入（${groups.length}）`}
            </option>
            {groups.map((group) => (
              <option key={group.relationId} value={String(group.relationId)}>
                {(group.relationName ?? `关系 ${group.relationId}`)}：
                {group.keys.map((key) => key.toColumn).join('+')}
              </option>
            ))}
          </select>
          <Button size="sm" icon={<IconPlus size={16} />} onClick={appendItem}>加键</Button>
          {onApplyToSiblings ? (
            <Button
              size="sm"
              variant="ghost"
              disabled={!value || target === ''}
              title="把当前定位键写回本动作中目标表相同的其余步骤"
              onClick={() => {
                if (value) onApplyToSiblings(value)
              }}
            >
              应用到同表步骤
            </Button>
          ) : null}
          <Button size="sm" variant="ghost" onClick={() => setExpert(!expert)}>
            {expert ? '结构化编辑' : '专家模式（JSON）'}
          </Button>
        </div>
      </div>

      {relationsQuery.isError ? (
        <div className="alert alert-warning py-1 small mb-2">
          关系边读取失败，可手动加键；保存时仍按服务端已登记关系校验。
        </div>
      ) : null}
      {invalid ? (
        <div className="alert alert-warning py-1 small mb-2">
          当前文本不是合法的定位键结构，已切换到专家模式；修正后方可回到结构化编辑。
        </div>
      ) : null}

      {expert || invalid ? (
        <textarea
          className="form-control form-control-sm font-monospace"
          rows={3}
          spellCheck={false}
          value={value ?? ''}
          placeholder='[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}]'
          onChange={(event) => onChange(event.target.value || null)}
        />
      ) : items.length === 0 ? (
        <div className="text-secondary small">未设置定位键。</div>
      ) : (
        <div className="d-flex flex-column gap-1">
          {items.map((item, index) => (
            <div className="row g-1 align-items-center" key={`${item.target}-${index}`}>
              <div className="col-4">
                <ColumnPickerInput
                  table={targetTable}
                  value={item.target}
                  title="目标列"
                  names={names}
                  onChange={(next) => updateItem(index, { ...item, target: next })}
                />
              </div>
              <div className="col-1 text-center small text-secondary">←</div>
              <div className="col-3">
                <select
                  className="form-select form-select-sm"
                  value={(item.source.scope ?? 'DETAIL').toUpperCase()}
                  onChange={(event) => updateItem(index, {
                    ...item,
                    source: { ...item.source, scope: event.target.value, table: null },
                  })}
                >
                  {MATCH_SCOPES.map((scope) => (
                    <option key={scope} value={scope}>{scopeText(scope)}</option>
                  ))}
                </select>
              </div>
              <div className="col-3">
                <ColumnPickerInput
                  table={sourceTableOf(item.source.scope)}
                  value={item.source.field ?? ''}
                  title="来源列"
                  names={names}
                  disabled={(item.source.scope ?? '').toUpperCase() === 'TABLE' && !contextTable}
                  onChange={(next) => updateItem(index, { ...item, source: { ...item.source, field: next || null } })}
                />
              </div>
              <div className="col-1 text-end">
                <Button
                  size="sm"
                  variant="ghost"
                  icon={<IconTrash size={14} />}
                  title="删除该键"
                  onClick={() => removeItem(index)}
                  aria-label="删除该键"
                />
              </div>
            </div>
          ))}
        </div>
      )}
      {!expert && !invalid && items.length > 0 && readableMatch ? (
        <div className="small text-secondary mt-1">读作：{readableMatch}</div>
      ) : null}
    </div>
  )
}
