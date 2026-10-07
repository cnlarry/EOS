import { IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useCallback, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { deleteKbDocument, listKbCollections, listKbDocuments } from './api'
import type { KbDocumentInfo } from './api'

/** 可见性三档的中文（与 KbVisibility 一致）。 */
const VISIBILITY_LABELS: Record<string, string> = {
  ALL: '所有人',
  CONSULTANT: '实施顾问',
  OPS: '运维',
}

/**
 * 工作助手管理 → **知识库管理**（菜单组 31 / 模块 3103，路由 `/admin/assistant/kb`）。
 *
 * <para>
 * 管理面**只做只读清单与删除**：入库要过敏感信息扫描与业务引用权限复核（见 `KbController`），
 * 那套门锚在 2302，在这里另开一个入库入口会绕过它，所以不做。
 * </para>
 *
 * <para>
 * **界面上必须写明"检索待嵌入接线"**：当前 `IEmbeddingModel` 仍是 fail-closed 占位
 * （`KB_EMBEDDING_NOT_CONFIGURED`），`KB_CHUNK` 是空的。不写的话，管理员会以为"库里没东西"
 * 是自己配错了——那才是真正的误导。
 * </para>
 */
export function KbAdminPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [collectionId, setCollectionId] = useState('')
  const [keyword, setKeyword] = useState('')
  const [includeDeleted, setIncludeDeleted] = useState(false)

  const collections = useQuery({
    queryKey: ['assistant-admin-kb-collections'],
    queryFn: listKbCollections,
    staleTime: 60_000,
  })

  // 默认选第一个集合（列表通常只有一两个）；用户选过就以他的为准
  const activeCollection = collectionId || collections.data?.[0]?.collectionId || ''

  const documents = useQuery({
    queryKey: ['assistant-admin-kb-documents', activeCollection, includeDeleted],
    queryFn: () => listKbDocuments(activeCollection, includeDeleted),
    enabled: activeCollection.length > 0,
  })

  const items = useMemo(() => {
    const all = documents.data ?? []
    const trimmed = keyword.trim().toLowerCase()
    return trimmed ? all.filter(item => item.title.toLowerCase().includes(trimmed)) : all
  }, [documents.data, keyword])

  const errorMessage = describeApiError(documents.error, '加载知识库文档失败，请稍后重试。')

  const remove = useMutation({
    mutationFn: (docId: string) => deleteKbDocument(docId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['assistant-admin-kb-documents'] })
      toast.notify({ message: '已删除。文档转为墓碑，它的向量已同步移除。', variant: 'success' })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '删除失败。'), variant: 'danger' }),
  })

  const confirmDelete = useCallback((doc: KbDocumentInfo) => {
    if (window.confirm(
      `确定删除知识库文档「${doc.title}」吗？\n\n`
      + '文档会转为墓碑（行保留、状态改为 deleted），它的向量会被同步移除，因此不再被检索到。')) {
      remove.mutate(doc.docId)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<KbDocumentInfo, unknown>[]>(() => [
    {
      accessorKey: 'title',
      header: '标题',
      cell: (info) => (
        <span className="d-inline-flex align-items-center gap-2">
          <span className="fw-semibold">{String(info.getValue())}</span>
          {info.row.original.status === 'deleted' && <span className="badge bg-secondary-lt">墓碑</span>}
        </span>
      ),
    },
    {
      accessorKey: 'visibility',
      header: '可见性',
      meta: { className: 'text-nowrap', minWidth: 90 },
      cell: (info) => {
        const value = String(info.getValue())
        return <span className="text-secondary">{VISIBILITY_LABELS[value] ?? value}</span>
      },
    },
    {
      accessorKey: 'version',
      header: '版本',
      meta: { className: 'text-end text-nowrap', minWidth: 70 },
      cell: (info) => <span className="text-secondary">v{Number(info.getValue() ?? 0)}</span>,
    },
    {
      accessorKey: 'sourceUri',
      header: '来源',
      meta: { className: 'text-nowrap' },
      cell: (info) => <span className="text-secondary small font-monospace">{String(info.getValue() ?? '—')}</span>,
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 110, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除（转墓碑并移除向量）"
          loading={remove.isPending && remove.variables === row.original.docId}
          disabled={remove.isPending || row.original.status === 'deleted'}
          onClick={() => confirmDelete(row.original)}>删除</Button>
      ),
    },
  ], [confirmDelete, remove])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="知识库管理查询与操作"
        search={<ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300}
          placeholder="搜索文档标题" ariaLabel="搜索知识库文档" />}
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 190 }} value={activeCollection}
            aria-label="选择集合"
            onChange={(event) => setCollectionId(event.target.value)}>
            {(collections.data ?? []).length === 0 && <option value="">（暂无集合）</option>}
            {(collections.data ?? []).map(item => (
              <option key={item.collectionId} value={item.collectionId}>
                {item.title}（{item.embeddingModel}）
              </option>
            ))}
          </select>
          <Button size="sm" variant={includeDeleted ? 'primary' : 'secondary'}
            title="连软删墓碑一起显示" onClick={() => setIncludeDeleted(value => !value)}>
            {includeDeleted ? '隐藏墓碑' : '显示墓碑'}
          </Button>
          <Button size="sm" icon={<IconRefresh size={16} />}
            onClick={() => { void collections.refetch(); void documents.refetch() }}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">知识库管理</h2>
          <span className="text-secondary small">共 {items.length} 篇文档</span>
          <span className="ms-auto text-secondary small">
            管理面只做清单与删除；入库仍走运维通道（敏感扫描 + 引用复核）
          </span>
        </div>}
      >
        {/* 这句话必须显眼：不写的话，管理员会以为"库里没东西"是自己配错了 */}
        <div className="alert alert-warning mb-0 rounded-0 py-2" role="status">
          <div className="fw-semibold">检索与入库待嵌入接线</div>
          <div className="small">
            当前嵌入模型仍是 fail-closed 占位（调用会返回 <code>KB_EMBEDDING_NOT_CONFIGURED</code>），
            <code>KB_CHUNK</code> 为空。因此这里的"文档为空"是**预期现象**，不是配置错误——
            接线后跑一次同步脚本即可入库。
          </div>
        </div>
        {!activeCollection ? (
          <EmptyState title="没有可选的集合" description="知识库还没有建任何集合。" />
        ) : documents.isPending ? <LoadingState label="正在加载知识库文档…" /> : documents.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void documents.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => row.docId}
            resizable
            storageKey="assistant-admin-kb-documents"
            empty={<EmptyState title="这个集合里还没有文档"
              description={includeDeleted ? '连墓碑也没有。' : '试试打开「显示墓碑」，或等嵌入接线后同步入库。'} />}
          />
        )}
      </ErpListCard>
    </div>
  )
}
