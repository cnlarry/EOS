import { IconArrowDown, IconArrowUp, IconTrash } from '@tabler/icons-react'
import { useEffect, useRef, useState, type UIEvent } from 'react'
import { Button } from '../ui/Button'
import { Modal } from '../ui/Modal'
import { ErrorState, LoadingState } from './AsyncState'
import { loadChooserSource, type UnifiedChooserRow, type UnifiedChooserSource } from './chooserSource'

type SortDirection = 'asc' | 'desc'

interface SelectedField {
  key: string
  dir: SortDirection
}

/**
 * 字段选择器已选串格式（与既有保存值兼容）：
 * - semicolon：分号分隔，key 取字段末段（菜单多选，如 `F_ID;F_ID2`）；
 * - comma：逗号分隔，key 保持原文（报表排序/分组，如 `T_ID.F_ID,T_ID2.F_ID2`）；
 * - comma-dir：逗号分隔 + 每字段方向（菜单排序，如 `F_ID ASC,F_ID2 DESC`）。
 */
export type ErpFieldChooserValueFormat = 'semicolon' | 'comma' | 'comma-dir'

export interface ErpFieldChooserProps<T extends UnifiedChooserRow = UnifiedChooserRow> {
  open: boolean
  title: string
  source: UnifiedChooserSource
  /** sort：右栏每行带升/降序切换（菜单排序字段场景）；multi：仅顺序 */
  mode: 'multi' | 'sort'
  /** 行键（左栏勾选即加入右栏的稳定标识），如菜单用 F_ID、报表用 T_ID.F_ID */
  getRowId: (row: T, index: number) => string
  /** 回显串（兼容旧格式，打开时按 valueFormat 解析） */
  value: string
  valueFormat: ErpFieldChooserValueFormat
  onSave: (value: string) => void
  onClose: () => void
  searchPlaceholder?: string
  emptyText?: string
}

const PAGE_SIZE = 50

/** 字段 token 归一：去除方括号限定，取最后一段（菜单旧值可能是 [T_ID].[F_ID] 或 T_ID.F_ID）。 */
function bareFieldToken(token: string): string {
  return token.replace(/^\[|\]$/g, '').split('.').pop() ?? ''
}

function parseValue(value: string, format: ErpFieldChooserValueFormat): SelectedField[] {
  return value
    .split(format === 'semicolon' ? ';' : ',')
    .map(part => part.trim())
    .filter(Boolean)
    .map(part => {
      if (format === 'comma-dir') {
        const [field, dir] = part.split(/\s+/)
        const key = field ? bareFieldToken(field) : ''
        if (!key) return null
        return { key, dir: dir?.toLowerCase() === 'desc' ? ('desc' as const) : ('asc' as const) }
      }
      if (format === 'semicolon') {
        const key = bareFieldToken(part)
        return key ? { key, dir: 'asc' as const } : null
      }
      return { key: part, dir: 'asc' as const }
    })
    .filter((item): item is SelectedField => item !== null)
}

function serializeValue(items: SelectedField[], format: ErpFieldChooserValueFormat): string {
  if (format === 'comma-dir') {
    return items.map(item => `${item.key} ${item.dir.toUpperCase()}`).join(',')
  }
  return items.map(item => item.key).join(format === 'semicolon' ? ';' : ',')
}

/**
 * 统一字段选择器（中文名：字段选择器）：
 * - 在统一选择器基础上改良为「左待选 / 右已选」左右双栏，解决下方已选条内容裁切；
 * - 左栏 = 服务端字段源（sourceKey/formField/loader 复用统一选择器管线，滚动加载 + 关键字搜索），
 *   勾选/点行即加入右栏；
 * - 右栏 = 已选字段（有序）：行内上移/下移/移除，sort 模式每行带升/降序切换；
 * - value/onSave 按 valueFormat 序列化，与既有菜单/报表字段串格式兼容。
 */
export function ErpFieldChooser<T extends UnifiedChooserRow = UnifiedChooserRow>({
  open,
  title,
  source,
  mode,
  getRowId,
  value,
  valueFormat,
  onSave,
  onClose,
  searchPlaceholder = '输入字段名/描述，回车查询',
  emptyText = '没有可用的字段。',
}: ErpFieldChooserProps<T>) {
  const [keyword, setKeyword] = useState('')
  const [rows, setRows] = useState<T[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [selected, setSelected] = useState<SelectedField[]>([])

  const requestSeq = useRef(0)
  const rowCache = useRef<Map<string, T>>(new Map())

  const fetchPage = async (targetPage: number, keywordValue = keyword) => {
    const seq = ++requestSeq.current
    setLoading(true)
    setError(null)
    try {
      const result = await loadChooserSource(source, {
        keyword: keywordValue.trim() || undefined,
        page: targetPage,
        pageSize: PAGE_SIZE,
      })
      if (seq !== requestSeq.current) return
      const nextRows = result.rows as T[]
      result.rows.forEach((row, index) => {
        const key = getRowId(row as T, index)
        if (key) rowCache.current.set(key, row as T)
      })
      setRows(targetPage === 1 ? nextRows : current => [...current, ...nextRows])
      setTotal(result.total)
      setPage(targetPage)
    } catch (cause) {
      if (seq !== requestSeq.current) return
      setError(cause instanceof Error ? cause.message : '字段加载失败。')
    } finally {
      if (seq === requestSeq.current) setLoading(false)
    }
  }

  const loadMore = async () => {
    if (loading || loadingMore || rows.length >= total) return
    const seq = ++requestSeq.current
    setLoadingMore(true)
    setError(null)
    try {
      const result = await loadChooserSource(source, {
        keyword: keyword.trim() || undefined,
        page: page + 1,
        pageSize: PAGE_SIZE,
      })
      if (seq !== requestSeq.current) return
      const nextRows = result.rows as T[]
      result.rows.forEach((row, index) => {
        const key = getRowId(row as T, index)
        if (key) rowCache.current.set(key, row as T)
      })
      setRows(current => [...current, ...nextRows])
      setTotal(result.total)
      setPage(current => current + 1)
    } catch (cause) {
      if (seq !== requestSeq.current) return
      setError(cause instanceof Error ? cause.message : '字段加载失败。')
    } finally {
      if (seq === requestSeq.current) setLoadingMore(false)
    }
  }

  useEffect(() => {
    if (!open) return
    setKeyword('')
    setRows([])
    setTotal(0)
    setPage(1)
    setError(null)
    setSelected(parseValue(value, valueFormat))
    rowCache.current = new Map()
    void fetchPage(1, '')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, value, valueFormat])

  const handleScroll = (event: UIEvent<HTMLDivElement>) => {
    const el = event.currentTarget
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 48) void loadMore()
  }

  const toggle = (key: string) => {
    setSelected(current => current.some(item => item.key === key)
      ? current.filter(item => item.key !== key)
      : [...current, { key, dir: 'asc' }])
  }

  const toggleDir = (key: string) => {
    setSelected(current => current.map(item => item.key === key ? { ...item, dir: item.dir === 'asc' ? 'desc' : 'asc' } : item))
  }

  const move = (key: string, delta: -1 | 1) => {
    setSelected(current => {
      const index = current.findIndex(item => item.key === key)
      const target = index + delta
      if (index < 0 || target < 0 || target >= current.length) return current
      const next = [...current]
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
  }

  const handleSave = () => {
    onSave(serializeValue(selected, valueFormat))
    onClose()
  }

  if (!open) return null

  const selectedKeys = new Set(selected.map(item => item.key))
  const describe = (key: string): { desc: string; type: string } => {
    const row = rowCache.current.get(key)
    const desc = typeof row?.F_DESC === 'string' ? row.F_DESC.trim() : ''
    const type = typeof row?.F_TYPE === 'string' ? row.F_TYPE : ''
    return { desc, type }
  }

  return (
    <Modal
      title={`${title}${mode === 'sort' ? '（可升/降序）' : ''}`}
      onClose={onClose}
      dialogClassName="erp-dialog-lg erp-field-chooser-dialog"
      footer={<>
        <Button onClick={onClose}>取消</Button>
        <Button variant="primary" onClick={handleSave}>确认</Button>
      </>}
    >
            <div className="erp-field-chooser-layout">
              <div className="erp-field-chooser-pane">
                <div className="erp-field-chooser-search input-group erp-chooser-query">
                  <input
                    className="form-control"
                    value={keyword}
                    placeholder={searchPlaceholder}
                    onChange={event => setKeyword(event.target.value)}
                    onKeyDown={event => { if (event.key === 'Enter') void fetchPage(1) }}
                  />
                  <Button variant="primary" loading={loading} onClick={() => void fetchPage(1)}>查询</Button>
                </div>
                <div className="small text-secondary mb-1">
                  待选字段（共 {total} 个{rows.length < total ? `，已加载 ${rows.length} 个` : ''}），勾选加入右侧
                </div>
                <div className="erp-field-picker-list erp-field-chooser-list" aria-label="待选字段列表" onScroll={handleScroll}>
                  {error ? <ErrorState message={error} onRetry={() => void fetchPage(1)} /> : null}
                  {!error && loading && rows.length === 0 ? <LoadingState label="正在加载字段…" /> : null}
                  {!error && !loading && rows.length === 0 ? (
                    <div className="text-secondary py-4 text-center">{emptyText}</div>
                  ) : null}
                  {!error && rows.length > 0 ? rows.map((row, index) => {
                    const key = getRowId(row, index)
                    const { desc, type } = describe(key)
                    return (
                      <label key={key} className="erp-field-picker-option">
                        <input
                          className="form-check-input m-0"
                          type="checkbox"
                          aria-label={`选择字段 ${key}`}
                          checked={selectedKeys.has(key)}
                          onChange={() => toggle(key)}
                        />
                        <span className="erp-menu-table-id">{key}</span>
                        {desc ? <span className="erp-field-picker-desc">{desc}</span> : null}
                        {type ? <span className="text-secondary small text-nowrap">{type}</span> : null}
                      </label>
                    )
                  }) : null}
                  {loadingMore ? <div className="text-secondary small text-center py-2">正在加载更多…</div> : null}
                </div>
              </div>
              <div className="erp-field-chooser-pane">
                <div className="erp-field-chooser-search" aria-hidden="true" />
                <div className="small text-secondary mb-1">已选字段（{selected.length} 个，顺序即保存顺序）</div>
                <div className="erp-field-picker-list erp-field-chooser-list" aria-label="已选字段列表">
                  {selected.map((item, index) => {
                    const { desc } = describe(item.key)
                    return (
                      <div key={item.key} className="erp-field-picker-row">
                        <span className="erp-field-picker-order">{index + 1}</span>
                        <span className="erp-menu-table-id">{item.key}</span>
                        {desc ? <span className="erp-field-picker-desc">{desc}</span> : null}
                        <span className="ms-auto d-flex align-items-center gap-1">
                          {mode === 'sort' && (
                            <button type="button" className="erp-field-dir" onClick={() => toggleDir(item.key)}>
                              {item.dir === 'asc' ? '升序' : '降序'}
                            </button>
                          )}
                          <button type="button" className="erp-field-mini" aria-label={`上移 ${item.key}`} disabled={index === 0} onClick={() => move(item.key, -1)}>
                            <IconArrowUp size={14} />
                          </button>
                          <button type="button" className="erp-field-mini" aria-label={`下移 ${item.key}`} disabled={index === selected.length - 1} onClick={() => move(item.key, 1)}>
                            <IconArrowDown size={14} />
                          </button>
                          <button type="button" className="erp-field-mini" aria-label={`移除 ${item.key}`} onClick={() => toggle(item.key)}>
                            <IconTrash size={14} />
                          </button>
                        </span>
                      </div>
                    )
                  })}
                  {selected.length === 0 && <div className="text-secondary small p-2">尚未选择字段。</div>}
                </div>
              </div>
            </div>
    </Modal>
  )
}
