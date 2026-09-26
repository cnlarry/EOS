import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import {
  DATASOURCE_MAX_COLUMNS,
  emptyDataSourceModel,
  isNumericType,
  resolvePhysicalTable,
  type DataSourceModel,
  type ExpressionModel,
  type ExpressionRegistry,
  type TableColumn,
  type TableRelations,
} from './expressionBuilder'
import { FieldPickerSelect, type FieldPickerOption } from './FieldPickerSelect'
import type { FieldEditorEndpoints, SetupLookup } from './FieldEditorForm'

interface BuilderProps {
  model: ExpressionModel
  onChange: (model: ExpressionModel) => void
  endpoints: FieldEditorEndpoints
  /** 构建器不可用（表达式为构建器不覆盖的形态或字段结构被锁定时）禁用全部控件。 */
  disabled: boolean
}

const EMPTY_COLUMNS: TableColumn[] = []

function columnOptions(columns: TableColumn[]): FieldPickerOption[] {
  return columns.map(column => ({
    value: column.name,
    label: `${column.description || column.name}(${column.name})`,
    meta: column.dataType,
  }))
}

/** 表列（构建器只认物理列：服务端按 sys.columns 校验存在性）。 */
function useTableColumns(table: string, endpoints: FieldEditorEndpoints) {
  const enabled = Boolean(table) && Boolean(endpoints.tableColumns)
  const query = useQuery({
    queryKey: ['field-admin', 'table-columns', table],
    queryFn: async () => (enabled ? (await endpoints.tableColumns!(table)) : []),
    enabled,
  })
  const columns = (query.data ?? EMPTY_COLUMNS).filter(column => !column.isVirtual)
  return { columns, isPending: query.isPending, isError: query.isError }
}

/** 转换函数构建器：下拉直接来自服务端注册表（白名单版本随响应返回，前端不硬编码函数名）。 */
export function ConvertFunctionBuilder({ model, onChange, endpoints, disabled }: BuilderProps) {
  const registryQuery = useQuery({
    queryKey: ['field-admin', 'expression-registry'],
    queryFn: async (): Promise<ExpressionRegistry> => endpoints.expressionRegistry
      ? endpoints.expressionRegistry()
      : { whiteListVersion: 0, convertFunctions: [] },
  })
  const registry = registryQuery.data
  const items = registry?.convertFunctions ?? []
  const current = model.function.trim()
  const registered = items.find(item => item.name.toLowerCase() === current.toLowerCase())
  // 已存值不在注册表内时仍原样列出，避免下拉把实际值显示成「未选择」
  const options: FieldPickerOption[] = [
    ...items.map(item => ({ value: item.name, label: item.name, meta: item.description })),
    ...(current && !registered ? [{ value: current, label: current, meta: '不在当前注册表内' }] : []),
  ]
  return (
    <div className="d-flex align-items-center gap-2">
      <div style={{ minWidth: 260 }}>
        <FieldPickerSelect
          options={options}
          value={model.function}
          onChange={fn => onChange({ ...model, function: fn })}
          placeholder={registryQuery.isPending ? '加载注册表…' : '选择注册表函数'}
          ariaLabel="转换函数（受控注册表）"
          disabled={disabled}
        />
      </div>
      {registered && <span className="text-secondary small">{registered.description}</span>}
      {registryQuery.isError && <span className="text-danger small">注册表加载失败，可直接在下方原始文本填写函数名。</span>}
    </div>
  )
}

interface VirtualExpressionBuilderProps extends BuilderProps {
  /** 当前字段所属表：本表引用与 QUERY_RELATION 白名单查询的基表。 */
  currentTable: string
}

/** 表显示名：`/admin/tables` 的 label 形如「客户资料 (CLIENT)」，取描述部分；取不到或与表名相同则回退表名。 */
function tableLabel(tables: SetupLookup[] | undefined, tableId: string): string {
  const entry = tables?.find(item => item.value.toLowerCase() === tableId.toLowerCase())
  const suffix = entry ? ` (${entry.value})` : ''
  const description = entry && suffix && entry.label.endsWith(suffix) ? entry.label.slice(0, -suffix.length) : entry?.label
  return description && description.toLowerCase() !== tableId.toLowerCase() ? `${description}（${tableId}）` : tableId
}

/**
 * 虚拟表达式构建器：来源按「表」展示，写入的仍是该段在 QUERY_RELATION 里的名字
 * （无别名时等于表名；有别名时写别名——运行期 SQL 用的是别名，写表名不可达）。
 * 关联条件来自 TABLES.QUERY_RELATION，随所选段只读展示，不随字段保存。
 */
export function VirtualExpressionBuilder({ model, onChange, endpoints, disabled, currentTable }: VirtualExpressionBuilderProps) {
  const relationsQuery = useQuery({
    queryKey: ['field-admin', 'table-relations', currentTable],
    queryFn: async (): Promise<TableRelations> => endpoints.tableRelations
      ? endpoints.tableRelations(currentTable)
      : { tableId: currentTable, ok: true, error: null, items: [] },
    enabled: Boolean(currentTable),
  })
  const tablesQuery = useQuery({
    queryKey: ['field-editor', 'tables'],
    queryFn: endpoints.tables ?? (async () => [] as SetupLookup[]),
    enabled: Boolean(endpoints.tables),
  })
  const relations = relationsQuery.data?.items ?? []
  const physicalTable = resolvePhysicalTable(currentTable, relations, model.table)
  const columnsQuery = useTableColumns(physicalTable, endpoints)
  const selectedJoin = relations.find(join => join.alias.toLowerCase() === model.table.trim().toLowerCase())
  const sourceOptions: FieldPickerOption[] = [
    { value: currentTable, label: tableLabel(tablesQuery.data, currentTable), meta: '本表' },
    ...relations.map(join => ({
      value: join.alias,
      label: join.alias.toLowerCase() === join.table.toLowerCase()
        ? tableLabel(tablesQuery.data, join.table)
        : `${tableLabel(tablesQuery.data, join.table)} AS ${join.alias}`,
      meta: join.alias.toLowerCase() === join.table.toLowerCase() ? '' : '别名',
    })),
  ]
  const relationBroken = relationsQuery.data != null && relationsQuery.data.ok === false
  return (
    <div className="d-flex flex-column gap-1">
      <div className="d-flex flex-wrap align-items-center gap-2">
        <div style={{ minWidth: 220 }}>
          <FieldPickerSelect
            options={sourceOptions}
            value={model.table}
            onChange={table => onChange({ ...model, table, column: '' })}
            placeholder="选择引用来源"
            ariaLabel="虚拟表达式引用来源"
            disabled={disabled}
          />
        </div>
        <div style={{ minWidth: 220 }}>
          <FieldPickerSelect
            options={columnOptions(columnsQuery.columns)}
            value={model.column}
            onChange={column => onChange({ ...model, column })}
            placeholder={columnsQuery.isPending && physicalTable ? '加载列…' : '选择引用列'}
            ariaLabel="虚拟表达式引用列"
            disabled={disabled || !physicalTable || columnsQuery.columns.length === 0}
          />
        </div>
        <span className="text-secondary small">
          生成：{model.table.trim() && model.column.trim() ? `${model.table.trim()}.${model.column.trim()}` : '—'}
        </span>
      </div>
      <div className="text-secondary small">
        {selectedJoin
          ? `关联条件：${selectedJoin.conditions.length > 0 ? selectedJoin.conditions.join(' AND ') : '（该段只有常量条件）'}`
          : '引用本表列，不需要关联条件。'}
      </div>
      {selectedJoin && selectedJoin.alias.toLowerCase() !== selectedJoin.table.toLowerCase() && (
        <div className="text-secondary small">
          该段在 QUERY_RELATION 里带别名：物理表 {selectedJoin.table}，表达式写入 {selectedJoin.alias}（运行期 SQL 用的是别名）。
        </div>
      )}
      {relationsQuery.isError && <div className="text-danger small">关联白名单加载失败，当前仅可选择本表列。</div>}
      {relationBroken && <div className="text-danger small">本表 QUERY_RELATION 不可解析（{relationsQuery.data?.error}），跨表引用一律被拒绝，仅可选择本表列。</div>}
      {relationsQuery.data?.ok !== false && relations.length === 0 && !relationsQuery.isPending && (
        <div className="text-secondary small">本表未配置 QUERY_RELATION，只能引用本表列。</div>
      )}
      {columnsQuery.isError && <div className="text-danger small">列清单加载失败，请确认 API 已重启（/admin/tables/{physicalTable}/columns）。</div>}
    </div>
  )
}

/** 数据源 SQL 构建器：受限单表 SELECT（列清单 + 单条常量过滤 + 单列排序）。 */
export function DataSourceSqlBuilder({ model, onChange, endpoints, disabled }: BuilderProps) {
  const dataSource = model.dataSource
  const [pickerOpen, setPickerOpen] = useState(false)
  const columnsQuery = useTableColumns(dataSource.table, endpoints)
  const columns = columnsQuery.columns

  const update = (patch: Partial<DataSourceModel>) => onChange({ ...model, dataSource: { ...dataSource, ...patch } })
  const toggleColumn = (name: string) => update({
    columns: dataSource.columns.includes(name)
      ? dataSource.columns.filter(column => column !== name)
      : [...dataSource.columns, name],
  })
  const clearConditions = () => update({ whereColumn: '', whereValue: '', whereValueIsString: true, orderColumn: '' })
  const atColumnLimit = dataSource.columns.length >= DATASOURCE_MAX_COLUMNS

  return (
    <div className="d-flex flex-column gap-2">
      <div className="d-flex align-items-center gap-2">
        <span className="text-secondary small" style={{ width: 72 }}>来源表</span>
        <input className="form-control form-control-sm" style={{ maxWidth: 260 }} readOnly value={dataSource.table} placeholder="点击选择" aria-label="数据源 SQL 来源表" />
        <Button size="sm" variant="secondary" disabled={disabled} onClick={() => setPickerOpen(true)}>选择</Button>
        {dataSource.table && <Button size="sm" variant="ghost" disabled={disabled} onClick={() => update({ ...emptyDataSourceModel() })}>清空</Button>}
      </div>
      <div className="d-flex align-items-start gap-2">
        <span className="text-secondary small" style={{ width: 72 }}>列清单</span>
        <div className="flex-grow-1">
          <div className="border rounded p-2" style={{ maxHeight: 148, overflowY: 'auto' }}>
            {!dataSource.table && <div className="text-secondary small">先选择来源表。</div>}
            {dataSource.table && columnsQuery.isPending && <div className="text-secondary small">加载列…</div>}
            {dataSource.table && !columnsQuery.isPending && columns.length === 0 && <div className="text-secondary small">该表没有可用物理列。</div>}
            {columns.map(column => (
              <label key={column.name} className="form-check form-check-inline me-3 mb-0">
                <input
                  className="form-check-input"
                  type="checkbox"
                  checked={dataSource.columns.includes(column.name)}
                  disabled={disabled || (!dataSource.columns.includes(column.name) && atColumnLimit)}
                  onChange={() => toggleColumn(column.name)}
                />
                <span className="form-check-label small">{column.description || column.name}<span className="text-secondary">({column.name})</span></span>
              </label>
            ))}
          </div>
          <div className="d-flex align-items-center gap-2 mt-1">
            <span className="text-secondary small">已选 {dataSource.columns.length} 列（服务端上限 {DATASOURCE_MAX_COLUMNS}）</span>
            {dataSource.columns.length > 0 && <Button size="sm" variant="ghost" disabled={disabled} onClick={() => update({ columns: [] })}>清空列</Button>}
          </div>
        </div>
      </div>
      <div className="d-flex align-items-center gap-2">
        <span className="text-secondary small" style={{ width: 72 }}>过滤</span>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 260 }}
          aria-label="数据源 SQL 过滤列"
          value={dataSource.whereColumn}
          disabled={disabled || !dataSource.table}
          onChange={event => {
            const name = event.target.value
            const column = columns.find(item => item.name === name)
            update({ whereColumn: name, whereValue: '', whereValueIsString: column ? !isNumericType(column.dataType) : true })
          }}
        >
          <option value="">无过滤条件</option>
          {columns.map(column => <option key={column.name} value={column.name}>{column.description || column.name}({column.name})</option>)}
        </select>
        <input
          className="form-control form-control-sm"
          style={{ maxWidth: 220 }}
          aria-label="数据源 SQL 过滤值"
          value={dataSource.whereValue}
          placeholder={dataSource.whereValueIsString ? "文本值（自动加引号）" : '数字值'}
          disabled={disabled || !dataSource.whereColumn}
          onChange={event => update({ whereValue: event.target.value })}
        />
        <span className="text-secondary small">仅支持「列 = 常量」单条件</span>
      </div>
      <div className="d-flex align-items-center gap-2">
        <span className="text-secondary small" style={{ width: 72 }}>排序</span>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 260 }}
          aria-label="数据源 SQL 排序列"
          value={dataSource.orderColumn}
          disabled={disabled || !dataSource.table}
          onChange={event => update({ orderColumn: event.target.value })}
        >
          <option value="">不排序</option>
          {columns.map(column => <option key={column.name} value={column.name}>{column.description || column.name}({column.name})</option>)}
        </select>
        <select
          className="form-select form-select-sm"
          style={{ maxWidth: 90 }}
          aria-label="数据源 SQL 排序方向"
          value={dataSource.orderDirection}
          disabled={disabled || !dataSource.orderColumn}
          onChange={event => update({ orderDirection: event.target.value as 'ASC' | 'DESC' })}
        >
          <option value="ASC">升序</option>
          <option value="DESC">降序</option>
        </select>
        {(dataSource.whereColumn || dataSource.orderColumn) && (
          <Button size="sm" variant="ghost" disabled={disabled} onClick={clearConditions}>清除条件</Button>
        )}
      </div>
      {columnsQuery.isError && <div className="text-danger small">列清单加载失败，请确认 API 已重启（/admin/tables/{dataSource.table}/columns）。</div>}
      {pickerOpen && (
        <UnifiedChooser
          open
          title="选择数据源 SELECT 的来源表"
          source={{ kind: 'sourceKey', key: 'field-admin.tables' }}
          mode="single"
          onPick={rows => {
            const row = rows[0]
            const table = String(row.T_ID ?? '')
            update({ ...emptyDataSourceModel(), table })
            setPickerOpen(false)
          }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </div>
  )
}
