import { IconArrowLeft, IconPlus, IconRefresh, IconSearch, IconTrash, IconUsers } from '@tabler/icons-react'
import { useMutation, useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { usePageBreadcrumb } from '../../components/layout/PageBreadcrumbContext'
import { apiClient } from '../../services/api'
import { ButtonRightsMatrix } from './ButtonRightsMatrix'

import { RightsMatrix } from './RightsMatrix'
import type { GroupMemberSummary, UserGroupSummary } from './types'
import { describeApiError } from '../../lib/errors'

interface UserOption {
  userId: string
  employeeName: string
}

function useGroupId() {
  const { groupId } = useParams<{ groupId: string }>()
  return (groupId ?? '').trim()
}

/** 子页上抛面包屑：系统管理 > 用户组管理 > {组名} {动作}。 */
function useGroupBreadcrumb(id: string, actionLabel: string) {
  const { setBreadcrumb } = usePageBreadcrumb()
  const groups = useQuery({
    queryKey: ['rights-admin', 'groups'],
    queryFn: () => apiClient.get<UserGroupSummary[]>('/admin/groups'),
  })
  const description = groups.data?.find((group) => group.groupId.trim() === id)?.groupDescription
  useEffect(() => {
    setBreadcrumb({
      leads: [{ label: '系统管理' }, { label: '用户组管理', to: '/admin/groups' }],
      title: `${description || id} ${actionLabel}`,
    })
  }, [setBreadcrumb, id, description, actionLabel])
  return description
}

/** 用户组模块权限完整页面（2305 定制页子页）。 */
export function GroupRightsPage() {
  const id = useGroupId()
  const navigate = useNavigate()
  useGroupBreadcrumb(id, '组权限')
  return (
    <RightsMatrix
      open
      variant="page"
      mode="group"
      targetId={id}
      title={`用户组模块权限：${id}`}
      onClose={() => navigate('/admin/groups')}
    />
  )
}

/** 用户组自定义按钮权限完整页面（2305 定制页子页）：按钮是默认拒绝的名单，需显式授权。 */
export function GroupButtonRightsPage() {
  const id = useGroupId()
  const navigate = useNavigate()
  useGroupBreadcrumb(id, '按钮权限')
  return (
    <ButtonRightsMatrix
      open
      variant="page"
      mode="group"
      targetId={id}
      title={`用户组按钮权限：${id}`}
      onClose={() => navigate('/admin/groups')}
    />
  )
}

interface DraftMember {
  userId: string
  employeeName: string
}

/**
 * 用户组成员完整页面（2305 定制页子页）：
 * 成员列表用统一电子表格（ErpTable），添加成员走选择器式弹窗（服务端搜索，适配大量用户），
 * 移除选中成员，保存时按组全量替换（复用 PUT /admin/groups/{id}/members）。
 */
export function GroupMembersPage() {
  const id = useGroupId()
  const navigate = useNavigate()
  useGroupBreadcrumb(id, '成员')
  const [draftMembers, setDraftMembers] = useState<DraftMember[]>([])
  const [removeSelection, setRemoveSelection] = useState<RowSelectionState>({})
  const [addOpen, setAddOpen] = useState(false)

  const members = useQuery({
    queryKey: ['rights-admin', 'members', id],
    queryFn: () => apiClient.get<GroupMemberSummary[]>(`/admin/groups/${encodeURIComponent(id)}/members`),
    enabled: id !== '',
  })

  useEffect(() => {
    if (!members.data) return
    setDraftMembers(members.data.map((member) => ({
      userId: member.userId.trim(),
      employeeName: member.employeeName,
    })))
    setRemoveSelection({})
  }, [members.data])

  const dirty = useMemo(() => {
    const originals = new Set((members.data ?? []).map((member) => member.userId.trim()))
    const draft = new Set(draftMembers.map((member) => member.userId))
    return originals.size !== draft.size || [...originals].some((id) => !draft.has(id))
  }, [members.data, draftMembers])

  const saveMembers = useMutation({
    mutationFn: async (ids: string[]) => {
      await apiClient.put(`/admin/groups/${encodeURIComponent(id)}/members`, { ids })
    },
    onSuccess: () => {
      void members.refetch()
    },
  })

  const addMembers = (users: UserOption[]) => {
    setDraftMembers((current) => {
      const seen = new Set(current.map((member) => member.userId))
      const next = [...current]
      users.forEach((user) => {
        const key = user.userId.trim()
        if (!seen.has(key)) {
          seen.add(key)
          next.push({ userId: key, employeeName: user.employeeName })
        }
      })
      return next
    })
    setAddOpen(false)
  }

  const removeSelected = () => {
    const ids = Object.keys(removeSelection).filter((key) => removeSelection[key])
    if (ids.length === 0) return
    if (!window.confirm(`确定从该组移除选中的 ${ids.length} 名成员吗？`)) return
    setDraftMembers((current) => current.filter((member) => !ids.includes(member.userId)))
    setRemoveSelection({})
  }

  const columns = useMemo<ColumnDef<DraftMember, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    { accessorKey: 'userId', header: '用户ID', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'employeeName', header: '姓名', cell: (info) => <span>{String(info.getValue() || '—')}</span> },
  ], [])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="组成员管理"
        actions={<div className="d-flex gap-2 align-items-center">
          <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={() => navigate('/admin/groups')}>返回用户组</Button>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void members.refetch()}>刷新</Button>
          <Button size="sm" variant="primary" icon={<IconPlus size={16} />} onClick={() => setAddOpen(true)}>添加成员</Button>
          <Button size="sm" variant="danger" icon={<IconTrash size={16} />} onClick={removeSelected} disabled={Object.keys(removeSelection).length === 0}>移除所选</Button>
          <Button size="sm" variant="primary" icon={<IconUsers size={16} />} onClick={() => saveMembers.mutate(draftMembers.map((member) => member.userId))} loading={saveMembers.isPending} disabled={!dirty}>保存</Button>
        </div>}
        header={(
          <div className="erp-list-header text-secondary small px-3 pt-2">
            当前成员 {draftMembers.length} 人{dirty ? `（含 ${draftMembers.length} 人未保存变更，点「保存」按组全量替换生效）` : ''}；添加成员通过选择器按用户搜索。
          </div>
        )}
      >
        {members.isPending ? <LoadingState label="正在加载组成员…" /> : members.isError ? (
          <ErrorState message={describeApiError(members.error, '加载失败。')} onRetry={() => void members.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={draftMembers}
            getRowId={(row) => row.userId}
            resizable
            storageKey={`user-group-members-${id}`}
            clientSideSorting
            rowClickSingleSelect
            rowSelection={removeSelection}
            onRowSelectionChange={setRemoveSelection}
            empty={<EmptyState title="该组暂无成员" description="点击「添加成员」通过选择器加入用户。" />}
          />
        )}
      </ErpListCard>
      {saveMembers.isError && (
        <div className="alert alert-danger mb-0" role="alert">
          {describeApiError(saveMembers.error, '保存失败，请稍后重试。')}
        </div>
      )}
      {addOpen && (
        <MemberAddModal
          groupId={id}
          currentIds={new Set(draftMembers.map((member) => member.userId))}
          onClose={() => setAddOpen(false)}
          onAdded={addMembers}
        />
      )}
    </div>
  )
}

interface MemberAddModalProps {
  groupId: string
  currentIds: Set<string>
  onClose: () => void
  onAdded: (users: UserOption[]) => void
}

/** 选择器式添加成员：服务端关键字分页搜索用户，勾选后一次加入（适配几百用户规模）。 */
function MemberAddModal({ groupId, currentIds, onClose, onAdded }: MemberAddModalProps) {
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [items, setItems] = useState<UserOption[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [selection, setSelection] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)

  const runSearch = async (nextKeyword: string, nextPage: number, append = false) => {
    setLoading(true)
    setError(null)
    try {
      const data = await apiClient.get<{ items: UserOption[]; total: number }>('/admin/users', {
        query: { keyword: nextKeyword, page: nextPage, pageSize: 50 },
      })
      const items = (data.items ?? []).filter((user) => !currentIds.has(user.userId.trim()))
      setTotal(data.total)
      setItems((current) => (append ? [...current, ...items] : items))
      if (!append) setSelection(new Set())
      setPage(nextPage)
    } catch (reason) {
      setError(describeApiError(reason, '用户搜索失败。'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void runSearch('', 1, false)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const toggle = (userId: string) => {
    setSelection((current) => {
      const next = new Set(current)
      if (next.has(userId)) next.delete(userId)
      else next.add(userId)
      return next
    })
  }

  const confirm = () => {
    const users = items.filter((user) => selection.has(user.userId.trim()))
    if (users.length > 0) onAdded(users)
  }

  return (
    <Modal
      title={`添加成员到 ${groupId}`}
      onClose={onClose}
      size="lg"
      footer={<>
        <Button onClick={onClose}>取消</Button>
        <Button variant="primary" onClick={confirm} disabled={selection.size === 0 || loading}>确认添加</Button>
      </>}
    >
            <div className="input-group input-group-sm mb-2">
              <span className="input-group-text"><IconSearch size={14} /></span>
              <input
                className="form-control form-control-sm"
                placeholder="按用户ID / 工号 / 姓名搜索"
                value={keyword}
                aria-label="搜索用户"
                onChange={(event) => setKeyword(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') { event.preventDefault(); void runSearch(keyword, 1, false) }
                }}
              />
              <Button size="sm" variant="primary" onClick={() => void runSearch(keyword, 1, false)} loading={loading}>查询</Button>
            </div>
            {error && <div className="alert alert-danger py-2 mb-2" role="alert">{error}</div>}
            {loading ? <div className="text-secondary small py-3 text-center">正在搜索用户…</div> : (
              <div className="border rounded p-2 rights-picker-list">
                {items.length === 0
                  ? <div className="text-secondary small p-2">没有可添加的用户（已自动排除当前成员）。</div>
                  : items.map((user) => (
                    <label key={user.userId.trim()} className="d-flex align-items-center gap-2 form-check-label small py-1">
                      <input
                        type="checkbox"
                        className="form-check-input m-0"
                        checked={selection.has(user.userId.trim())}
                        onChange={() => toggle(user.userId.trim())}
                      />
                      <span className="font-monospace">{user.userId.trim()}</span>
                      <span className="text-secondary">{user.employeeName}</span>
                    </label>
                  ))}
              </div>
            )}
            <div className="small text-secondary mt-1">
              共 {total} 位用户可搜索，已选 {selection.size} 项（已加载 {items.length} 条）。
            </div>
            {!loading && items.length < total && (
              <div className="mt-2">
                <Button size="sm" variant="ghost" onClick={() => void runSearch(keyword, page + 1, true)}>加载更多</Button>
              </div>
            )}
    </Modal>
  )
}
