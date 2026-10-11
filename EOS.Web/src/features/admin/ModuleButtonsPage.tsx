import { IconAlertTriangle, IconDeviceFloppy, IconPencil, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { ConfigPublishAlerts, type ConfigSaveOutcome } from './ConfigPublishAlerts'

/**
 * 自定义按钮（模块 230103，定制承载页 `/admin/module-buttons`，ADR-033 批 2）。
 *
 * 编辑对象是 `MODULE_BUSINESS_ACTION` 里 `EVENT_CODE='MANUAL'` 的行——它与效果链动作**同表**，
 * 所以这个页面保存时依赖服务端的**显式作用域**（`PUT /admin/module-buttons/{id}` 固定走 `manual`）：
 * 只替换按钮行，该模块的行为动作与校验规则一行不动。反之，230101 的行为动作面按 `actions` 保存，
 * 也不会碰按钮。两个配置面互不覆盖，真库防线见 `ModuleBusinessConfigScopeLiveTests`。
 *
 * 保存是"整面替换"（服务端语义）：本页把内存里的按钮全集一次性提交，故界面要如实告知未保存改动。
 */

/** 服务端下发的可配置按钮处理器（`GET /admin/module-buttons/meta`）：键必须从这里挑，注册表是封闭目录。 */
type DocumentActionOption = { key: string; label: string; placement: string }

/** 按钮行（字段与模块管理侧同一个 DTO：编辑时整行回传，未知字段原样带上，不做"只回传编辑过的字段"）。 */
type ButtonRow = {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode?: string
  condition?: string | null
  params?: string | null
  reverse?: string | null
  remark?: string | null
  sourceRef?: string | null
  ops?: unknown
  label?: string | null
}

type ButtonsView = { moduleId: number; buttons: ButtonRow[] }

/** 授权镜子：每个按钮当前有几个用户 / 几个组可用（0/0 = 配了没人能用，界面必须说出来）。 */
type AuthorizationEntry = { seq: number; effectKey: string; label: string; users: number; groups: number }

type AuthorizationView = { buttons: AuthorizationEntry[] }

const MANUAL_EVENT = 'MANUAL'

export function ModuleButtonsPage() {
  const queryClient = useQueryClient()
  const [moduleId, setModuleId] = useState<number | null>(null)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [draft, setDraft] = useState<ButtonRow[] | null>(null)
  const [editor, setEditor] = useState<{ mode: 'create' | 'edit'; index: number; row: ButtonRow } | null>(null)
  const [deleteIndex, setDeleteIndex] = useState<number | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})

  const meta = useQuery({
    queryKey: ['module-buttons', 'meta'],
    queryFn: () => apiClient.get<{ buttons: DocumentActionOption[] }>('/admin/module-buttons/meta'),
  })

  const view = useQuery({
    queryKey: ['module-buttons', moduleId],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ButtonsView>(`/admin/module-buttons/${moduleId}`),
  })

  const authorization = useQuery({
    queryKey: ['module-buttons', moduleId, 'authorization'],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<AuthorizationView>(`/admin/module-buttons/${moduleId}/action-authorization`),
  })

  const rows = draft ?? view.data?.buttons ?? []
  const dirty = draft != null
  const options = meta.data?.buttons ?? []
  const optionsByKey = useMemo(() => {
    const map = new Map<string, DocumentActionOption>()
    for (const option of options) map.set(option.key, option)
    return map
  }, [options])

  const authByKey = useMemo(() => {
    const map = new Map<string, AuthorizationEntry>()
    for (const entry of authorization.data?.buttons ?? []) map.set(entry.effectKey, entry)
    return map
  }, [authorization.data])

  const refresh = async () => {
    setDraft(null)
    setRowSelection({})
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['module-buttons', moduleId] }),
      queryClient.invalidateQueries({ queryKey: ['module-buttons', moduleId, 'authorization'] }),
    ])
  }

  const save = useMutation({
    mutationFn: async (list: ButtonRow[]) => {
      // 整面替换：请求体只带按钮行、不带校验规则——越域的行服务端会拒（显式优于推导）。
      // 响应带**发布结果**：服务端保存成功后自动发布一次（ADR-033 §7.3 方案 B）。
      return await apiClient.put<ConfigSaveOutcome>(`/admin/module-buttons/${moduleId}`, {
        actions: list.map((row) => ({ ...row, seq: row.seq, eventCode: MANUAL_EVENT })),
        validationRules: [],
      })
    },
    onSuccess: async (result) => {
      setSaveError(null)
      setOutcome(result ?? null)
      setDraft(null)
      await queryClient.invalidateQueries({ queryKey: ['module-buttons', moduleId] })
    },
    onError: (error) => {
      setOutcome(null)
      setSaveError(describeApiError(error, '保存自定义按钮失败。'))
    },
  })

  const nextSeq = () => rows.reduce((max, row) => Math.max(max, row.seq), 0) + 1

  const openCreate = () => {
    const first = options[0]
    setEditor({
      mode: 'create',
      index: -1,
      row: {
        seq: nextSeq(),
        eventCode: MANUAL_EVENT,
        effectKey: first?.key ?? '',
        enabled: true,
        failMode: 'BLOCK',
        label: first?.label ?? '',
      },
    })
  }

  const applyEditor = (row: ButtonRow) => {
    if (!editor) return
    const list = [...rows]
    if (editor.mode === 'create') list.push(row)
    else list[editor.index] = row
    setDraft(list)
    setEditor(null)
  }

  const confirmDelete = () => {
    if (deleteIndex == null) return
    setDraft(rows.filter((_, index) => index !== deleteIndex))
    setDeleteIndex(null)
    setRowSelection({})
  }

  const columns = useMemo<ColumnDef<ButtonRow>[]>(() => [
    { accessorKey: 'seq', header: '顺序', size: 70 },
    {
      accessorKey: 'label',
      header: '按钮文案',
      cell: ({ row }) => row.original.label?.trim()
        || optionsByKey.get(row.original.effectKey)?.label
        || <span className="text-secondary">（用处理器默认文案）</span>,
    },
    { accessorKey: 'effectKey', header: '处理器键', cell: ({ row }) => <span className="font-monospace">{row.original.effectKey}</span> },
    {
      accessorKey: 'enabled',
      header: '启用',
      size: 70,
      cell: ({ row }) => (row.original.enabled ? '是' : '否'),
    },
    {
      id: 'authorization',
      header: '授权',
      size: 130,
      // 0/0 是 fail-closed 的正常态，但"配了没人能用"必须显眼——它是静默失效，不是错误。
      cell: ({ row }) => {
        const entry = authByKey.get(row.original.effectKey)
        if (!entry) return <span className="text-secondary">—</span>
        if (entry.users === 0 && entry.groups === 0) {
          return (
            <span className="text-danger d-inline-flex align-items-center gap-1">
              <IconAlertTriangle size={14} /> 无人可用
            </span>
          )
        }
        return `${entry.users} 用户 / ${entry.groups} 组`
      },
    },
    { accessorKey: 'remark', header: '备注', cell: ({ row }) => row.original.remark ?? '' },
  ], [authByKey, optionsByKey])

  const selectedIndex = Object.keys(rowSelection).find((key) => rowSelection[key]) != null
    ? rows.findIndex((_, index) => rowSelection[String(index)] === true)
    : -1

  return (
    <div className="d-flex flex-column gap-3">
      <ErpListCard
        header={(
          <div className="px-3 pt-2 small">
            <div className="fw-semibold">
              自定义按钮{moduleId != null && view.data ? `（${view.data.buttons.length} 个）` : ''}
            </div>
            <div className="text-secondary">
              按钮是用户点击类动作：它不在效果链上，随模块<span className="fw-semibold">发布后生效</span>。
            </div>
          </div>
        )}
        actions={(
          <>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={16} />} onClick={() => setChooserOpen(true)}>
              选择模块
            </Button>
            <Button
              size="sm"
              variant="ghost"
              icon={<IconRefresh size={16} />}
              disabled={moduleId == null}
              onClick={() => void refresh()}
            >
              刷新
            </Button>
            <Button size="sm" variant="ghost" icon={<IconPlus size={16} />} disabled={moduleId == null} onClick={openCreate}>
              新增按钮
            </Button>
            <Button
              size="sm"
              variant="ghost"
              icon={<IconPencil size={16} />}
              disabled={selectedIndex < 0}
              onClick={() => selectedIndex >= 0 && setEditor({ mode: 'edit', index: selectedIndex, row: rows[selectedIndex] })}
            >
              编辑
            </Button>
            <Button
              size="sm"
              variant="ghost"
              icon={<IconTrash size={16} />}
              disabled={selectedIndex < 0}
              onClick={() => selectedIndex >= 0 && setDeleteIndex(selectedIndex)}
            >
              删除
            </Button>
            <Button
              size="sm"
              icon={<IconDeviceFloppy size={16} />}
              disabled={!dirty || save.isPending}
              loading={save.isPending}
              onClick={() => save.mutate(rows)}
            >
              保存
            </Button>
          </>
        )}
      >
        {moduleId == null ? (
          <EmptyState
            title="还没有选择模块"
            description="自定义按钮挂在各模块上：先点右上角「选择模块」，再维护该模块的按钮。"
          />
        ) : view.isPending ? (
          <LoadingState />
        ) : view.isError ? (
          <ErrorState message={describeApiError(view.error, '加载该模块的按钮失败。')} onRetry={() => void view.refetch()} />
        ) : (
          <div className="d-flex flex-column gap-2">
            <div className="text-secondary small">
              正在配置模块 <span className="font-monospace">{moduleId}</span>
              {rows.length > 0 ? ` 的 ${rows.length} 个按钮` : ''}。
              按钮是用户点击类动作：它不在效果链上，随模块**发布后生效**；此处保存只替换按钮行，
              不影响该模块的行为动作与校验规则。
              {dirty ? <span className="text-warning ms-1">有未保存的改动。</span> : null}
            </div>
            {saveError ? <div className="alert alert-danger py-2 mb-0">{saveError}</div> : null}
            <ConfigPublishAlerts outcome={outcome} />
            <ErpTable
              columns={columns}
              data={rows}
              getRowId={(_row, index) => String(index)}
              empty={<EmptyState title="该模块还没有自定义按钮" description="没有按钮行时，单据面上不出现任何自定义按钮。" />}
              resizable
              storageKey="module-buttons"
              rowClickSingleSelect
              rowSelection={rowSelection}
              onRowSelectionChange={(selection) => setRowSelection(selection)}
              onRowDoubleClick={(row) => setEditor({ mode: 'edit', index: rows.indexOf(row), row })}
            />
          </div>
        )}
      </ErpListCard>

      {chooserOpen ? (
        <UnifiedChooser
          open
          title="选择要配置按钮的模块"
          source={{ kind: 'sourceKey', key: 'module-buttons.targets' }}
          mode="single"
          getRowId={(row) => String(row.M_IDX)}
          onPick={(pickedRows) => {
            const picked = pickedRows[0] as { M_IDX?: unknown } | undefined
            if (picked) {
              setModuleId(Number(picked.M_IDX))
              setDraft(null)
              setRowSelection({})
              setSaveError(null)
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
          dialogSize="md"
          searchPlaceholder="按模块号或模块名搜索"
          emptyText="没有可配按钮的模块（只有统一工作台模块才有按钮消费方）。"
        />
      ) : null}

      {editor ? (
        <Modal
          title={editor.mode === 'create' ? '新增按钮' : `编辑按钮（${editor.row.label || editor.row.effectKey}）`}
          size="lg"
          onClose={() => setEditor(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setEditor(null)}>取消</Button>
              <Button
                size="sm"
                icon={<IconDeviceFloppy size={16} />}
                disabled={editor.row.effectKey.trim() === ''}
                onClick={() => applyEditor(editor.row)}
              >
                确定
              </Button>
            </>
          )}
        >
          <div className="row g-2">
            <div className="col-12">
              <label className="form-label small mb-1">处理器</label>
              <select
                className="form-select form-select-sm"
                aria-label="处理器"
                value={editor.row.effectKey}
                onChange={(event) => {
                  const option = optionsByKey.get(event.target.value)
                  setEditor({
                    ...editor,
                    row: {
                      ...editor.row,
                      effectKey: event.target.value,
                      label: editor.row.label?.trim() ? editor.row.label : (option?.label ?? ''),
                    },
                  })
                }}
              >
                {options.length === 0 ? <option value="">（没有已登记的处理器）</option> : null}
                {options.map((option) => (
                  <option key={option.key} value={option.key}>{option.label}（{option.key}）</option>
                ))}
              </select>
              <div className="form-text">
                只能挑已登记的处理器：未登记实现的键保存会被拒（注册表是封闭目录）。
              </div>
            </div>
            <div className="col-6">
              <label className="form-label small mb-1">按钮文案</label>
              <input
                className="form-control form-control-sm"
                aria-label="按钮文案"
                maxLength={50}
                value={editor.row.label ?? ''}
                onChange={(event) => setEditor({ ...editor, row: { ...editor.row, label: event.target.value } })}
              />
              <div className="form-text">留空则用处理器声明的默认文案。</div>
            </div>
            <div className="col-6 d-flex align-items-end">
              <div className="form-check">
                <input
                  className="form-check-input"
                  type="checkbox"
                  id="module-button-enabled"
                  aria-label="启用"
                  checked={editor.row.enabled}
                  onChange={(event) => setEditor({ ...editor, row: { ...editor.row, enabled: event.target.checked } })}
                />
                <label className="form-check-label small" htmlFor="module-button-enabled">启用该按钮</label>
              </div>
            </div>
            <div className="col-12">
              <label className="form-label small mb-1">备注</label>
              <textarea
                className="form-control form-control-sm"
                aria-label="备注"
                rows={2}
                maxLength={200}
                value={editor.row.remark ?? ''}
                onChange={(event) => setEditor({ ...editor, row: { ...editor.row, remark: event.target.value } })}
              />
            </div>
          </div>
        </Modal>
      ) : null}

      {deleteIndex != null ? (
        <Modal
          title="删除按钮"
          onClose={() => setDeleteIndex(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setDeleteIndex(null)}>取消</Button>
              <Button size="sm" variant="danger" onClick={confirmDelete}>删除</Button>
            </>
          )}
        >
          <div className="small">
            将删除按钮「{rows[deleteIndex]?.label?.trim() || rows[deleteIndex]?.effectKey}」。
            改动在点「保存」后落到服务端，并随模块**发布**生效。
          </div>
        </Modal>
      ) : null}
    </div>
  )
}
