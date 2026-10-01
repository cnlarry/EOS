import { IconTrash, IconUserPlus } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import type { ColumnDef } from '@tanstack/react-table'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { deleteScopeLayer, listScopes, upsertScope } from './api'
import type { AssistantScopeOverride } from './api'

/**
 * 助手设置 → **按用户覆盖**（ADR-030 §6.2）。
 *
 * <para>
 * 参数有三层取值：**用户 &gt; 模块 &gt; 全局**。这里管的是最贴近当事人的那一层——
 * "给某个人单独调高日限额"。它不是越权：放宽限额是管理决定，而每一次改动都进审计
 * （`ASSISTANT_PARAM_SCOPE` 资源），所以"谁在什么时候把谁的限额调高了"是可回答的。
 * </para>
 *
 * <para>
 * **可选参数由服务端给出**（`scopable`）：哪条参数能被覆盖、允许出现在哪些层、能不能放宽，
 * 都在参数目录里声明，界面只显示——front-end 不自己判断"这条应该能改吧"。
 * 目前只有 `USER_DAILY_CAP_YUAN` 声明了可被用户覆盖；模块层的参数（各域的输出预算与能力开关）
 * 随它们的消费改造分批进来，届时这里会出现"按模块"的入口。
 * </para>
 *
 * <para>
 * **清空取值 = 撤掉这一层的覆盖**，不是"设成空"：与主页面"恢复默认"同一口径。
 * </para>
 */
export function AssistantScopeOverrides() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const scopes = useQuery({ queryKey: ['assistant-admin-scopes'], queryFn: listScopes })

  const [chooserOpen, setChooserOpen] = useState(false)
  const [target, setTarget] = useState<{ id: string; label: string } | null>(null)
  const [paramKey, setParamKey] = useState('')
  const [value, setValue] = useState('')

  /** 只列出声明了"可被用户覆盖"的参数；模块层的随各自的批进来。 */
  const userScopable = useMemo(
    () => (scopes.data?.scopable ?? []).filter(item => item.layers.includes('USER')),
    [scopes.data])

  const rows = scopes.data?.items ?? []

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-scopes'] })
  }

  const save = useMutation({
    mutationFn: () => upsertScope({
      scopeType: 'USER',
      scopeKey: target!.id,
      paramKey,
      value: value.trim(),
    }),
    onSuccess: () => {
      toast.notify({ message: `已为「${target?.id}」保存覆盖，立即生效。`, variant: 'success' })
      setValue('')
      refresh()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存覆盖失败。'), variant: 'danger' }),
  })

  const clearOne = useMutation({
    mutationFn: (row: AssistantScopeOverride) => upsertScope({
      scopeType: row.scopeType,
      scopeKey: row.scopeKey,
      paramKey: row.paramKey,
      value: '',
    }),
    onSuccess: () => {
      toast.notify({ message: '已撤掉这一项覆盖（回到上一层取值）。', variant: 'success' })
      refresh()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '撤掉覆盖失败。'), variant: 'danger' }),
  })

  const clearLayer = useMutation({
    mutationFn: (row: AssistantScopeOverride) => deleteScopeLayer(row.scopeType, row.scopeKey),
    onSuccess: (_result, row) => {
      toast.notify({ message: `已清除「${row.scopeKey}」的全部覆盖。`, variant: 'success' })
      refresh()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '清除失败。'), variant: 'danger' }),
  })

  const columns = useMemo<ColumnDef<AssistantScopeOverride, unknown>[]>(() => [
    {
      accessorKey: 'scopeKey',
      header: '对象',
      cell: info => <span className="font-monospace">{info.getValue<string>()}</span>,
    },
    {
      id: 'param',
      header: '参数',
      cell: ({ row }) => {
        const descriptor = (scopes.data?.scopable ?? []).find(item => item.key === row.original.paramKey)
        return <span title={row.original.paramKey}>{descriptor?.displayName ?? row.original.paramKey}</span>
      },
    },
    {
      accessorKey: 'value',
      header: '覆盖值',
      cell: info => <span className="font-monospace">{info.getValue<string>() ?? '—'}</span>,
    },
    {
      id: 'updated',
      header: '上次修改',
      cell: ({ row }) => row.original.updatedBy
        ? <span className="text-secondary small">
            {row.original.updatedBy}
            {row.original.updatedAt ? ` · ${new Date(row.original.updatedAt).toLocaleString('zh-CN')}` : ''}
          </span>
        : <span className="text-secondary small">—</span>,
    },
    {
      id: 'actions',
      header: '',
      cell: ({ row }) => (
        <div className="d-flex gap-1">
          <Button size="sm" onClick={() => clearOne.mutate(row.original)}
            loading={clearOne.isPending && clearOne.variables?.paramKey === row.original.paramKey}>
            撤掉这一项
          </Button>
          <Button size="sm" icon={<IconTrash size={14} />} onClick={() => clearLayer.mutate(row.original)}
            loading={clearLayer.isPending && clearLayer.variables?.scopeKey === row.original.scopeKey}>
            清除该对象
          </Button>
        </div>
      ),
    },
  ], [scopes.data, clearOne, clearLayer])

  const selectedParam = userScopable.find(item => item.key === paramKey)

  return (
    <section className="border rounded p-3">
      <div className="d-flex align-items-center gap-2 flex-wrap mb-2">
        <h3 className="h5 mb-0">按用户覆盖</h3>
        <span className="text-secondary small">
          取值优先级：用户 → 模块 → 全局；这里设置的是最贴近当事人的那一层
        </span>
      </div>

      {scopes.isPending ? <LoadingState label="正在加载覆盖…" /> : scopes.isError ? (
        <ErrorState message={describeApiError(scopes.error, '加载覆盖失败。')} onRetry={refresh} />
      ) : (
        <>
          {userScopable.length === 0 ? (
            <div className="text-secondary small">
              目前没有声明"可被用户覆盖"的参数，因此这里没有可设的项。
            </div>
          ) : (
            <div className="row g-2 align-items-end mb-3">
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-user">用户</label>
                <div className="d-flex gap-1">
                  <input id="scope-user" className="form-control form-control-sm font-monospace"
                    style={{ maxWidth: 220 }} readOnly value={target?.id ?? ''}
                    placeholder="点击右侧按钮选择" aria-label="目标用户" />
                  <Button size="sm" icon={<IconUserPlus size={14} />} onClick={() => setChooserOpen(true)}>
                    选择
                  </Button>
                </div>
              </div>
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-param">参数</label>
                <select id="scope-param" className="form-select form-select-sm" style={{ maxWidth: 280 }}
                  value={paramKey} onChange={event => setParamKey(event.target.value)}>
                  <option value="">请选择…</option>
                  {userScopable.map(item => (
                    <option key={item.key} value={item.key}>{item.displayName}</option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-value">覆盖值</label>
                <input id="scope-value" className="form-control form-control-sm" style={{ maxWidth: 160 }}
                  value={value} onChange={event => setValue(event.target.value)}
                  placeholder={selectedParam?.rangeHint || '留空 = 撤掉覆盖'} />
              </div>
              <div className="col-auto">
                <Button size="sm" variant="primary"
                  disabled={!target || !paramKey || save.isPending}
                  loading={save.isPending}
                  onClick={() => save.mutate()}>
                  保存覆盖
                </Button>
              </div>
              {selectedParam && (
                <div className="col-12">
                  <div className="form-hint">
                    {selectedParam.displayName}：{selectedParam.displayNameOfPolicy}
                    {selectedParam.unit ? `（单位：${selectedParam.unit}）` : ''}
                    {selectedParam.rangeHint ? `；取值范围：${selectedParam.rangeHint}` : ''}
                    。留空保存 = 撤掉这一层的覆盖。
                  </div>
                </div>
              )}
            </div>
          )}

          <ErpTable<AssistantScopeOverride>
            columns={columns}
            data={rows}
            getRowId={row => `${row.scopeType}|${row.scopeKey}|${row.paramKey}`}
            storageKey="assistant-scope-overrides"
            resizable
            copyable={false}
            clientSideSorting
            empty={<div className="p-3 text-secondary">没有任何覆盖——所有用户都按全局值运行。</div>}
          />
        </>
      )}

      <UnifiedChooser
        open={chooserOpen}
        title="选择用户"
        source={{ kind: 'sourceKey', key: 'rights-admin.users' }}
        mode="single"
        getRowId={(row) => String((row as { USER_ID?: unknown }).USER_ID ?? '').trim()}
        onPick={(picked) => {
          const row = picked[0] as { USER_ID?: unknown } | undefined
          const userId = row ? String(row.USER_ID ?? '').trim() : ''
          if (userId) setTarget({ id: userId, label: userId })
          setChooserOpen(false)
        }}
        onClose={() => setChooserOpen(false)}
        searchPlaceholder="按用户名/工号/姓名搜索"
      />
    </section>
  )
}
