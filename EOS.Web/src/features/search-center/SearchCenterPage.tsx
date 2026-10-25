import { IconEye, IconFileExport, IconHistory, IconRefresh } from '@tabler/icons-react'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpColumnSelector } from '../../components/common/ErpColumnSelector'
import { checkboxSelectColumn } from '../../components/common/erpCheckboxSelectColumn'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { ErpQueryBuilder } from '../../components/common/ErpQueryBuilder'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { emptyQueryCondition, type QueryCondition } from '../../components/common/queryCondition'
import { describeApiError } from '../../lib/errors'
import { useListSelection } from '../../hooks/useListSelection'
import { useColumnWidthSaver } from '../../hooks/useColumnWidthSaver'
import type { ColumnDef, SortingState } from '../../lib/tanstackTable'
import { apiClient } from '../../services/api'
import { alignClass, formatFieldValue } from '../document-workbench/fieldFormat'
import { workbenchView } from '../document-workbench/workbenchPath'
import {
  pushRecentSearch,
  readRecentSearches,
  type RecentSearch,
} from './searchCenterState'

interface SearchableModule { moduleId: number; title: string; masterTable: string; detailTable: string | null; searchMaster: boolean; searchDetail: boolean }
interface SearchField { key: string; label: string; dataType: string; width?: number; align?: string | null; headerAlign?: string | null; displayFormat?: string | null; numericRole?: string | null }
interface SearchDefinition { moduleId: number; title: string; table: string; fields: SearchField[]; columns: SearchField[]; pkOrder: string[]; masterPkOrder: string[] }
interface SearchResult { items: Record<string, unknown>[]; total: number; page: number; pageSize: number }
/** 统一工作台的列配置（个人 current / 系统 defaults）：查询中心的列顺序与显隐沿用同一份。 */
interface ColumnSetting { key: string; label: string; isVisible: boolean }
interface ColumnSettings { master: ColumnSetting[]; detail: ColumnSetting[] }

/** 查询中心入口模块（菜单 34）：它自己没有检索面，路径里出现它时按"未选择模块"处理。 */
const ENTRY_MODULE_ID = 34

/**
 * `?find=字段:值`：从统一工作台的单元格右键进来时带着"要查哪个字段的哪个值"。
 * 用 contains（包含）而不是等于——摸清一个料号出现在哪些单据里，用包含更稳
 * （值可能带前缀/后缀空格或它在别的字段里是更长编号的一部分）。
 */
function conditionsFromFind(raw: string | null): QueryCondition[] {
  if (!raw) return []
  const index = raw.indexOf(':')
  if (index <= 0) return []
  const field = raw.slice(0, index)
  const value = raw.slice(index + 1).trim()
  return value ? [{ field, operator: 'contains', value, valueTo: '', logic: 'and' }] : []
}

/**
 * 查询中心：跨模块、跨单据的即席倒查。
 *
 * 交互与统一工作台同一套：模块/表选择在命令栏左侧、关键字用 ErpSearchBox（防抖）、
 * 结构化条件用 ErpQueryBuilder、结果用 ErpListGrid（服务端排序 + 双击打开单据）。
 * 与工作台的区别只有一个：**不先进某个模块**，而是在这里挑一个可检索的模块。
 */
export function SearchCenterPage() {
  const { moduleId = '' } = useParams()
  const openTab = useOpenTab()
  const [searchParams, setSearchParams] = useSearchParams()
  const findParam = searchParams.get('find')
  // 模块来源：查询串 `?module=`（页面内切换）优先，其次路径段（兼容 `?find=` 那类直达深链）。
  const moduleParam = searchParams.get('module')
  const [selectedModule, setSelectedModule] = useState(() => {
    if (moduleParam) return moduleParam
    return moduleId && moduleId !== String(ENTRY_MODULE_ID) ? moduleId : ''
  })
  const [requestedTable, setRequestedTable] = useState<'master' | 'detail'>('master')
  const [conditions, setConditions] = useState<QueryCondition[]>(() => conditionsFromFind(findParam))
  const [draftConditions, setDraftConditions] = useState<QueryCondition[]>([emptyQueryCondition()])
  const [queryOpen, setQueryOpen] = useState(false)
  const [columnsOpen, setColumnsOpen] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [sort, setSort] = useState<SortingState>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [exporting, setExporting] = useState(false)
  const [recentOpen, setRecentOpen] = useState(false)
  const [recent, setRecent] = useState<RecentSearch[]>(() => readRecentSearches())
  const [visibleKeys, setVisibleKeys] = useState<string[] | null>(null)

  const modules = useQuery({ queryKey: ['search-center', 'modules'], queryFn: () => apiClient.get<SearchableModule[]>('/search-center/modules') })
  const activeModule = useMemo(
    () => modules.data?.find(item => String(item.moduleId) === selectedModule),
    [modules.data, selectedModule],
  )

  // 表的默认值按**检索位**派生：模块只登记了明细位（如 1505 入库单）时主表位为 0，
  // 固定按 master 查会拿到 404（"这个模块不能用主表检索"）。用户手动切表仍然优先。
  const table = activeModule
    ? (!activeModule.searchMaster && activeModule.searchDetail ? 'detail'
      : !activeModule.searchDetail ? 'master'
        : requestedTable)
    : requestedTable

  const definition = useQuery({
    queryKey: ['search-center', activeModule?.moduleId, table, 'definition'],
    queryFn: () => apiClient.get<SearchDefinition>(`/search-center/${activeModule?.moduleId}/definition?table=${table === 'detail' ? activeModule?.detailTable : activeModule?.masterTable}`),
    enabled: activeModule != null,
  })

  // 条件只保留当前表里存在的字段：切模块/切表后沿用条件，失效的字段静默剔除，
  // 而不是把整次查询打成 400。
  const fieldKeys = useMemo(
    () => new Set((definition.data?.fields ?? []).map(field => field.key.toLowerCase())),
    [definition.data],
  )
  const effectiveConditions = useMemo(
    () => conditions.filter(condition => !condition.field || fieldKeys.has(condition.field.toLowerCase())),
    [conditions, fieldKeys],
  )

  const result = useQuery({
    queryKey: ['search-center', activeModule?.moduleId, table, 'query', keyword, effectiveConditions, sort, page, pageSize],
    queryFn: () => apiClient.post<SearchResult>(
      `/search-center/${activeModule?.moduleId}/query?page=${page}&pageSize=${pageSize}`,
      {
        table: table === 'detail' ? activeModule?.detailTable : activeModule?.masterTable,
        conditions: effectiveConditions.map(condition => ({
          field: condition.field,
          operator: condition.operator,
          value: condition.value,
          valueTo: condition.valueTo,
          logic: condition.logic,
        })),
        keyword: keyword || null,
        // 排序与统一工作台同一形态：多列、逗号分隔（表头 Shift+点击可叠多列），
        // 只取第一列会让"界面显示两列排序、实际只按一列排"。
        sortFields: sort.length > 0 ? sort.map(item => item.id).join(',') : null,
        sortDirections: sort.length > 0 ? sort.map(item => (item.desc ? 'desc' : 'asc')).join(',') : null,
      },
    ),
    // 选中模块即查一次：这是列表页，进来就该看到数据，而不是"请先点查询"。
    enabled: activeModule != null && definition.isSuccess,
    placeholderData: keepPreviousData,
  })

  // 从工作台右键带过来的 `?find=字段:值`：换一次值就重置条件（同标签内再次右键也能生效）。
  useEffect(() => {
    if (!findParam) return
    setConditions(conditionsFromFind(findParam))
    setPage(1)
  }, [findParam])

  // 换模块 / 换表时回到"跟随统一工作台的列配置"（本地不存显隐：同一张表不该有两份列状态）。
  useEffect(() => {
    setVisibleKeys(null)
  }, [activeModule, table])

  // 最近查询：每完成一次成功查询记一条（同条件只留最近一次）。
  const dataUpdatedAt = result.dataUpdatedAt
  useEffect(() => {
    if (!activeModule || !result.isSuccess || dataUpdatedAt === 0) return
    setRecent(pushRecentSearch({ moduleId: activeModule.moduleId, table, keyword, conditions: effectiveConditions }))
  }, [activeModule, dataUpdatedAt, result.isSuccess, table, keyword, effectiveConditions])

  // 列顺序与显隐：默认沿用统一工作台的列配置（`/column-editor` 的 current）——同一张表在
  // 列表与查询中心里应该按同一个顺序、显示同一批列。用户点过「结果列」后以本地选择为准。
  const columnSettings = useQuery({
    queryKey: ['search-center', activeModule?.moduleId, 'column-editor'],
    queryFn: () => apiClient.get<{ current?: ColumnSettings; defaults?: ColumnSettings }>(`/document-workbench/${activeModule?.moduleId}/column-editor`),
    enabled: activeModule != null,
  })

  /** 工作台列配置里的可见列键（按配置顺序）：`current` = 个人当前，`defaults` = 系统默认。 */
  const workbenchColumnSettings = useCallback((which: 'current' | 'defaults') => {
    const settings = columnSettings.data?.[which]
    const list = (table === 'detail' ? settings?.detail : settings?.master) ?? []
    return list.filter(setting => setting.isVisible).map(setting => setting.key)
  }, [columnSettings.data, table])

  const displayColumns = useMemo(() => {
    const shown = definition.data?.columns ?? []
    if (!visibleKeys || visibleKeys.length === 0) return shown
    // 用户点过「结果列」：**按他排的顺序**输出（弹窗里的上移/下移就是改这个数组），
    // 勾了但工作台默认不显示的列从字段全集里补上。
    const all = definition.data?.fields ?? []
    const byKey = new Map([...shown, ...all].map(column => [column.key, column]))
    return visibleKeys
      .map(key => byKey.get(key))
      .filter((column): column is SearchField => Boolean(column))
  }, [definition.data, visibleKeys])

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(
    () => [
      // 列表第一列永远是复选框（与统一工作台同一列定义）
      checkboxSelectColumn<Record<string, unknown>>(),
      ...displayColumns.map((column): ColumnDef<Record<string, unknown>, unknown> => {
        const format = (value: unknown) => formatFieldValue(value, column.dataType, column.displayFormat ?? null, { numericRole: column.numericRole })
        return {
          id: column.key,
          accessorKey: column.key,
          header: column.label,
          enableSorting: true,
          meta: {
            // 表头/单元格对齐、列宽、格式化、复制文本全部按工作台同一套元数据口径
            className: alignClass(column.headerAlign),
            cellClassName: alignClass(column.align, column.dataType),
            minWidth: column.width || undefined,
            dataType: column.dataType,
            truncate: (column.dataType ?? '').toLowerCase() !== 'bit',
            title: ({ value }) => format(value) || undefined,
            copyText: ({ value }) => format(value) || '—',
          },
          cell: (info) => {
            if ((column.dataType ?? '').toLowerCase() === 'bit') {
              return <input type="checkbox" className="form-check-input" checked={Boolean(info.getValue())} disabled aria-label={column.label} />
            }
            return format(info.getValue()) || '—'
          },
        }
      }),
    ],
    [displayColumns],
  )

  const switchModule = (next: string) => {
    setSelectedModule(next)
    setRequestedTable('master')
    setPage(1)
    setSort([])
    // 只改查询串、路径永远是 /search-center：模块号是页面内的检索目标，不是页面身份
    // （写进路径会让每个模块都变成一个不同的地址，标签与标题解析都要跟着特判）。
    setSearchParams(current => {
      if (next) current.set('module', next)
      else current.delete('module')
      current.delete('find')
      return current
    }, { replace: true })
  }

  const applyConditions = (next: QueryCondition[]) => {
    setConditions(next)
    setPage(1)
    setQueryOpen(false)
  }

  /** 从最近查询恢复：同样只改查询串 */
  const restoreRecent = (item: RecentSearch) => {
    setSelectedModule(String(item.moduleId))
    setRequestedTable(item.table === 'detail' ? 'detail' : 'master')
    setKeyword(item.keyword)
    setConditions(item.conditions)
    setPage(1)
    setSort([])
    setSearchParams(current => {
      current.set('module', String(item.moduleId))
      current.delete('find')
      return current
    }, { replace: true })
  }

  /** 结果行 → 单据：主表行用自身主键，明细行用主表主键（两条都得在结果里有值）。 */
  const openRecord = useCallback((row: Record<string, unknown>) => {
    if (!activeModule || !definition.data) return
    const order = table === 'detail' ? definition.data.masterPkOrder : definition.data.pkOrder
    if (!order || order.length === 0) return
    if (!order.every(column => column in row)) return
    openTab(workbenchView(activeModule.moduleId, order.map(column => String(row[column] ?? ''))), { locate: true })
  }, [activeModule, definition.data, openTab, table])

  /** 行键：与选择态同一口径（主键列都在结果里时按主键拼，否则退化到整行内容，仍保证稳定）。 */
  const rowIdOf = useCallback((row: Record<string, unknown>) => {
    const order = definition.data?.pkOrder
    if (order && order.length > 0 && order.every(column => column in row)) {
      return order.map(column => String(row[column] ?? '')).join('|')
    }
    return JSON.stringify(row)
  }, [definition.data])

  const selection = useListSelection<Record<string, unknown>>({ rows: result.data?.items, getId: rowIdOf })

  // 列宽写回与工作台同一条链路（同一端点、同一队列），写完刷新定义让新宽度回到列上。
  const queryClient = useQueryClient()
  const { saveColumnWidth } = useColumnWidthSaver(activeModule?.moduleId ?? null, () => {
    void queryClient.invalidateQueries({ queryKey: ['search-center', activeModule?.moduleId, table, 'definition'] })
  })

  const canOpenRecord = Boolean(definition.data && (table === 'detail' ? definition.data.masterPkOrder.length > 0 : definition.data.pkOrder.length > 0))

  const handleExport = async () => {
    if (!activeModule || !definition.data || table !== 'master') return
    setExporting(true)
    try {
      // 主表导出直接复用工作台的导出管线（同一套条件白名单、模块过滤与数据范围）。
      const exportColumns = definition.data.columns.map(column => column.key).join(',')
      const blob = await apiClient.postFile(
        `/document-workbench/${activeModule.moduleId}/export`,
        { conditions: effectiveConditions },
        { query: { keyword: keyword || undefined, format: 'csv', columns: exportColumns, sortFields: sort.length > 0 ? sort.map(item => item.id).join(',') : undefined, sortDirections: sort.length > 0 ? sort.map(item => (item.desc ? 'desc' : 'asc')).join(',') : undefined } },
      )
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${definition.data.title}.csv`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
    } catch (error) {
      window.alert(error instanceof Error ? `导出失败：${error.message}` : '导出失败。')
    } finally {
      setExporting(false)
    }
  }

  if (modules.isPending) return <LoadingState label="正在加载查询中心…" />
  if (modules.isError) return <ErrorState message="查询中心加载失败。" onRetry={() => void modules.refetch()} />

  const tables = activeModule
    ? [
        ...(activeModule.searchMaster ? [{ value: 'master' as const, label: activeModule.masterTable }] : []),
        ...(activeModule.searchDetail && activeModule.detailTable ? [{ value: 'detail' as const, label: activeModule.detailTable }] : []),
      ]
    : []

  return (
    <div className="erp-base-page erp-page-fill">
      <ErpListGrid
        ariaLabel="查询中心"
        listId={activeModule ? `search-center-${activeModule.moduleId}-${table}` : 'search-center'}
        search={(
          <div className="erp-search-scope">
            <select
              className="form-select form-select-sm erp-search-scope-select"
              aria-label="查询模块"
              value={activeModule ? String(activeModule.moduleId) : ''}
              onChange={(event) => switchModule(event.target.value)}
            >
              <option value="">选择要查询的模块…</option>
              {modules.data?.map(item => <option key={item.moduleId} value={item.moduleId}>{item.title}</option>)}
            </select>
            {tables.length > 1 && (
              <select
                className="form-select form-select-sm erp-search-scope-select"
                aria-label="数据表"
                value={table}
                onChange={(event) => { setRequestedTable(event.target.value as 'master' | 'detail'); setPage(1); setSort([]) }}
              >
                {tables.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
              </select>
            )}
            {activeModule && <ErpSearchBox value={keyword} onChange={(next) => { setKeyword(next); setPage(1) }} debounceMs={400} placeholder="在各文本字段中查找…" ariaLabel="关键字搜索" />}
          </div>
        )}
        actions={(
          <ErpCommandBar iconOnly items={[
            { action: 'view', icon: <IconEye size={16} />, visible: selection.selectedRows.length === 1, title: '打开选中的单据', onClick: () => openRecord(selection.selectedRows[0]!) },
            { action: 'query', visible: activeModule != null, badge: effectiveConditions.length, title: effectiveConditions.length ? `高级查询 (${effectiveConditions.length})` : '高级查询', onClick: () => { setDraftConditions(effectiveConditions.length ? effectiveConditions.map(item => ({ ...item })) : [emptyQueryCondition()]); setQueryOpen(true) } },
            { action: 'columns', visible: activeModule != null, onClick: () => setColumnsOpen(true) },
            { action: 'export', icon: <IconFileExport size={16} />, visible: activeModule != null && table === 'master', loading: exporting, title: table === 'master' ? '导出当前查询结果' : '明细结果暂不支持导出', disabled: definition.isPending, onClick: () => void handleExport() },
            { action: 'recent', icon: <IconHistory size={16} />, visible: recent.length > 0, title: '最近查询', onClick: () => setRecentOpen(open => !open) },
            { action: 'refresh', icon: <IconRefresh size={16} />, visible: activeModule != null, onClick: () => void result.refetch() },
          ]} />
        )}
        header={undefined}        pagination={{ total: result.data?.total ?? 0, page, pageSize, onPageChange: setPage, pageSizes: [50, 100, 200], onPageSizeChange: (size) => { setPageSize(size); setPage(1) } }}
        sorting={{ mode: 'server', value: sort, onChange: (next) => { setSort(next); setPage(1) } }}
        selection={selection.selection}
        activeRowId={selection.activeRowId}
        onRowClick={selection.onRowClick}
        tableProps={{
          // 列宽与工作台同一条链路：拖拽即写个人层（SYS_USER_COLUMN.F_WIDTH），
          // 所以这里关掉本地持久化，否则同一列在本地与服务端各存一份。
          persistResize: false,
          onColumnResize: (columnKey, width) => { saveColumnWidth(table === 'detail', columnKey, width) },
        }}
        columns={columns}
        rows={result.data?.items}
        rowKey={(row) => rowIdOf(row)}
        loading={activeModule != null && (definition.isPending || (definition.isSuccess && result.isPending))}
        loadingLabel="正在查询…"
        fetching={result.isFetching}
        error={result.isError ? describeApiError(result.error, '查询失败。') : definition.isError ? '检索定义加载失败。' : null}
        onRetry={() => { void result.refetch(); if (definition.isError) void definition.refetch() }}
        onRowDoubleClick={canOpenRecord ? openRecord : undefined}
        empty={!activeModule
          ? <EmptyState title="先选一个模块" description="查询中心按模块检索：选一个模块，就能按它的字段跨单据查找记录。" />
          : <EmptyState title="没有匹配的记录" description="试着放宽条件、换个关键字，或换一张表再查。" />}
        before={recentOpen && recent.length > 0 ? (
          <div className="erp-search-recent">
            {recent.map(item => (
              <button
                key={`${item.moduleId}|${item.table}|${item.at}`}
                type="button"
                className="dropdown-item"
                onClick={() => {
                  setRecentOpen(false)
                  restoreRecent(item)
                }}
              >
                {modules.data?.find(module => module.moduleId === item.moduleId)?.title ?? item.moduleId}
                <span className="text-secondary small"> · {item.table === 'detail' ? '明细表' : '主表'}{item.keyword ? ` · “${item.keyword}”` : ''}{item.conditions.length ? ` · ${item.conditions.length} 个条件` : ''}</span>
              </button>
            ))}
          </div>
        ) : undefined}
      />

      {queryOpen && (
        <ErpQueryBuilder
          open
          title="查询条件"
          fields={(definition.data?.fields ?? []).map(field => ({ key: field.key, label: field.label }))}
          conditions={draftConditions}
          onChange={setDraftConditions}
          onApply={() => applyConditions(draftConditions.filter(condition => condition.field))}
          onClear={() => { setKeyword(''); applyConditions([]) }}
          onClose={() => setQueryOpen(false)}
        />
      )}

      {columnsOpen && definition.data && activeModule && (
        <ErpColumnSelector
          open
          title="结果列"
          groups={[{
            id: 'result',
            label: '结果列',
            fields: (definition.data.fields ?? []).map(column => ({ key: column.key, label: column.label })),
            // 「当前列」= 这一屏实际显示的那些（已按工作台列配置排好序）；
            // 「默认列」= 工作台的系统默认列（`SYS_DEFAULT_COLUMN`），所以「恢复默认」回到的是工作台默认。
            visibleKeys: displayColumns.map(column => column.key),
            defaultKeys: workbenchColumnSettings('defaults'),
          }]}
          onClose={() => setColumnsOpen(false)}
          onSave={(selection) => {
            setVisibleKeys(selection.result ?? [])
            setColumnsOpen(false)
          }}
        />
      )}
    </div>
  )
}
