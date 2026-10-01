import { IconAlertTriangle, IconCheck, IconKey, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import {
  activateModel, clearActiveModel, createModel, deleteModel, getModelUsage, listModels, setModelKey, updateModel,
} from './api'
import type { AssistantModelItem, AssistantModelUsageRow, AssistantModelWriteInput, AssistantUsageTrendRow } from './api'

/** 供应商选项。目前都走 OpenAI 兼容协议，所以共用同一个客户端实现。 */
const PROVIDERS = [
  { value: 'deepseek', label: 'DeepSeek（OpenAI 兼容）' },
  { value: 'openai-compatible', label: '其它 OpenAI 兼容厂商' },
]

const providerLabel = (value: string) => PROVIDERS.find(item => item.value === value)?.label ?? value

/** 空字符串代表"不传该参数、用厂商默认"，所以这里区分"空"与"0"。 */
function parseOptionalNumber(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed.length === 0) return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
}

/**
 * 工作助手管理 → **模型与用量**（菜单组 31 / 模块 3102，路由 `/admin/assistant/models`）。
 *
 * <para>
 * **密钥不入库**（ADR-030 §3）：列表里只显示环境变量名与掩码末四位，密钥只在"设置密钥"那一个
 * 弹窗里出现一次、只写不读。库内留下的始终只是变量名，所以数据库备份/导出不含密钥。
 * </para>
 *
 * <para>
 * **当前模型至多一个**，由数据库的筛选唯一索引保证；"设为当前"要求该模型的密钥**已配置**，
 * 否则服务端会拒绝——把没有密钥的模型设成当前，会让所有人的助手立刻不可用而看不出原因。
 * </para>
 */
export function ModelAdminPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [editor, setEditor] = useState<{ model: AssistantModelItem | null } | null>(null)
  const [keyTarget, setKeyTarget] = useState<AssistantModelItem | null>(null)
  const [days, setDays] = useState(30)

  const models = useQuery({ queryKey: ['assistant-admin-models'], queryFn: listModels })
  const usage = useQuery({
    queryKey: ['assistant-admin-model-usage', days],
    queryFn: () => getModelUsage(days),
  })

  const items = useMemo(() => models.data?.items ?? [], [models.data])
  const current = models.data?.current
  const errorMessage = describeApiError(models.error, '加载模型列表失败，请稍后重试。')

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-models'] })
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-model-usage'] })
  }, [queryClient])

  const fail = useCallback((error: unknown, fallback: string) => {
    toast.notify({ message: describeApiError(error, fallback), variant: 'danger' })
  }, [toast])

  const activate = useMutation({
    mutationFn: (modelId: number) => activateModel(modelId),
    onSuccess: () => { refresh(); toast.notify({ message: '已切换当前模型，立即生效（无需重启）。', variant: 'success' }) },
    onError: (error) => fail(error, '切换失败。'),
  })

  const clearActive = useMutation({
    mutationFn: () => clearActiveModel(),
    onSuccess: () => { refresh(); toast.notify({ message: '已取消当前模型，助手回到配置文件里的那套。', variant: 'success' }) },
    onError: (error) => fail(error, '取消失败。'),
  })

  const remove = useMutation({
    mutationFn: (modelId: number) => deleteModel(modelId),
    onSuccess: () => { refresh(); toast.notify({ message: '已删除该模型配置。', variant: 'success' }) },
    onError: (error) => fail(error, '删除失败。'),
  })

  const confirmRemove = useCallback((model: AssistantModelItem) => {
    if (window.confirm(
      `确定删除模型「${model.displayName}」吗？\n\n`
      + `库里那条配置会被删掉；环境变量 ${model.apiKeyEnvVar} 里的密钥不会被清除，`
      + '如果它还被别处引用，请自行清理。')) {
      remove.mutate(model.modelId)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<AssistantModelItem, unknown>[]>(() => [
    {
      accessorKey: 'displayName',
      header: '显示名',
      cell: (info) => (
        <span className="d-inline-flex align-items-center gap-2">
          <span className="fw-semibold">{String(info.getValue())}</span>
          {info.row.original.isActive && <span className="badge bg-green-lt text-success">当前</span>}
          {!info.row.original.enabled && <span className="badge bg-secondary-lt">已停用</span>}
        </span>
      ),
    },
    {
      accessorKey: 'provider',
      header: '供应商',
      meta: { className: 'text-nowrap', minWidth: 150 },
      cell: (info) => <span className="text-secondary">{providerLabel(String(info.getValue()))}</span>,
    },
    {
      accessorKey: 'modelName',
      header: '模型名',
      meta: { className: 'text-nowrap', minWidth: 150 },
      cell: (info) => <span className="font-monospace">{String(info.getValue())}</span>,
    },
    {
      accessorKey: 'baseUrl',
      header: '端点',
      cell: (info) => <span className="text-secondary small font-monospace">{String(info.getValue())}</span>,
    },
    {
      accessorKey: 'apiKeyEnvVar',
      header: '密钥',
      enableSorting: false,
      meta: { className: 'text-nowrap', minWidth: 200 },
      // 只显示"环境变量名 + 掩码末四位"：库里本来就没有密钥可显示
      cell: (info) => {
        const model = info.row.original
        return (
          <span className="d-flex flex-column">
            <span className="font-monospace small">{model.apiKeyEnvVar}</span>
            {model.apiKeyConfigured
              ? <span className="text-secondary small">已配置 {model.apiKeyMaskedTail}</span>
              : <span className="text-danger small">未配置</span>}
          </span>
        )
      },
    },
    {
      accessorKey: 'timeoutSeconds',
      header: '超时',
      meta: { className: 'text-end text-nowrap', minWidth: 80 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)} 秒</span>,
    },
    {
      accessorKey: 'temperature',
      header: '温度',
      meta: { className: 'text-end text-nowrap', minWidth: 70 },
      cell: (info) => <span className="text-secondary">{info.getValue() == null ? '默认' : String(info.getValue())}</span>,
    },
    {
      accessorKey: 'maxTokens',
      header: 'Token上限',
      meta: { className: 'text-end text-nowrap', minWidth: 92 },
      cell: (info) => <span className="text-secondary">{info.getValue() == null ? '默认' : String(info.getValue())}</span>,
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 250, minWidthFloor: true, resizable: false },
      cell: ({ row }) => {
        const model = row.original
        return (
          <div className="d-flex gap-1 justify-content-end">
            {/* 密钥没配就不给点：把一个没有密钥的模型设成当前，会让所有人的助手立刻不可用，
                而原因只写在服务端日志里。禁用按钮的 tooltip 在部分浏览器里不弹，所以"未配置"
                在密钥列已经用红字标了出来——这里只是不让这一步走得通。 */}
            <Button size="sm" variant="ghost" icon={<IconCheck size={14} />}
              title={model.apiKeyConfigured
                ? '设为当前模型（立即生效，无需重启）'
                : `环境变量 ${model.apiKeyEnvVar} 还没有值，先用「密钥」填一次`}
              disabled={model.isActive || !model.enabled || !model.apiKeyConfigured || activate.isPending}
              onClick={() => activate.mutate(model.modelId)}>设为当前</Button>
            <Button size="sm" variant="ghost" icon={<IconKey size={14} />} title="设置密钥（写入环境变量，不入库、不回显）"
              onClick={() => setKeyTarget(model)}>密钥</Button>
            <Button size="sm" variant="ghost" title="编辑（不含密钥）" onClick={() => setEditor({ model })}>编辑</Button>
            <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除（当前模型不可删）"
              disabled={model.isActive || remove.isPending}
              onClick={() => confirmRemove(model)}>删除</Button>
          </div>
        )
      },
    },
  ], [activate, confirmRemove, remove])

  const usageColumns = useMemo<ColumnDef<AssistantModelUsageRow, unknown>[]>(() => [
    {
      accessorKey: 'modelName',
      header: '模型',
      cell: (info) => <span className="font-monospace">{String(info.getValue())}</span>,
    },
    {
      accessorKey: 'requests',
      header: '回复数',
      meta: { className: 'text-end text-nowrap', minWidth: 84 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
    },
    {
      accessorKey: 'promptTokens',
      header: '输入 tokens',
      meta: { className: 'text-end text-nowrap', minWidth: 110 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
    },
    {
      accessorKey: 'completionTokens',
      header: '输出 tokens',
      meta: { className: 'text-end text-nowrap', minWidth: 110 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
    },
    {
      accessorKey: 'estimatedCostYuan',
      header: '估算成本',
      meta: { className: 'text-end text-nowrap', minWidth: 100 },
      cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
    },
  ], [])

  const trendColumns = useMemo<ColumnDef<AssistantUsageTrendRow, unknown>[]>(() => [
    { accessorKey: 'day', header: '日期', meta: { className: 'text-nowrap', minWidth: 110 } },
    {
      accessorKey: 'requests',
      header: '回复数',
      meta: { className: 'text-end text-nowrap', minWidth: 84 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
    },
    {
      accessorKey: 'promptTokens',
      header: '输入 tokens',
      meta: { className: 'text-end text-nowrap', minWidth: 110 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
    },
    {
      accessorKey: 'completionTokens',
      header: '输出 tokens',
      meta: { className: 'text-end text-nowrap', minWidth: 110 },
      cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
    },
    {
      accessorKey: 'estimatedCostYuan',
      header: '估算成本',
      meta: { className: 'text-end text-nowrap', minWidth: 100 },
      cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
    },
  ], [])

  const today = usage.data?.today
  const caps = usage.data?.caps

  return (
    <div className="erp-full-list-page d-flex flex-column gap-3">
      <ErpListCard
        ariaLabel="助手模型管理"
        actions={<>
          {current?.source === 'database' && (
            <Button size="sm" variant="secondary" icon={<IconAlertTriangle size={14} />}
              title="取消当前模型，回到配置文件里的那套"
              disabled={clearActive.isPending} onClick={() => clearActive.mutate()}>取消当前</Button>
          )}
          <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setEditor({ model: null })}>新增模型</Button>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => { refresh() }}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">模型管理</h2>
          <span className="text-secondary small">
            当前生效：
            {current
              ? `${current.displayName ?? current.model}（${current.model}，来源：${current.source === 'database' ? '本页配置' : '配置文件'}）`
              : '配置文件'}
          </span>
          {current && !current.apiKeyConfigured && (
            <span className="badge bg-danger-lt text-danger">当前模型的密钥未配置，助手会调用失败</span>
          )}
          <span className="ms-auto text-secondary small">密钥不入库：库里只存环境变量名，密钥只写不读</span>
        </div>}
      >
        {/* 表为空是**正常状态**：此时助手用配置文件里那套，行为与从前一致 */}
        <div className="alert alert-info mb-0 rounded-0 py-2" role="status">
          <div className="fw-semibold">这里没有记录时，助手用配置文件（appsettings）里的模型</div>
          <div className="small">
            新增一条并「设为当前」后即刻生效（不需要重启服务）；「取消当前」可以随时退回去。
            想保留现状又要纳管，照着上面显示的当前值填一条即可。
          </div>
        </div>
        {models.isPending ? <LoadingState label="正在加载模型…" /> : models.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void models.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => String(row.modelId)}
            resizable
            storageKey="assistant-admin-models"
            empty={<EmptyState title="还没有纳管任何模型"
              description="点右上角「新增模型」，或直接沿用配置文件里的那套。" />}
          />
        )}
      </ErpListCard>

      <ErpListCard
        ariaLabel="助手用量"
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 120 }} value={days}
            aria-label="统计区间" onChange={(event) => setDays(Number(event.target.value))}>
            <option value={7}>近 7 天</option>
            <option value={30}>近 30 天</option>
            <option value={90}>近 90 天</option>
          </select>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void usage.refetch()}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">用量</h2>
          <span className="text-secondary small">
            当日 {today?.requests ?? 0} 次回复、
            {((today?.promptTokens ?? 0) + (today?.completionTokens ?? 0)).toLocaleString('zh-CN')} tokens、
            约 ¥{(today?.estimatedCostYuan ?? 0).toFixed(4)}
            {caps ? `（上限：每人 ¥${caps.userDailyCapYuan}/天、全局 ¥${caps.globalDailyCapYuan}/天）` : ''}
          </span>
          <span className="ms-auto text-secondary small">按 UTC 自然日统计；成本按配置的单价估算</span>
        </div>}
      >
        {usage.isPending ? <LoadingState label="正在汇总用量…" /> : usage.isError ? (
          <ErrorState message={describeApiError(usage.error, '加载用量失败。')} onRetry={() => void usage.refetch()} />
        ) : (
          <div className="p-2 d-flex flex-column gap-3">
            <section>
              <h3 className="h4 mb-2">按模型（近 {usage.data?.days ?? days} 天）</h3>
              {/* 这一档此前根本不存在：MODEL_NAME 一直是只写不聚合 */}
              {(usage.data?.models.length ?? 0) === 0
                ? <EmptyState title="这段时间还没有模型调用记录" description="有回复之后这里会出现按模型的用量。" />
                : <ErpTable columns={usageColumns} data={usage.data?.models ?? []}
                    getRowId={(row) => row.modelName} resizable storageKey="assistant-admin-usage-models" />}
            </section>
            <section>
              <h3 className="h4 mb-2">按天（近 {usage.data?.days ?? days} 天）</h3>
              {(usage.data?.trend.length ?? 0) === 0
                ? <EmptyState title="这段时间还没有调用记录" description="有回复之后这里会出现每日趋势。" />
                : <ErpTable columns={trendColumns} data={[...(usage.data?.trend ?? [])].reverse()}
                    getRowId={(row) => row.day} resizable storageKey="assistant-admin-usage-trend" />}
            </section>
          </div>
        )}
      </ErpListCard>

      {editor && (
        <ModelEditorDialog
          model={editor.model}
          onClose={() => setEditor(null)}
          onSaved={() => { setEditor(null); refresh() }}
        />
      )}

      {keyTarget && (
        <ModelKeyDialog
          model={keyTarget}
          onClose={() => setKeyTarget(null)}
          onSaved={() => { setKeyTarget(null); refresh() }}
        />
      )}
    </div>
  )
}

/** 新增 / 编辑模型。**没有密钥输入框**——密钥是单独的动作，那条路不进库。 */
function ModelEditorDialog({ model, onClose, onSaved }: {
  model: AssistantModelItem | null
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const [form, setForm] = useState<AssistantModelWriteInput>({
    displayName: model?.displayName ?? '',
    provider: model?.provider ?? 'deepseek',
    modelName: model?.modelName ?? 'deepseek-chat',
    baseUrl: model?.baseUrl ?? 'https://api.deepseek.com',
    apiKeyEnvVar: model?.apiKeyEnvVar ?? 'EOS_ASSISTANT_API_KEY',
    timeoutSeconds: model?.timeoutSeconds ?? 300,
    temperature: model?.temperature ?? null,
    maxTokens: model?.maxTokens ?? null,
    enabled: model?.enabled ?? true,
    sortIdx: model?.sortIdx ?? 0,
    remark: model?.remark ?? null,
  })
  const [temperatureText, setTemperatureText] = useState(model?.temperature == null ? '' : String(model.temperature))
  const [maxTokensText, setMaxTokensText] = useState(model?.maxTokens == null ? '' : String(model.maxTokens))

  const save = useMutation({
    // 两者返回类型不同（新增回带主键），统一成 void：调用方不需要那个返回值
    mutationFn: async () => {
      if (model) {
        await updateModel(model.modelId, form)
      } else {
        await createModel(form)
      }
    },
    onSuccess: () => {
      toast.notify({ message: model ? '已保存。' : '已新增。别忘了给它设置密钥，之后才能设为当前。', variant: 'success' })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  const set = <K extends keyof AssistantModelWriteInput>(key: K, value: AssistantModelWriteInput[K]) =>
    setForm(previous => ({ ...previous, [key]: value }))

  const field = (label: string, node: ReactNode, hint?: string) => (
    <div className="mb-2">
      <label className="form-label mb-1">{label}</label>
      {node}
      {hint && <div className="form-hint">{hint}</div>}
    </div>
  )

  return (
    <Modal
      title={model ? `编辑模型：${model.displayName}` : '新增模型'}
      ariaLabel={model ? '编辑模型' : '新增模型'}
      onClose={onClose}
      size="lg"
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>保存</Button>
      </>}
    >
      {field('显示名', <input className="form-control" value={form.displayName}
        aria-label="显示名" onChange={(event) => set('displayName', event.target.value)} />, '管理页上看到的名字，例如「DeepSeek 生产」。')}
      {field('供应商', <select className="form-select" value={form.provider} aria-label="供应商"
        onChange={(event) => set('provider', event.target.value)}>
        {PROVIDERS.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
      </select>)}
      {field('模型名', <input className="form-control font-monospace" value={form.modelName}
        aria-label="模型名" onChange={(event) => set('modelName', event.target.value)} />, '厂商侧的模型标识，例如 deepseek-chat。')}
      {field('端点', <input className="form-control font-monospace" value={form.baseUrl}
        aria-label="端点" onChange={(event) => set('baseUrl', event.target.value)} />, '不含 /chat/completions 的根地址。')}
      {field('密钥环境变量名', <input className="form-control font-monospace" value={form.apiKeyEnvVar}
        aria-label="密钥环境变量名" onChange={(event) => set('apiKeyEnvVar', event.target.value)} />,
        '库里只存这个变量名，密钥本体在环境变量里。改完记得用「密钥」按钮写一次值。')}
      <div className="row">
        <div className="col-md-4">
          {field('超时（秒）', <input type="number" className="form-control" value={form.timeoutSeconds}
            aria-label="超时" onChange={(event) => set('timeoutSeconds', Number(event.target.value))} />)}
        </div>
        <div className="col-md-4">
          {field('温度', <input type="number" step="0.1" className="form-control" value={temperatureText}
            placeholder="留空用默认" aria-label="温度"
            onChange={(event) => { setTemperatureText(event.target.value); set('temperature', parseOptionalNumber(event.target.value)) }} />,
            '0–2；留空表示不传该参数。')}
        </div>
        <div className="col-md-4">
          {field('最大 token', <input type="number" className="form-control" value={maxTokensText}
            placeholder="留空用默认" aria-label="最大 token"
            onChange={(event) => { setMaxTokensText(event.target.value); set('maxTokens', parseOptionalNumber(event.target.value)) }} />,
            '留空表示不传该参数。')}
        </div>
      </div>
      <div className="row">
        <div className="col-md-4">
          {field('排序号', <input type="number" className="form-control" value={form.sortIdx}
            aria-label="排序号" onChange={(event) => set('sortIdx', Number(event.target.value))} />)}
        </div>
        <div className="col-md-8 d-flex align-items-end pb-2">
          <label className="form-check">
            <input type="checkbox" className="form-check-input" checked={form.enabled} aria-label="启用"
              onChange={(event) => set('enabled', event.target.checked)} />
            <span className="form-check-label">启用（停用的模型不能设为当前）</span>
          </label>
        </div>
      </div>
      {field('备注', <input className="form-control" value={form.remark ?? ''}
        aria-label="备注" onChange={(event) => set('remark', event.target.value || null)} />)}
    </Modal>
  )
}

/** 设置密钥：只写不读。 */
function ModelKeyDialog({ model, onClose, onSaved }: {
  model: AssistantModelItem
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const [apiKey, setApiKey] = useState('')

  const save = useMutation({
    mutationFn: () => setModelKey(model.modelId, apiKey.trim()),
    onSuccess: (result) => {
      // persisted 为假说明只有本次进程生效：不能只说"成功"，否则重启后助手会突然不可用
      toast.notify({
        message: result.persisted
          ? `密钥已写入环境变量 ${result.envVar}（${result.maskedTail}），当前进程已生效。`
          : `密钥已写入环境变量 ${result.envVar}（${result.maskedTail}），但只对当前进程生效——`
            + '重启后需要重新设置（该环境不允许写用户级变量）。',
        variant: result.persisted ? 'success' : 'warning',
      })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '写入密钥失败。'), variant: 'danger' }),
  })

  return (
    <Modal title={`设置密钥：${model.displayName}`} ariaLabel="设置密钥" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} disabled={apiKey.trim().length === 0}
          onClick={() => save.mutate()}>写入</Button>
      </>}>
      <div className="alert alert-warning py-2">
        <div className="fw-semibold">密钥不入库、也不会再显示出来</div>
        <div className="small">
          它会写进环境变量 <code>{model.apiKeyEnvVar}</code>（当前进程立即生效，并尽量持久化到当前用户），
          数据库里只保留这个变量名。提交后无法回看，只能重新设置。
        </div>
      </div>
      <label className="form-label mb-1">密钥</label>
      <input type="password" className="form-control font-monospace" value={apiKey} autoFocus
        aria-label="密钥" autoComplete="new-password"
        onChange={(event) => setApiKey(event.target.value)} />
      {model.apiKeyConfigured && (
        <div className="form-hint">当前已配置 {model.apiKeyMaskedTail}，写入会覆盖它。</div>
      )}
    </Modal>
  )
}
