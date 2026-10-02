import { IconSearch, IconTrash, IconUserPlus } from '@tabler/icons-react'
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
 * 助手设置 → **按模块 / 按用户覆盖**（ADR-030 §6.2）。
 *
 * <para>
 * 参数有三层取值：**用户 &gt; 模块 &gt; 全局**。这里管的是全局之外的两层：
 * "这个模块上的助手更严"（如关掉某模块的删除动作族）与"给某个人单独调高日限额"。
 * 都不是越权——收紧只能更严、放宽是管理决定，而每一次改动都进审计
 * （`ASSISTANT_PARAM_SCOPE` 资源），所以"谁在什么时候把谁的限额调高了"是可回答的。
 * </para>
 *
 * <para>
 * **可选参数由服务端给出**（`scopable`）：哪条参数能被覆盖、允许出现在哪些层、能不能放宽，
 * 都在参数目录里声明，界面只按所选层过滤显示——front-end 不自己判断"这条应该能改吧"。
 * 层也是**选出来的**，不是写死的：哪条能按模块、哪条能按用户，目录说了算，
 * 所以这里不会出现"某天多了一条模块层参数、界面却仍然只发 USER"这种静默错配。
 * </para>
 *
 * <para>
 * **模块候选集来自服务端选择器**（`assistant-admin.modules`，权限门 3105）：它的候选集与服务端
 * 校验作用域键时查的那张表同源，因此"能选中的"与"能存进去的"是同一批。
 * </para>
 *
 * <para>
 * **清空取值 = 撤掉这一层的覆盖**，不是"设成空"：与主页面"恢复默认"同一口径。
 * </para>
 */

/** 两层：值即服务端的作用域类型（`ScopeType`），不在前端另立映射表。 */
const LAYERS = [
  { value: 'MODULE', label: '按模块', target: '模块', hint: '这个模块上的助手更严' },
  { value: 'USER', label: '按用户', target: '用户', hint: '给某个人单独调整' },
] as const

type ScopeLayer = typeof LAYERS[number]['value']

export function AssistantScopeOverrides() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const scopes = useQuery({ queryKey: ['assistant-admin-scopes'], queryFn: listScopes })

  const [layer, setLayer] = useState<ScopeLayer>('USER')
  const [chooserOpen, setChooserOpen] = useState(false)
  const [target, setTarget] = useState<{ id: string; label: string } | null>(null)
  const [paramKey, setParamKey] = useState('')
  const [value, setValue] = useState('')

  const currentLayer = LAYERS.find(item => item.value === layer)!

  /**
   * 只列出**当前这一层**能覆盖的参数。切换层要把已选对象与参数一起清掉：
   * 一个模块号放进用户层不是"无效输入"，而是一个**会写错对象**的输入——服务端会拒，
   * 但拒的理由（"用户 1401 不存在"）对操作者毫无帮助。
   */
  const scopable = useMemo(
    () => (scopes.data?.scopable ?? []).filter(item => item.layers.includes(layer)),
    [scopes.data, layer])

  const switchLayer = (next: ScopeLayer) => {
    setLayer(next)
    setTarget(null)
    setParamKey('')
    setValue('')
  }

  const rows = scopes.data?.items ?? []

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-scopes'] })
  }

  const save = useMutation({
    mutationFn: () => upsertScope({
      // 层跟着界面所选走：写死 USER 时，模块层参数（动作族）在界面上永远配不出来
      scopeType: layer,
      scopeKey: target!.id,
      paramKey,
      value: value.trim(),
    }),
    onSuccess: () => {
      toast.notify({
        message: `已为${currentLayer.target}「${target?.label ?? target?.id}」保存覆盖，立即生效。`,
        variant: 'success',
      })
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
      // 层级必须显示：同一张表里"1401"既可能是一个模块号、也可能是一个用户名，
      // 不标层级时两种行长得一模一样（撤掉时点错对象的代价是静默失效）
      id: 'layer',
      header: '层级',
      cell: ({ row }) =>
        <span>{LAYERS.find(item => item.value === row.original.scopeType)?.label ?? row.original.scopeType}</span>,
    },
    {
      accessorKey: 'scopeKey',
      header: '对象',
      // 号是身份（服务端按它匹配），名字是给人看的：两个都要，缺了名字这一列就是天书
      cell: info => (
        <span className="d-flex flex-column">
          <span className="font-monospace">{info.getValue<string>()}</span>
          {info.row.original.scopeLabel && (
            <span className="text-secondary small">{info.row.original.scopeLabel}</span>
          )}
        </span>
      ),
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

  const selectedParam = scopable.find(item => item.key === paramKey)

  return (
    <section className="border rounded p-3">
      <div className="d-flex align-items-center gap-2 flex-wrap mb-2">
        <h3 className="h5 mb-0">按模块 / 按用户覆盖</h3>
        <span className="text-secondary small">
          取值优先级：用户 → 模块 → 全局；这里设置的是全局之上的那两层
        </span>
      </div>

      {scopes.isPending ? <LoadingState label="正在加载覆盖…" /> : scopes.isError ? (
        <ErrorState message={describeApiError(scopes.error, '加载覆盖失败。')} onRetry={refresh} />
      ) : (
        <>
          {scopable.length === 0 ? (
            <div className="text-secondary small">
              目前没有声明"可被{currentLayer.target}覆盖"的参数，因此这一层没有可设的项。
            </div>
          ) : (
            <div className="row g-2 align-items-end mb-3">
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-layer">层级</label>
                <select id="scope-layer" className="form-select form-select-sm" style={{ maxWidth: 140 }}
                  value={layer} onChange={event => switchLayer(event.target.value as ScopeLayer)}
                  aria-label="作用域层级">
                  {LAYERS.map(item => (
                    <option key={item.value} value={item.value} title={item.hint}>{item.label}</option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-target">{currentLayer.target}</label>
                <div className="d-flex gap-1">
                  <input id="scope-target" className="form-control form-control-sm font-monospace"
                    style={{ maxWidth: 220 }} readOnly value={target?.label ?? ''}
                    placeholder="点击右侧按钮选择" aria-label={`目标${currentLayer.target}`} />
                  <Button size="sm"
                    icon={layer === 'USER' ? <IconUserPlus size={14} /> : <IconSearch size={14} />}
                    onClick={() => setChooserOpen(true)}>
                    选择
                  </Button>
                </div>
              </div>
              <div className="col-auto">
                <label className="form-label small mb-1" htmlFor="scope-param">参数</label>
                <select id="scope-param" className="form-select form-select-sm" style={{ maxWidth: 280 }}
                  value={paramKey} onChange={event => setParamKey(event.target.value)}>
                  <option value="">请选择…</option>
                  {scopable.map(item => (
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
            empty={<div className="p-3 text-secondary">没有任何覆盖——所有模块与用户都按全局值运行。</div>}
          />
        </>
      )}

      {layer === 'USER' ? (
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
      ) : (
        // 模块候选集来自服务端（`assistant-admin.modules`，权限门 3105）：它的候选集与
        // 服务端校验作用域键时查的那张表同源——"能选中的"与"能存进去的"是同一批
        <UnifiedChooser
          open={chooserOpen}
          title="选择模块"
          source={{ kind: 'sourceKey', key: 'assistant-admin.modules' }}
          mode="single"
          getRowId={(row) => String((row as { M_IDX?: unknown }).M_IDX ?? '').trim()}
          onPick={(picked) => {
            const row = picked[0] as { M_IDX?: unknown; M_DESC?: unknown } | undefined
            const moduleId = row ? String(row.M_IDX ?? '').trim() : ''
            const moduleName = row ? String(row.M_DESC ?? '').trim() : ''
            if (moduleId) {
              // 存的是模块号（服务端按 MODULES.M_IDX 校验），显示带上名字便于核对
              setTarget({ id: moduleId, label: moduleName ? `${moduleId} ${moduleName}` : moduleId })
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
          searchPlaceholder="按模块号/模块名搜索"
        />
      )}
    </section>
  )
}
