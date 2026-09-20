import { IconRefresh, IconDeviceFloppy } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useEffect, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
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
 * 写入只走官方策略端点（`PUT /admin/depot-stock-policy/{depotId}`），组合规则、
 * 破坏性下调的二次确认与归并/归位、变更审计都在服务端；本页只负责把服务端的档位目录
 * 渲染出来（未实现的档位灰显）并把确认信号回传，不自行判断合法性。
 */
export function DepotStockPolicyPage() {
  const queryClient = useQueryClient()
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [draft, setDraft] = useState<Record<string, string> | null>(null)
  const [relocateTo, setRelocateTo] = useState('')
  const [message, setMessage] = useState<{ kind: 'error' | 'warning' | 'ok'; text: string } | null>(null)
  const [confirmPanel, setConfirmPanel] = useState<{ errors: string[]; warnings: string[] } | null>(null)

  const policies = useQuery({
    queryKey: ['depot-stock-policy'],
    queryFn: () => apiClient.get<Policy[]>('/admin/depot-stock-policy'),
  })
  const tiers = useQuery({
    queryKey: ['depot-stock-policy-tiers'],
    queryFn: () => apiClient.get<Tier[]>('/admin/depot-stock-policy/tiers'),
  })

  const selectedId = Object.keys(rowSelection).find((key) => rowSelection[key])
  const selected = useMemo(
    () => (policies.data ?? []).find((item) => item.depotId === selectedId) ?? null,
    [policies.data, selectedId],
  )

  useEffect(() => {
    setDraft(selected ? toDraft(selected) : null)
    setConfirmPanel(null)
    setRelocateTo('')
    setMessage(null)
  }, [selected])

  const save = useMutation({
    mutationFn: (body: Record<string, unknown>) =>
      apiClient.put<SaveResult>(`/admin/depot-stock-policy/${encodeURIComponent(selected?.depotId ?? '')}`, body),
    onSuccess: (result) => {
      setConfirmPanel(null)
      setMessage(result.warnings.length > 0
        ? { kind: 'warning', text: `已保存。${result.warnings.join(' ')}` }
        : { kind: 'ok', text: '已保存。' })
      void queryClient.invalidateQueries({ queryKey: ['depot-stock-policy'] })
    },
    onError: (error) => {
      const result = readSaveResult(error)
      if (result?.requiresConfirmation) {
        setConfirmPanel({ errors: result.errors, warnings: result.warnings })
        setMessage(null)
        return
      }
      setConfirmPanel(null)
      setMessage({
        kind: 'error',
        text: result ? result.errors.join(' ') : describeApiError(error, '保存失败，请稍后重试。'),
      })
    },
  })

  const submit = (confirmDowngrade: boolean) => {
    if (!draft || !selected) return
    save.mutate({
      locationMode: Number(draft.locationMode),
      storageMode: draft.storageMode,
      batchMode: Number(draft.batchMode),
      capacityMode: Number(draft.capacityMode),
      mixProduct: draft.mixProduct === '1',
      mixBatch: draft.mixBatch === '1',
      monthCloseByBatch: draft.monthCloseByBatch === '1',
      monthCloseByLocation: draft.monthCloseByLocation === '1',
      confirmDowngrade,
      relocateTo: relocateTo.trim() === '' ? null : relocateTo.trim(),
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

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="库存策略"
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => { void policies.refetch(); void tiers.refetch() }}>刷新</Button>}
      >
        {policies.isPending || tiers.isPending ? <LoadingState label="正在加载库存策略…" /> : policies.isError || tiers.isError ? (
          <ErrorState message={errorMessage} onRetry={() => { void policies.refetch(); void tiers.refetch() }} />
        ) : (
          <div className="d-flex gap-3 align-items-start p-2">
            <div className="flex-grow-1">
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
                onRowSelectionChange={setRowSelection}
              />
            </div>
            <div className="erp-side-panel" style={{ minWidth: 320 }}>
              {!selected || !draft ? (
                <div className="text-secondary small p-3">选择左侧一行库别策略进行编辑。</div>
              ) : (
                <div className="p-3">
                  <div className="fw-semibold mb-2">
                    编辑：{selected.depotId === '*' ? '部署级默认' : selected.depotId}
                  </div>
                  {(tiers.data ?? []).map((tier) => (
                    <div className="mb-2" key={tier.key}>
                      <label className="form-label small mb-1" title={tier.description}>{tier.label}</label>
                      <select
                        className="form-select form-select-sm"
                        value={draft[tier.key] ?? ''}
                        onChange={(event) => setDraft({ ...draft, [tier.key]: event.target.value })}
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
                      value={relocateTo}
                      placeholder="留空则不搬动存量"
                      onChange={(event) => setRelocateTo(event.target.value)}
                    />
                    <div className="form-text">位置档位升高且确有"未指定位置"存量时填写。</div>
                  </div>

                  {message ? (
                    <div className={`alert py-1 px-2 small ${message.kind === 'error' ? 'alert-danger' : message.kind === 'warning' ? 'alert-warning' : 'alert-success'}`}>
                      {message.text}
                    </div>
                  ) : null}

                  {confirmPanel ? (
                    <div className="alert alert-warning py-2 px-2 small">
                      <div className="fw-semibold mb-1">这是一次破坏性下调，需要确认</div>
                      <div>{confirmPanel.errors.join(' ')}</div>
                      <div className="d-flex gap-2 mt-2">
                        <Button size="sm" variant="danger" onClick={() => submit(true)}>确认下调</Button>
                        <Button size="sm" variant="ghost" onClick={() => setConfirmPanel(null)}>取消</Button>
                      </div>
                    </div>
                  ) : (
                    <Button
                      size="sm"
                      icon={<IconDeviceFloppy size={16} />}
                      disabled={save.isPending}
                      onClick={() => submit(false)}
                    >
                      保存
                    </Button>
                  )}
                </div>
              )}
            </div>
          </div>
        )}
      </ErpListCard>
    </div>
  )
}
