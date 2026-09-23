import { IconRefresh, IconDeviceFloppy, IconPlus, IconPencil, IconTrash, IconArrowRight } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useEffect, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { describeApiError } from '../../lib/errors'

/** 档位候选值；implemented=false 表示本版未实现——可见但不可选（与服务端拒存规则同源）。 */
interface TierOption {
  value: string
  label: string
  implemented: boolean
}

/** 一个可配置维度（位置 / 存放 / 批次 / 容量 / 混品号 / 混批次 / 月结两列）。 */
interface Tier {
  key: string
  label: string
  description: string
  options: TierOption[]
}

interface Policy {
  depotId: string
  locationMode: number
  storageMode: string
  batchMode: number
  capacityMode: number
  mixProduct: boolean
  mixBatch: boolean
  monthCloseByBatch: boolean
  monthCloseByLocation: boolean
}

interface SaveResult {
  saved: boolean
  errors: string[]
  warnings: string[]
  requiresConfirmation: boolean
}

interface RelocatePreview {
  saved: boolean
  requiresConfirmation: boolean
  pendingGroups: number
  message: string
}

/** 保存返回 400 时响应体仍是保存结果（不是标准 problem），故按形状宽容读取。 */
function readSaveResult(error: unknown): SaveResult | null {
  if (!(error instanceof ApiError)) return null
  const body = error.body as unknown as Partial<SaveResult>
  if (!Array.isArray(body?.errors)) return null
  return {
    saved: false,
    errors: body.errors ?? [],
    warnings: Array.isArray(body.warnings) ? body.warnings : [],
    requiresConfirmation: body.requiresConfirmation === true,
  }
}

function readMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    const body = error.body as unknown as { message?: unknown }
    if (typeof body?.message === 'string' && body.message.length > 0) return body.message
  }
  return describeApiError(error, fallback)
}

function toDraft(policy: Policy): Record<string, string> {
  return {
    locationMode: String(policy.locationMode),
    storageMode: policy.storageMode,
    batchMode: String(policy.batchMode),
    capacityMode: String(policy.capacityMode),
    mixProduct: policy.mixProduct ? '1' : '0',
    mixBatch: policy.mixBatch ? '1' : '0',
    monthCloseByBatch: policy.monthCloseByBatch ? '1' : '0',
    monthCloseByLocation: policy.monthCloseByLocation ? '1' : '0',
  }
}

/**
 * 库存策略（110310）：决定"库存管到多细"——位置 / 存放 / 批次 / 容量 / 混品号 / 混批次。
 *
 * 写入只走官方策略端点（`PUT /admin/depot-stock-policy/{depotId}` 新增与编辑、
 * `DELETE` 删除、`POST …/relocate` 独立归位），组合规则、破坏性下调的二次确认与
 * 归并/归位、变更审计都在服务端；本页只负责把服务端的档位目录渲染出来
 * （未实现的档位灰显）并把确认信号回传，不自行判断合法性。
 *
 * 编辑走弹窗（新增/编辑共用一具表单）；归位是本页自管的独立动作
 * （先预览"将搬几组"，确认后才真写），不经单据动作框架。
 */
export function DepotStockPolicyPage() {
  const queryClient = useQueryClient()
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [editor, setEditor] = useState<null | {
    mode: 'create' | 'edit'
    depotId: string
    draft: Record<string, string>
    relocateTo: string
    confirmPanel: { errors: string[]; warnings: string[] } | null
    message: { kind: 'error' | 'warning' | 'ok'; text: string } | null
  }>(null)
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null)
  const [relocateTo, setRelocateTo] = useState('')
  const [relocatePreview, setRelocatePreview] = useState<RelocatePreview | null>(null)
  const [relocateMessage, setRelocateMessage] = useState<{ kind: 'error' | 'ok'; text: string } | null>(null)

  const policies = useQuery({
    queryKey: ['depot-stock-policy'],
    queryFn: () => apiClient.get<Policy[]>('/admin/depot-stock-policy'),
  })
  const tiers = useQuery({
    queryKey: ['depot-stock-policy-tiers'],
    queryFn: () => apiClient.get<Tier[]>('/admin/depot-stock-policy/tiers'),
  })

  const selectedId = Object.keys(rowSelection).find((key) => rowSelection[key])
  // 单选语义：首列是单选框，一次只留一行（表格底层是开关语义，不收拢会留下多行）。
  const handleRowSelectionChange = (updater: React.SetStateAction<RowSelectionState>) => {
    setRowSelection((prev) => {
      const next = typeof updater === 'function'
        ? (updater as (old: RowSelectionState) => RowSelectionState)(prev)
        : updater
      const picked = Object.keys(next).filter((key) => next[key])
      if (picked.length <= 1) return next
      const last = picked[picked.length - 1]
      return { [last]: true }
    })
  }
  const selected = useMemo(
    () => (policies.data ?? []).find((item) => item.depotId === selectedId) ?? null,
    [policies.data, selectedId],
  )
  const deployment = useMemo(
    () => (policies.data ?? []).find((item) => item.depotId === '*') ?? null,
    [policies.data],
  )

  useEffect(() => {
    setRelocateTo('')
    setRelocatePreview(null)
    setRelocateMessage(null)
  }, [selectedId])

  const refreshAll = () => {
    void policies.refetch()
    void tiers.refetch()
  }

  const save = useMutation({
    mutationFn: ({ depotId, body }: { depotId: string; body: Record<string, unknown> }) =>
      apiClient.put<SaveResult>(`/admin/depot-stock-policy/${encodeURIComponent(depotId)}`, body),
    onSuccess: (result) => {
      setEditor((prev) => prev && {
        ...prev,
        confirmPanel: null,
        message: result.warnings.length > 0
          ? { kind: 'warning', text: `已保存。${result.warnings.join(' ')}` }
          : { kind: 'ok', text: '已保存。' },
      })
      void queryClient.invalidateQueries({ queryKey: ['depot-stock-policy'] })
    },
    onError: (error) => {
      const result = readSaveResult(error)
      if (result?.requiresConfirmation) {
        setEditor((prev) => prev && {
          ...prev,
          confirmPanel: { errors: result.errors, warnings: result.warnings },
          message: null,
        })
        return
      }
      setEditor((prev) => prev && {
        ...prev,
        confirmPanel: null,
        message: {
          kind: 'error',
          text: result ? result.errors.join(' ') : describeApiError(error, '保存失败，请稍后重试。'),
        },
      })
    },
  })

  const remove = useMutation({
    mutationFn: (depotId: string) => apiClient.delete(`/admin/depot-stock-policy/${encodeURIComponent(depotId)}`),
    onSuccess: () => {
      setDeleteTarget(null)
      setRowSelection({})
      void queryClient.invalidateQueries({ queryKey: ['depot-stock-policy'] })
    },
    onError: (error) => {
      setRelocateMessage({ kind: 'error', text: readMessage(error, '删除失败，请稍后重试。') })
      setDeleteTarget(null)
    },
  })

  const previewRelocate = useMutation({
    mutationFn: ({ depotId, target }: { depotId: string; target: string }) =>
      apiClient.post<RelocatePreview, { relocateTo: string; confirm: boolean }>(
        `/admin/depot-stock-policy/${encodeURIComponent(depotId)}/relocate`,
        { relocateTo: target, confirm: false },
      ),
    onSuccess: (result) => {
      setRelocatePreview(result)
      setRelocateMessage(null)
    },
    onError: (error) => {
      setRelocatePreview(null)
      setRelocateMessage({ kind: 'error', text: readMessage(error, '预览失败，请稍后重试。') })
    },
  })

  const runRelocate = useMutation({
    mutationFn: ({ depotId, target }: { depotId: string; target: string }) =>
      apiClient.post<{ saved: boolean; message: string }, { relocateTo: string; confirm: boolean }>(
        `/admin/depot-stock-policy/${encodeURIComponent(depotId)}/relocate`,
        { relocateTo: target, confirm: true },
      ),
    onSuccess: (result) => {
      setRelocatePreview(null)
      setRelocateTo('')
      setRelocateMessage({ kind: 'ok', text: result.message })
      void queryClient.invalidateQueries({ queryKey: ['depot-stock-policy'] })
    },
    onError: (error) => {
      setRelocateMessage({ kind: 'error', text: readMessage(error, '归位失败，请稍后重试。') })
    },
  })

  const openCreate = () => {
    const base = deployment ? toDraft(deployment) : {
      locationMode: '0', storageMode: 'FIXED', batchMode: '0', capacityMode: '0',
      mixProduct: '1', mixBatch: '1', monthCloseByBatch: '1', monthCloseByLocation: '0',
    }
    setEditor({ mode: 'create', depotId: '', draft: base, relocateTo: '', confirmPanel: null, message: null })
  }

  const openEdit = () => {
    if (!selected) return
    setEditor({
      mode: 'edit', depotId: selected.depotId, draft: toDraft(selected),
      relocateTo: '', confirmPanel: null, message: null,
    })
  }

  const submitEditor = (confirmDowngrade: boolean) => {
    if (!editor || save.isPending) return
    const depotId = editor.mode === 'create' ? editor.depotId.trim() : editor.depotId
    if (depotId.length === 0) {
      setEditor({ ...editor, message: { kind: 'error', text: '请填写库别代号。' } })
      return
    }
    save.mutate({
      depotId,
      body: {
        locationMode: Number(editor.draft.locationMode),
        storageMode: editor.draft.storageMode,
        batchMode: Number(editor.draft.batchMode),
        capacityMode: Number(editor.draft.capacityMode),
        mixProduct: editor.draft.mixProduct === '1',
        mixBatch: editor.draft.mixBatch === '1',
        monthCloseByBatch: editor.draft.monthCloseByBatch === '1',
        monthCloseByLocation: editor.draft.monthCloseByLocation === '1',
        confirmDowngrade,
        relocateTo: editor.relocateTo.trim() === '' ? null : editor.relocateTo.trim(),
      },
    })
  }

  const columns = useMemo<ColumnDef<Policy, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: () => <span className="visually-hidden">选择</span>,
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="radio"
          aria-label={`选择 ${row.original.depotId}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    {
      accessorKey: 'depotId',
      header: '库别',
      cell: (info) => (
        <span className="font-monospace fw-semibold">
          {info.getValue() === '*' ? '*（部署级默认）' : String(info.getValue())}
        </span>
      ),
    },
    { accessorKey: 'locationMode', header: '位置档位' },
    { accessorKey: 'storageMode', header: '存放方式' },
    { accessorKey: 'batchMode', header: '批次档位' },
    { accessorKey: 'capacityMode', header: '容量档位' },
    { accessorKey: 'mixProduct', header: '混品号', cell: (info) => (info.getValue() ? '允许' : '禁止') },
    { accessorKey: 'mixBatch', header: '混批次', cell: (info) => (info.getValue() ? '允许' : '禁止') },
    { accessorKey: 'monthCloseByBatch', header: '月结按批次', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'monthCloseByLocation', header: '月结按库位', cell: (info) => (info.getValue() ? '是' : '否') },
  ], [])

  const errorMessage = describeApiError(policies.error ?? tiers.error, '发生未知错误，请稍后重试。')
  const canModify = selected && selected.depotId !== '*'

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="库存策略"
        actions={(
          <>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={openCreate}>新增</Button>
            <Button size="sm" icon={<IconPencil size={16} />} disabled={!selected} onClick={openEdit}>编辑</Button>
            <Button
              size="sm"
              variant="danger"
              icon={<IconTrash size={16} />}
              disabled={!canModify}
              title={selected?.depotId === '*' ? '部署级默认行不允许删除' : '删除所选库别的策略行'}
              onClick={() => selected && setDeleteTarget(selected.depotId)}
            >
              删除
            </Button>
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={refreshAll}>刷新</Button>
          </>
        )}
      >
        {policies.isPending || tiers.isPending ? <LoadingState label="正在加载库存策略…" /> : policies.isError || tiers.isError ? (
          <ErrorState message={errorMessage} onRetry={refreshAll} />
        ) : (
          <div className="d-flex flex-column gap-2 p-2">
            <ErpTable
              columns={columns}
              data={policies.data ?? []}
              getRowId={(row: Policy) => row.depotId}
              empty={<EmptyState title="没有策略行" description="库别无策略行时按部署级默认行使。" />}
              resizable
              storageKey="depot-stock-policy"
              clientSideSorting
              rowClickSingleSelect
              rowSelection={rowSelection}
              onRowSelectionChange={handleRowSelectionChange}
            />
            {selected && selected.depotId !== '*' ? (
              <div className="card">
                <div className="card-body py-2">
                  <div className="fw-semibold small mb-1">哨兵存量归位：{selected.depotId}</div>
                  <div className="text-secondary small mb-2">
                    把该库别记在『未指定位置』上的存量改记到目标库位（库别总量不变）。先预览，确认后才真写。
                  </div>
                  <div className="d-flex gap-2 align-items-center flex-wrap">
                    <input
                      className="form-control form-control-sm"
                      style={{ maxWidth: 220 }}
                      aria-label="目标库位"
                      value={relocateTo}
                      placeholder="目标库位"
                      onChange={(event) => { setRelocateTo(event.target.value); setRelocatePreview(null) }}
                    />
                    <Button
                      size="sm"
                      icon={<IconArrowRight size={16} />}
                      disabled={relocateTo.trim() === '' || previewRelocate.isPending}
                      loading={previewRelocate.isPending}
                      onClick={() => previewRelocate.mutate({ depotId: selected.depotId, target: relocateTo.trim() })}
                    >
                      预览
                    </Button>
                    {relocatePreview ? (
                      <Button
                        size="sm"
                        variant="danger"
                        disabled={runRelocate.isPending}
                        loading={runRelocate.isPending}
                        onClick={() => runRelocate.mutate({ depotId: selected.depotId, target: relocateTo.trim() })}
                      >
                        确认执行（{relocatePreview.pendingGroups} 组）
                      </Button>
                    ) : null}
                  </div>
                  {relocatePreview ? (
                    <div className="alert alert-warning py-1 px-2 small mt-2 mb-0">{relocatePreview.message}</div>
                  ) : null}
                  {relocateMessage ? (
                    <div className={`alert py-1 px-2 small mt-2 mb-0 ${relocateMessage.kind === 'error' ? 'alert-danger' : 'alert-success'}`}>
                      {relocateMessage.text}
                    </div>
                  ) : null}
                </div>
              </div>
            ) : null}
          </div>
        )}
      </ErpListCard>

      {editor ? (
        <Modal
          title={editor.mode === 'create' ? '新增策略行' : `编辑：${editor.depotId === '*' ? '部署级默认' : editor.depotId}`}
          onClose={() => setEditor(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setEditor(null)}>取消</Button>
              <Button
                size="sm"
                icon={<IconDeviceFloppy size={16} />}
                disabled={save.isPending}
                loading={save.isPending}
                onClick={() => submitEditor(false)}
              >
                保存
              </Button>
            </>
          )}
        >
          {editor.mode === 'create' ? (
            <div className="mb-2">
              <label className="form-label small mb-1">库别代号</label>
              <input
                className="form-control form-control-sm font-monospace"
                aria-label="库别代号"
                value={editor.depotId}
                placeholder="如 CP"
                onChange={(event) => setEditor({ ...editor, depotId: event.target.value })}
              />
            </div>
          ) : null}
          {(tiers.data ?? []).map((tier) => (
            <div className="mb-2" key={tier.key}>
              <label className="form-label small mb-1" title={tier.description}>{tier.label}</label>
              <select
                className="form-select form-select-sm"
                value={editor.draft[tier.key] ?? ''}
                onChange={(event) => setEditor({ ...editor, draft: { ...editor.draft, [tier.key]: event.target.value } })}
              >
                {tier.options.map((option) => (
                  <option key={option.value} value={option.value} disabled={!option.implemented}>
                    {option.implemented ? option.label : `${option.label}（本版未实现）`}
                  </option>
                ))}
              </select>
            </div>
          ))}
          <div className="mb-2">
            <label className="form-label small mb-1">升档归位目标库位（可选）</label>
            <input
              className="form-control form-control-sm"
              value={editor.relocateTo}
              placeholder="留空则不搬动存量"
              onChange={(event) => setEditor({ ...editor, relocateTo: event.target.value })}
            />
            <div className="form-text">位置档位升高且确有“未指定位置”存量时填写。</div>
          </div>

          {editor.message ? (
            <div className={`alert py-1 px-2 small ${editor.message.kind === 'error' ? 'alert-danger' : editor.message.kind === 'warning' ? 'alert-warning' : 'alert-success'}`}>
              {editor.message.text}
            </div>
          ) : null}

          {editor.confirmPanel ? (
            <div className="alert alert-warning py-2 px-2 small">
              <div className="fw-semibold mb-1">这是一次破坏性下调，需要确认</div>
              <div>{editor.confirmPanel.errors.join(' ')}</div>
              <div className="d-flex gap-2 mt-2">
                <Button size="sm" variant="danger" onClick={() => submitEditor(true)}>确认下调</Button>
                <Button size="sm" variant="ghost" onClick={() => setEditor({ ...editor, confirmPanel: null })}>取消</Button>
              </div>
            </div>
          ) : null}
        </Modal>
      ) : null}

      {deleteTarget ? (
        <Modal
          title="删除策略行"
          onClose={() => setDeleteTarget(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setDeleteTarget(null)}>取消</Button>
              <Button
                size="sm"
                variant="danger"
                disabled={remove.isPending}
                loading={remove.isPending}
                onClick={() => remove.mutate(deleteTarget)}
              >
                确认删除
              </Button>
            </>
          )}
        >
          <p className="small mb-0">
            确认删除库别 <span className="font-monospace fw-semibold">{deleteTarget}</span> 的策略行？
            删除后该库别按部署级默认行使，库存存量本身不动。
          </p>
        </Modal>
      ) : null}
    </div>
  )
}
