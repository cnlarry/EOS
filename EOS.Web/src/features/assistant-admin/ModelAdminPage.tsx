import {
  IconAlertTriangle, IconCloudDownload, IconKey, IconPlus, IconRefresh, IconTrash,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { UseQueryResult } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import type { TabbedPanelTab } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import {
  activateModel, clearActiveModel, deleteModel, deleteProvider, getModelUsage, listProviders,
} from './api'
import type {
  AssistantModelItem, AssistantModelKind, AssistantModelUsage, AssistantModelUsageRow,
  AssistantProviderItem, AssistantUsageTrendRow,
} from './api'
import {
  DiscoverModelsDialog, ModelEditorDialog, PresetPickerDialog, ProviderEditorDialog, ProviderKeyDialog,
} from './ModelAdminDialogs'

/** 数字可空时显示"默认"，避免把"没配"看成"配成了 0"。 */
const orDash = (value: number | null | undefined) => (value == null ? '默认' : String(value))

/**
 * 两个用途各一句话：助手能不能聊天看对话模型，知识库能不能用看嵌入模型。
 *
 * <p>分开说而不是笼统说"模型未配置"：两者坏掉的表现完全不同（前者是"助手不回话"，
 * 后者是"问制度类问题没答案、入库被拒"），合起来说会让人去查错方向。</p>
 */
const KIND_SECTION: Record<AssistantModelKind, { title: string; missing: string }> = {
  CHAT: {
    title: '对话模型',
    missing: '尚未配置对话模型：工作助手当前不可用（发消息会失败）。',
  },
  EMBEDDING: {
    title: '嵌入模型',
    missing: '尚未配置嵌入模型：知识库当前不可用——检索取不到内容，文档入库会被拒'
      + '（报 KB_EMBEDDING_NOT_CONFIGURED 家族的原因）。',
  },
}

/** 用途切换器上的短标签（详情区工具栏要放下"标签 + 条数 + 两个动作按钮"）。 */
const KIND_SHORT: Record<AssistantModelKind, string> = { CHAT: '对话', EMBEDDING: '嵌入' }

/** 页签：模型（配置面）与用量（观测面）是两种读法，混在一页里会互相挤。 */
const PAGE_TABS: TabbedPanelTab<'models' | 'usage'>[] = [
  { key: 'models', label: '模型' },
  { key: 'usage', label: '用量' },
]

/** 用量：按模型聚合的列（表结构固定，放模块级，免得每次渲染重建）。 */
const USAGE_COLUMNS: ColumnDef<AssistantModelUsageRow, unknown>[] = [
  { accessorKey: 'modelName', header: '模型', cell: (info) => <span className="font-monospace">{String(info.getValue())}</span> },
  {
    accessorKey: 'requests', header: '回复数',
    meta: { className: 'text-end text-nowrap', minWidth: 84 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
  },
  {
    accessorKey: 'promptTokens', header: '输入 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'completionTokens', header: '输出 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'estimatedCostYuan', header: '估算成本',
    meta: { className: 'text-end text-nowrap', minWidth: 100 },
    cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
  },
]

/** 用量：按天聚合的列。 */
const TREND_COLUMNS: ColumnDef<AssistantUsageTrendRow, unknown>[] = [
  { accessorKey: 'day', header: '日期', meta: { className: 'text-nowrap', minWidth: 110 } },
  {
    accessorKey: 'requests', header: '回复数',
    meta: { className: 'text-end text-nowrap', minWidth: 84 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
  },
  {
    accessorKey: 'promptTokens', header: '输入 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'completionTokens', header: '输出 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'estimatedCostYuan', header: '估算成本',
    meta: { className: 'text-end text-nowrap', minWidth: 100 },
    cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
  },
]

/**
 * 工作助手管理 → **模型与用量**（菜单组 31 / 模块 3102，路由 `/admin/assistant/models`）。
 *
 * <p>
 * 一页两件事，用**页签**分开：**模型**（配置面：供应商 → 模型）与**用量**（观测面：按模型 / 按天）。
 * 页签内容各自是一整块工作台，且**整页不出滚动条**——`.erp-page-tabs` 把高度接着往下传，
 * 滚动发生在表格内部（表头吸顶）。
 * </p>
 *
 * <p>
 * 「模型」页签是**主子表**：上面是**供应商表**（一行一家 = 端点 + 密钥变量名 + 默认超时），
 * 下面是**该供应商的模型表**。选中哪一家决定下面看什么，所以"这台机器上到底配了什么"
 * 一眼能对上号；此前的写法是每家供应商一张卡片，供应商一多就得来回滚。
 * </p>
 *
 * <p>
 * **密钥不入库**（ADR-030 §3）：列表里只显示环境变量名与掩码末四位。**未配置**（没有"当前模型"
 * 或那把密钥没填）时助手不可用，页面顶部直说这件事并给出去哪儿配。
 * </p>
 */
export function ModelAdminPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [tab, setTab] = useState<'models' | 'usage'>('models')
  const [kind, setKind] = useState<AssistantModelKind>('CHAT')
  const [pickedProviderId, setPickedProviderId] = useState<number | null>(null)
  const [days, setDays] = useState(30)
  const [adding, setAdding] = useState(false)
  const [editProvider, setEditProvider] = useState<AssistantProviderItem | null>(null)
  const [keyProvider, setKeyProvider] = useState<AssistantProviderItem | null>(null)
  const [editModel, setEditModel] = useState<{ model: AssistantModelItem; provider: AssistantProviderItem } | null>(null)
  const [discoverProvider, setDiscoverProvider] = useState<AssistantProviderItem | null>(null)
  const [addModelProvider, setAddModelProvider] = useState<AssistantProviderItem | null>(null)

  const providers = useQuery({ queryKey: ['assistant-admin-providers'], queryFn: listProviders })
  const usage = useQuery({
    queryKey: ['assistant-admin-model-usage', days],
    queryFn: () => getModelUsage(days),
  })

  const items = useMemo(() => providers.data?.providers ?? [], [providers.data])
  const current = providers.data?.current ?? null
  const currentEmbedding = providers.data?.currentEmbedding ?? null
  const usedCodes = useMemo(() => items.map(item => item.code), [items])

  /**
   * 详情区显示哪一家：**选中的那家**，选不中（还没选 / 被删了 / 筛选后不在列表里）回落第一家。
   * 回落放在"算出来"这一步，而不是用 effect 去同步状态——否则删掉一家供应商会有一帧的
   * "详情区空着"，而且状态与列表可能长期不一致。
   */
  const selectedProvider = useMemo(
    () => items.find(item => item.providerId === pickedProviderId) ?? items[0] ?? null,
    [items, pickedProviderId])

  const providerSelection = useMemo<RowSelectionState>(
    () => (selectedProvider ? { [String(selectedProvider.providerId)]: true } : {}),
    [selectedProvider])

  const handleProviderSelectionChange = useCallback((next: RowSelectionState) => {
    const picked = Object.keys(next).find(key => next[key])
    // 空选（再点一次已选中的行）**不当成"取消选择"**：详情区没有"什么都不看"这种状态
    if (picked) setPickedProviderId(Number(picked))
  }, [])

  const kindCounts = useMemo(() => {
    const models = selectedProvider?.models ?? []
    return {
      CHAT: models.filter(model => model.kind === 'CHAT').length,
      EMBEDDING: models.filter(model => model.kind === 'EMBEDDING').length,
    } satisfies Record<AssistantModelKind, number>
  }, [selectedProvider])

  const detailModels = useMemo(
    () => (selectedProvider?.models ?? []).filter(model => model.kind === kind),
    [selectedProvider, kind])

  /** 两个用途里还没有"当前"的那些：缺失要按用途分开说，因为坏掉的表现与处置都不同。 */
  const missingKinds = useMemo(
    () => (['CHAT', 'EMBEDDING'] as const).filter(
      target => (target === 'CHAT' ? current === null : currentEmbedding === null)),
    [current, currentEmbedding])

  /**
   * 每个缺失用途各自的"下一步"：按**卡在哪一步**给，而不是一句万能话。
   *
   * <p>顺序不能颠倒——一个模型都没有时说"去点设为当前"，等于把人指到一个不存在的按钮上；
   * 两个用途各有各的卡点（对话缺模型 / 嵌入缺密钥），所以**不合并成一句**。</p>
   */
  const stepFor = useCallback((target: AssistantModelKind) => {
    if (items.length === 0) {
      return '先「添加供应商」（可从预设目录选一家），再在模型上点「设为当前」'
    }

    const noModel = !items.some(item => item.models.some(model => model.kind === target))
    if (noModel) {
      return target === 'CHAT'
        ? '选中一家供应商 → 「拉取型号」或「新增模型」→ 点「设为当前」'
        : '选中一家供应商 → 拉取型号时把用途选成"嵌入"并给维度（或用「新增模型」）→ 点「设为当前」'
    }

    if (items.some(item => !item.apiKeyConfigured)) {
      return '先在那一行点「密钥」完成设置密钥，再点「设为当前」'
    }

    return '在下面的模型表里点需要那条的「设为当前」'
  }, [items])

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-providers'] })
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
    // 用途必传：不传的话服务端按对话处理，"取消嵌入模型"会静默把对话模型清掉
    mutationFn: (target: AssistantModelKind) => clearActiveModel(target),
    onSuccess: (_result, target) => {
      refresh()
      toast.notify({
        message: `${KIND_SECTION[target].title}已取消当前：该用途回到未配置状态（供应商与模型都还留着）。`,
        variant: 'success',
      })
    },
    onError: (error) => fail(error, '取消失败。'),
  })

  const removeModel = useMutation({
    mutationFn: (modelId: number) => deleteModel(modelId),
    onSuccess: () => { refresh(); toast.notify({ message: '已删除该模型。', variant: 'success' }) },
    onError: (error) => fail(error, '删除失败。'),
  })

  const removeProvider = useMutation({
    mutationFn: (providerId: number) => deleteProvider(providerId),
    onSuccess: () => { refresh(); toast.notify({ message: '已删除该供应商。', variant: 'success' }) },
    onError: (error) => fail(error, '删除失败。'),
  })

  const confirmRemoveModel = useCallback((model: AssistantModelItem) => {
    if (window.confirm(
      `确定删除模型「${model.displayName}」吗？\n\n`
      + '库里这条模型配置会被删掉。它所属供应商的密钥不受影响（密钥是供应商级的）。')) {
      removeModel.mutate(model.modelId)
    }
  }, [removeModel])

  const confirmRemoveProvider = useCallback((provider: AssistantProviderItem) => {
    if (window.confirm(
      `确定删除供应商「${provider.displayName}」吗？\n\n`
      + `环境变量 ${provider.apiKeyEnvVar} 里的密钥不会被清除，如需清理请自行处理。`)) {
      removeProvider.mutate(provider.providerId)
    }
  }, [removeProvider])

  /** 供应商表（主表）：端点、密钥状态、默认超时都摆出来——这些是"这家能不能用"的全部依据。 */
  const providerColumns = useMemo<ColumnDef<AssistantProviderItem, unknown>[]>(() => [
    {
      accessorKey: 'displayName',
      header: '供应商',
      meta: { minWidth: 200 },
      cell: (info) => {
        const provider = info.row.original
        return (
          <span className="d-flex flex-column">
            <span className="d-flex align-items-center gap-2">
              <span>{provider.displayName}</span>
              {current?.providerId === provider.providerId && <span className="badge bg-green-lt text-success">当前对话</span>}
              {currentEmbedding?.providerId === provider.providerId && <span className="badge bg-green-lt text-success">当前嵌入</span>}
              {!provider.enabled && <span className="badge bg-secondary-lt">已停用</span>}
            </span>
            <span className="text-secondary small font-monospace">{provider.code}</span>
          </span>
        )
      },
    },
    {
      accessorKey: 'baseUrl',
      header: '端点',
      meta: { minWidth: 220 },
      cell: (info) => <span className="font-monospace small text-secondary">{String(info.getValue())}</span>,
    },
    {
      id: 'key',
      header: '密钥',
      enableSorting: false,
      meta: { className: 'text-nowrap', minWidth: 200 },
      // 密钥只显示"变量名 + 是否已配置 + 掩码末四位"：库里本来就没有密钥可显示。
      // 变量名为空是**合法**的（无凭据端点，如本机嵌入服务），那种情况不该显示成"未配置"
      cell: ({ row }) => {
        const provider = row.original
        if (!provider.apiKeyEnvVar) {
          return <span className="text-secondary small">无需凭据</span>
        }

        return (
          <span className="small">
            <span className="font-monospace">{provider.apiKeyEnvVar}</span>
            {' '}
            {provider.apiKeyConfigured
              ? <span className="text-secondary">已配置 {provider.apiKeyMaskedTail}</span>
              : <span className="text-danger">未配置</span>}
          </span>
        )
      },
    },
    {
      id: 'timeout',
      header: '默认超时',
      enableSorting: false,
      meta: { className: 'text-nowrap', minWidth: 96 },
      cell: ({ row }) => <span className="text-secondary small">{row.original.timeoutSeconds} 秒</span>,
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', truncate: false, minWidth: 200, minWidthFloor: true, resizable: false },
      cell: ({ row }) => {
        const provider = row.original
        return (
          <div className="d-flex gap-1 justify-content-end">
            <Button size="sm" variant="ghost" icon={<IconKey size={14} />}
              title="设置密钥（写入环境变量，不入库、不回显）"
              onClick={() => setKeyProvider(provider)}>密钥</Button>
            <Button size="sm" variant="ghost" title="编辑供应商" onClick={() => setEditProvider(provider)}>编辑</Button>
            <Button size="sm" variant="ghost" icon={<IconTrash size={14} />}
              title={provider.models.length > 0 ? '名下还有模型，需先删除它们' : '删除供应商'}
              disabled={provider.models.length > 0}
              onClick={() => confirmRemoveProvider(provider)}>删除</Button>
          </div>
        )
      },
    },
  ], [confirmRemoveProvider, current, currentEmbedding])

  /**
   * 模型表（子表）的列**按用途各一套**。不共用一套的理由很实际：嵌入模型没有"最大输出"与
   * "输出单价"，摆在那里只能是空白或误导；而对话模型没有维度。共用的那套会出现"一列里一半的
   * 行是空的"，读的人分不清"这项没有"与"这项没填"。
   */
  const modelColumnsByKind = useMemo(() => {
    const identity: ColumnDef<AssistantModelItem, unknown> = {
      accessorKey: 'modelCode',
      header: '模型',
      meta: { minWidth: 220 },
      cell: (info) => {
        const model = info.row.original
        return (
          <span className="d-flex flex-column">
            <span className="d-flex align-items-center gap-2">
              <span className="font-monospace">{model.modelCode}</span>
              {model.isActive && <span className="badge bg-green-lt text-success">当前</span>}
              {!model.enabled && <span className="badge bg-secondary-lt">已停用</span>}
              {/* "不支持工具"只对对话模型有意义：嵌入模型一律不支持，标出来是噪声 */}
              {model.kind === 'CHAT' && !model.supportsTools && <span className="badge bg-orange-lt">不支持工具</span>}
            </span>
            <span className="text-secondary small">{model.displayName}</span>
          </span>
        )
      },
    }

    const actions: ColumnDef<AssistantModelItem, unknown> = {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', truncate: false, minWidth: 210, minWidthFloor: true, resizable: false },
      cell: ({ row }) => {
        const model = row.original
        return (
          <div className="d-flex gap-1 justify-content-end">
            {/* 密钥没配就不给点：把一个没有密钥的模型设成当前，会让所有人的助手立刻不可用 */}
            <Button size="sm" variant="ghost"
              title={model.isActive ? '已经是当前模型' : '设为当前模型（立即生效，无需重启）'}
              disabled={model.isActive || !model.enabled || activate.isPending}
              onClick={() => activate.mutate(model.modelId)}>设为当前</Button>
            <Button size="sm" variant="ghost" title="编辑（不含密钥）"
              onClick={() => setEditModel({ model, provider: items.find(p => p.providerId === model.providerId)! })}>编辑</Button>
            <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除（当前模型不可删）"
              disabled={model.isActive || removeModel.isPending}
              onClick={() => confirmRemoveModel(model)}>删除</Button>
          </div>
        )
      },
    }

    const unitPrice = (value: number | null | undefined) => (
      // 留空 = 用 3105 里的全局兜底价；不能显示成 0，那会让人以为"免费"
      <span className="text-secondary small">{value == null ? '兜底' : value}</span>
    )

    const chatColumns: ColumnDef<AssistantModelItem, unknown>[] = [
      identity,
      {
        id: 'window',
        header: '窗口 / 最大输出',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 150 },
        // 窗口会被真的用来裁剪历史，所以它不是装饰性字段
        cell: ({ row }) => (
          <span className="text-secondary small">
            {row.original.contextWindow == null ? '未知' : row.original.contextWindow.toLocaleString('zh-CN')}
            {' / '}
            {row.original.maxOutputTokens == null ? '默认' : row.original.maxOutputTokens.toLocaleString('zh-CN')}
          </span>
        ),
      },
      {
        id: 'params',
        header: '温度 / 超时',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 130 },
        cell: ({ row }) => (
          <span className="text-secondary small">
            {orDash(row.original.defaultTemperature)}
            {' / '}
            {row.original.timeoutSeconds == null ? '用供应商' : `${row.original.timeoutSeconds} 秒`}
          </span>
        ),
      },
      {
        id: 'price',
        header: '单价（入 / 出）',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 140 },
        cell: ({ row }) => (
          <span className="text-secondary small">
            {unitPrice(row.original.inputPerMillionYuan)}
            {' / '}
            {unitPrice(row.original.outputPerMillionYuan)}
          </span>
        ),
      },
      actions,
    ]

    const embeddingColumns: ColumnDef<AssistantModelItem, unknown>[] = [
      identity,
      {
        id: 'dimension',
        header: '维度',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 110 },
        // 维度决定向量能不能存进集合：与集合登记不一致时入库会被拒，所以它得显示出来
        cell: ({ row }) => (
          <span className={row.original.dimension == null ? 'text-danger small' : 'text-secondary small'}>
            {row.original.dimension == null ? '缺维度' : `${row.original.dimension} 维`}
          </span>
        ),
      },
      {
        id: 'timeout',
        header: '超时',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 110 },
        cell: ({ row }) => (
          <span className="text-secondary small">
            {row.original.timeoutSeconds == null ? '用供应商' : `${row.original.timeoutSeconds} 秒`}
          </span>
        ),
      },
      {
        id: 'inputPrice',
        header: '输入单价',
        enableSorting: false,
        meta: { className: 'text-nowrap', minWidth: 110 },
        cell: ({ row }) => unitPrice(row.original.inputPerMillionYuan),
      },
      actions,
    ]

    return {
      CHAT: chatColumns,
      EMBEDDING: embeddingColumns,
    } as Record<AssistantModelKind, ColumnDef<AssistantModelItem, unknown>[]>
  }, [activate, confirmRemoveModel, items, removeModel])

  return (
    <div className="erp-full-list-page">
      <TabbedPanel label="模型与用量" className="erp-page-tabs"
        tabs={PAGE_TABS} activeKey={tab} onActiveKeyChange={setTab}>
        {tab === 'models' ? (
          <div className="erp-workbench-page erp-model-admin-page">
            {/* 「未配置」是**正常且必须显眼**的状态：此时助手不可用，而用户看到的是"助手不好用"。
                所以在这里直说、并给出下一步，而不是等有人去点聊天才发现。嵌入那条同理：
                它坏掉时用户看到的是"问制度没答案"，而不会想到是模型没配。

                两个用途都缺时**合成一块**，不是叠两个告警：告警是提醒不是内容，
                而矮窗口里两块告警能把正文挤掉一半（实测 1024×560 下正是如此）。 */}
            {missingKinds.length > 0 && (
              <div className="alert alert-warning mb-0 py-2" role="alert">
                {/* 每个缺失用途一行：标题 + 它自己的下一步。压成一行并用 title 补全——
                    告警不是内容，长句换三四行会把下面的表格挤出窗口 */}
                {missingKinds.map(target => (
                  <div key={target} className="small text-truncate">
                    <span className="fw-semibold">{KIND_SECTION[target].missing}</span>
                    {' '}
                    <span>下一步：{stepFor(target)}</span>
                  </div>
                ))}
              </div>
            )}

            <ErpListCard
              ariaLabel="助手模型供应商"
              actions={<>
                {current && (
                  <Button size="sm" variant="secondary" icon={<IconAlertTriangle size={14} />}
                    title="取消当前对话模型：助手回到未配置状态（供应商与模型都留着）"
                    disabled={clearActive.isPending} onClick={() => clearActive.mutate('CHAT')}>取消对话当前</Button>
                )}
                {currentEmbedding && (
                  <Button size="sm" variant="secondary" icon={<IconAlertTriangle size={14} />}
                    title="取消当前嵌入模型：知识库回到未配置状态（供应商与模型都留着）"
                    disabled={clearActive.isPending} onClick={() => clearActive.mutate('EMBEDDING')}>取消嵌入当前</Button>
                )}
                <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setAdding(true)}>添加供应商</Button>
                <Button size="sm" icon={<IconRefresh size={16} />} onClick={refresh}>刷新</Button>
              </>}
              header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
                <h2 className="card-title mb-0">模型管理</h2>
                {/* 两个用途各说一句：它们坏掉的表现完全不同（"助手不回话"对"问制度没答案"），
                    合成一句"模型未配置"会让人去查错方向 */}
                <span className="text-secondary small">
                  对话：
                  {current
                    ? `${current.providerDisplayName} / ${current.modelCode}（${current.displayName}）`
                    : '尚未配置'}
                </span>
                <span className="text-secondary small">
                  嵌入：
                  {currentEmbedding
                    ? `${currentEmbedding.providerDisplayName} / ${currentEmbedding.modelCode}`
                      + `${currentEmbedding.dimension ? `（${currentEmbedding.dimension} 维）` : ''}`
                    : '尚未配置'}
                </span>
                <span className="ms-auto text-secondary small">
                  密钥不入库：库里只存环境变量名，密钥只写不读
                </span>
              </div>}
            >
              {/* 主表：供应商。选中一行决定下面那张模型表看谁 */}
              <div className="erp-master-table-region">
                {providers.isPending ? <LoadingState label="正在加载模型配置…" /> : providers.isError ? (
                  <ErrorState message={describeApiError(providers.error, '加载模型配置失败，请稍后重试。')}
                    onRetry={() => void providers.refetch()} />
                ) : items.length === 0 ? (
                  <EmptyState title="还没有供应商"
                    description="点上方「添加供应商」从预设目录选一家，或选「自定义」自己填端点。" />
                ) : (
                  <ErpTable columns={providerColumns} data={items}
                    getRowId={(row) => String(row.providerId)}
                    empty={<EmptyState title="还没有供应商" description="点「添加供应商」建立第一个接入点。" />}
                    resizable storageKey="assistant-admin-providers"
                    rowClickSingleSelect
                    rowSelection={providerSelection}
                    onRowSelectionChange={handleProviderSelectionChange} />
                )}
              </div>
            </ErpListCard>

            {/* 子表：所选供应商的模型。用途用一个小切换器分开，而不是并成一张表——
                两个用途的列根本不一样（嵌入看维度、对话看窗口与输出） */}
            <section className="card erp-detail-card">
              <div className="card-header erp-detail-toolbar d-flex align-items-center gap-2 flex-wrap">
                <span className="small fw-semibold">
                  {selectedProvider ? `${selectedProvider.displayName} 的模型` : '模型'}
                </span>
                <div className="btn-group btn-group-sm" role="group" aria-label="模型用途">
                  {(['CHAT', 'EMBEDDING'] as const).map(item => (
                    <button key={item} type="button"
                      className={`btn ${kind === item ? 'btn-secondary' : 'btn-outline-secondary'}`}
                      onClick={() => setKind(item)}>
                      {KIND_SHORT[item]}（{kindCounts[item]}）
                    </button>
                  ))}
                </div>
                <span className="ms-auto d-flex gap-1">
                  <Button size="sm" variant="ghost" icon={<IconCloudDownload size={14} />}
                    disabled={!selectedProvider}
                    title="向厂商拉取可用型号，勾选后落库（拉取失败会说明原因，不会退回预设清单）"
                    onClick={() => selectedProvider && setDiscoverProvider(selectedProvider)}>拉取型号</Button>
                  <Button size="sm" variant="ghost" icon={<IconPlus size={14} />}
                    disabled={!selectedProvider}
                    title="手工新增一个模型（厂商拉不到的型号、自建端点走这里）"
                    onClick={() => selectedProvider && setAddModelProvider(selectedProvider)}>新增模型</Button>
                </span>
              </div>
              {selectedProvider ? (
                <ErpTable columns={modelColumnsByKind[kind]} data={detailModels}
                  getRowId={(row) => String(row.modelId)}
                  empty={<EmptyState
                    title={kind === 'CHAT' ? '这家还没有对话模型' : '这家还没有嵌入模型'}
                    description={kind === 'CHAT'
                      ? '用「拉取型号」从厂商拉取后勾选落库，或用「新增模型」手工填。'
                      : '嵌入模型供知识库使用：拉取型号时把用途选成"嵌入"并给维度，或用「新增模型」手工填。'} />}
                  resizable
                  storageKey={`assistant-admin-models-${kind}-${selectedProvider.providerId}`} />
              ) : (
                <EmptyState title="还没有供应商" description="先在上表添加供应商，再维护它的模型。" />
              )}
            </section>
          </div>
        ) : (
          <UsageTab days={days} onDaysChange={setDays} usage={usage}
            onRefresh={() => void usage.refetch()} />
        )}
      </TabbedPanel>

      {adding && (
        <PresetPickerDialog usedCodes={usedCodes}
          onClose={() => setAdding(false)}
          onSaved={() => { setAdding(false); refresh() }} />
      )}
      {editProvider && (
        <ProviderEditorDialog provider={editProvider} usedCodes={usedCodes}
          onClose={() => setEditProvider(null)}
          onSaved={() => { setEditProvider(null); refresh() }} />
      )}
      {keyProvider && (
        <ProviderKeyDialog provider={keyProvider}
          onClose={() => setKeyProvider(null)}
          onSaved={() => { setKeyProvider(null); refresh() }} />
      )}
      {editModel && (
        <ModelEditorDialog model={editModel.model} providerId={editModel.provider.providerId} providers={items}
          onClose={() => setEditModel(null)}
          onSaved={() => { setEditModel(null); refresh() }} />
      )}
      {discoverProvider && (
        <DiscoverModelsDialog provider={discoverProvider}
          onClose={() => setDiscoverProvider(null)}
          onSaved={() => { setDiscoverProvider(null); refresh() }} />
      )}
      {/* 同一个对话框的"新增"形态：厂商拉不到的型号（自建端点、刚出的新型号）从这里手工加 */}
      {addModelProvider && (
        <ModelEditorDialog model={null} providerId={addModelProvider.providerId} providers={items}
          onClose={() => setAddModelProvider(null)}
          onSaved={() => { setAddModelProvider(null); refresh() }} />
      )}
    </div>
  )
}

/** 「用量」页签：沿用工作台的主子表节奏——上面是按模型，下面按天（各自内部滚动）。 */
function UsageTab({ days, onDaysChange, usage, onRefresh }: {
  days: number
  onDaysChange: (days: number) => void
  usage: UseQueryResult<AssistantModelUsage, Error>
  onRefresh: () => void
}) {
  const today = usage.data?.today
  const caps = usage.data?.caps
  const models = usage.data?.models ?? []
  const trend = usage.data?.trend ?? []

  return (
    <div className="erp-workbench-page erp-model-admin-page">
      <ErpListCard
        ariaLabel="助手用量"
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 120 }} value={days}
            aria-label="统计区间" onChange={(event) => onDaysChange(Number(event.target.value))}>
            <option value={7}>近 7 天</option>
            <option value={30}>近 30 天</option>
            <option value={90}>近 90 天</option>
          </select>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={onRefresh}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">用量</h2>
          <span className="text-secondary small">
            按模型（近 {usage.data?.days ?? days} 天）
          </span>
          <span className="text-secondary small">
            当日 {today?.requests ?? 0} 次回复、
            {((today?.promptTokens ?? 0) + (today?.completionTokens ?? 0)).toLocaleString('zh-CN')} tokens、
            约 ¥{(today?.estimatedCostYuan ?? 0).toFixed(4)}
            {caps ? `（上限：每人 ¥${caps.userDailyCapYuan}/天、全局 ¥${caps.globalDailyCapYuan}/天）` : ''}
          </span>
          <span className="ms-auto text-secondary small">按 UTC 自然日统计；成本按各模型单价估算</span>
        </div>}
      >
        <div className="erp-master-table-region">
          {usage.isPending ? <LoadingState label="正在汇总用量…" /> : usage.isError ? (
            <ErrorState message={describeApiError(usage.error, '加载用量失败。')} onRetry={onRefresh} />
          ) : models.length === 0 ? (
            <EmptyState title="这段时间还没有模型调用记录" description="有回复之后这里会出现按模型的用量。" />
          ) : (
            <ErpTable columns={USAGE_COLUMNS} data={models}
              getRowId={(row) => row.modelName} resizable storageKey="assistant-admin-usage-models" />
          )}
        </div>
      </ErpListCard>

      <section className="card erp-detail-card">
        <div className="card-header erp-detail-toolbar d-flex align-items-center gap-2">
          <span className="small fw-semibold">按天（近 {usage.data?.days ?? days} 天）</span>
        </div>
        {usage.isPending ? <LoadingState label="正在汇总用量…" /> : trend.length === 0 ? (
          <EmptyState title="这段时间还没有调用记录" description="有回复之后这里会出现每日趋势。" />
        ) : (
          // 倒序：最近的一天在上面（按天的表默认是升序，读的人第一眼想看今天）
          <ErpTable columns={TREND_COLUMNS} data={[...trend].reverse()}
            getRowId={(row) => row.day} resizable storageKey="assistant-admin-usage-trend" />
        )}
      </section>
    </div>
  )
}
