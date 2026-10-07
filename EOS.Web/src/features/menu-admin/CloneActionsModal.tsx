import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import type { RowSelectionState } from '../../lib/tanstackTable'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { MANUAL_EVENT } from './documentActionConfig'
import type { BusinessAction } from './BusinessActionsPanel'

/**
 * 「从相似模块克隆动作」：挑一个已有模块，勾选它的业务动作追加到当前模块。
 * 只读来源模块的配置、写回当前模块草稿，顺序号按当前模块重新顺延；
 * 克隆结果仍要过保存期校验（表/列/定位键登记），克隆不等于免检。
 */

interface ClonedAction extends BusinessAction {
  moduleId?: number
}

interface SourceConfig {
  moduleId: number
  actions: ClonedAction[]
}

export function CloneActionsModal({
  open,
  currentModuleId,
  onClose,
  onAppend,
}: {
  open: boolean
  currentModuleId: number
  onClose: () => void
  onAppend: (actions: ClonedAction[]) => void
}) {
  const [sourceModule, setSourceModule] = useState<{ id: number; desc: string } | null>(null)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [selection, setSelection] = useState<RowSelectionState>({})

  const configQuery = useQuery({
    queryKey: ['module-business-config-clone-source', sourceModule?.id ?? 0],
    queryFn: () => apiClient.get<SourceConfig>(`/admin/module-business-config/${sourceModule!.id}`),
    enabled: open && sourceModule != null && sourceModule.id > 0,
  })

  // 自定义按钮（EVENT_CODE='MANUAL'）不参与克隆：按钮授权是 fail-closed 名单，
  // 不随配置跨模块带走，克隆过去只会得到一批没人能点的按钮，且失败得很安静。
  const rows = useMemo(() => {
    const list = [...(configQuery.data?.actions ?? [])].filter((action) => action.eventCode !== MANUAL_EVENT)
    return list.sort((a, b) => a.eventCode.localeCompare(b.eventCode) || a.seq - b.seq)
  }, [configQuery.data])
  /** 源模块里被跳过的自定义按钮数：必须显式告知（静默丢按钮才是真坑）。 */
  const skippedManualCount = useMemo(
    () => (configQuery.data?.actions ?? []).filter((action) => action.eventCode === MANUAL_EVENT).length,
    [configQuery.data],
  )

  const columns = useMemo<ColumnDef<ClonedAction, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', resizable: false, frozenLeft: true, truncate: false },
      header: () => null,
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择该动作"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    { accessorKey: 'eventCode', header: '事件', meta: { minWidth: 120 } },
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    { accessorKey: 'effectKey', header: '效果', meta: { minWidth: 180 } },
    {
      accessorKey: 'effectName',
      header: '名称',
      cell: (info) => String(info.getValue() ?? '') || '—',
    },
    {
      id: 'steps',
      header: '步骤数',
      meta: { minWidth: 80 },
      cell: ({ row }) => `${(row.original.ops ?? []).length}`,
    },
    {
      id: 'targets',
      header: '涉及目标表',
      meta: { minWidth: 200 },
      cell: ({ row }) => {
        const tables = [...new Set((row.original.ops ?? []).map((op) => op.targetTable).filter(Boolean))]
        return tables.length > 0 ? tables.join('、') : '—'
      },
    },
  ], [])

  const selectedRows = rows.filter((row) => selection[`${row.eventCode}|${row.seq}`])
  const reset = () => {
    setSourceModule(null)
    setSelection({})
  }
  const close = () => {
    reset()
    onClose()
  }

  return (
    <Modal
      title="从其它模块复制业务动作"
      onClose={close}
      size="lg"
      scrollable
      footer={
        <div className="d-flex gap-2 justify-content-end align-items-center">
          <span className="text-secondary small me-auto">已选 {selectedRows.length} 个动作</span>
          <Button variant="secondary" onClick={close}>取消</Button>
          <Button
            variant="primary"
            disabled={selectedRows.length === 0}
            onClick={() => {
              onAppend(selectedRows)
              close()
            }}
          >
            追加到本模块
          </Button>
        </div>
      }
    >
      <div className="d-flex align-items-center gap-2 mb-2 flex-wrap">
        <strong className="small">来源模块</strong>
        <span className="font-monospace">{sourceModule ? `${sourceModule.desc}(${sourceModule.id})` : '未选择'}</span>
        <Button size="sm" onClick={() => setChooserOpen(true)}>选择模块</Button>
        {sourceModule?.id === currentModuleId ? (
          <span className="text-secondary small">来源就是当前模块（会生成重复动作，保存校验可能拒绝）。</span>
        ) : null}
      </div>

      {sourceModule == null ? (
        <div className="text-secondary small">先选择一个来源模块，再勾选要复制的业务动作。</div>
      ) : configQuery.isLoading ? (
        <LoadingState />
      ) : configQuery.isError ? (
        <ErrorState
          message={`来源配置加载失败：${describeApiError(configQuery.error, '无法读取该模块的业务动作。')}`}
          onRetry={() => void configQuery.refetch()}
        />
      ) : (
        <>
          {skippedManualCount > 0 ? (
            <div className="alert alert-warning py-2 px-3 small" role="status">
              源模块有 <strong>{skippedManualCount}</strong> 个自定义按钮**不会**随之复制：按钮级授权是
              fail-closed 名单，克隆只带效果链与校验规则；要在本模块用这些按钮，需单独新增并在
              「自定义按钮」页签里给具体用户/组发放授权。
            </div>
          ) : null}
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={(row) => `${row.eventCode}|${row.seq}`}
            rowSelection={selection}
            onRowSelectionChange={setSelection}
            clientSideSorting
            copyable={false}
            empty={<div className="p-3 text-secondary">该模块没有可复制的业务动作（自定义按钮不参与克隆）。</div>}
          />
        </>
      )}

      <UnifiedChooser
        open={chooserOpen}
        title="选择来源模块"
        source={{ kind: 'sourceKey', key: 'menu-admin.modules' }}
        mode="single"
        getRowId={(row) => String(row.M_IDX)}
        onPick={(picked) => {
          const row = picked[0]
          if (row) {
            setSourceModule({ id: Number(row.M_IDX), desc: String(row.M_DESC ?? '') })
            setSelection({})
          }
          setChooserOpen(false)
        }}
        onClose={() => setChooserOpen(false)}
        searchPlaceholder="搜索模块号/模块名…"
        emptyText="没有匹配的模块。"
      />
    </Modal>
  )
}
