import { IconPlayerPlay, IconRefresh } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useContext, useMemo, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'
import { Button } from '../../components/ui/Button'
import { AuthContext } from '../auth/authContext'
import type { NavigationItem } from '../auth/types'
import { describeApiError } from '../../lib/errors'
import {
  getEvalRun, listEvalLayers, listEvalRuns, runEval,
  type AssistantEvalResult, type AssistantEvalRun,
} from './api'

/**
 * 评估运行（模块 3107，ADR-046 缺口 #4）：把库内用例（3106）跑一轮**确定性检查**。
 *
 * 这一页判的是「尺子还准不准」——用例与当前系统有没有漂移（工具改名、规则删了、原因码不在闭集里），
 * **不判模型答得对不对**（那要判官，ADR-044）。因此它不调用模型、没有费用，运维可以随时跑、反复跑。
 * 两个结论不能混成一张报告：那边回答"模型今天行不行"，这边回答"该改的几处改没改"。
 *
 * 失败清单按**分诊**分组（ADR-046 §3）：每条失败都带"该去哪里改、需要哪个模块的权限"——
 * 客户运维按它自己去改，不必把每条失败转给开发。`code` 类的意思是"这条改不了，提缺陷单"。
 *
 * 「去改」入口的地址**不写死在这里**：按落点的权限模块号，从当前用户的菜单（`/app/bootstrap`
 * 的 navigation）里取该模块的 route。取不到就是没有该模块的权限——那正是要说出口的事实，
 * 所以显示「需授权」而不是给一个点进去会被拦的链接。
 */
type Outcome = 'PASS' | 'FAIL' | 'SKIP'

const OUTCOME_BADGE: Record<Outcome, { text: string; className: string }> = {
  FAIL: { text: '失败', className: 'badge text-bg-danger' },
  PASS: { text: '通过', className: 'badge text-bg-success' },
  SKIP: { text: '跳过', className: 'badge text-bg-secondary' },
}

function formatPercent(rate: number | null): string {
  if (rate === null) return '—'
  return `${(rate * 100).toFixed(1)}%`
}

/** 运行下拉里的一行：时间 + 触发人 + 失败数（失败数是最该先看的那个数）。 */
function runLabel(run: AssistantEvalRun): string {
  const when = run.startedTime.replace('T', ' ').slice(0, 16)
  return `#${run.runId} · ${when} · ${run.runBy} · 失败 ${run.failed}/${run.caseTotal}`
}

/** 菜单树里的叶子 → 模块号到承载页地址。这张表就是"当前用户能去哪几个模块"。 */
function moduleRoutesOf(navigation: NavigationItem[]): Map<number, string> {
  const routes = new Map<number, string>()
  const walk = (items: NavigationItem[]) => {
    for (const item of items) {
      if (item.moduleId !== undefined && item.route) routes.set(item.moduleId, item.route)
      if (item.children && item.children.length > 0) walk(item.children)
    }
  }
  walk(navigation)
  return routes
}

export function EvalRunPage() {
  const queryClient = useQueryClient()
  const openTab = useOpenTab()
  // 直接读 context 而不是 useAuth()：这一页要能在没有 AuthProvider 的隔离渲染（与单测）里渲染，
  // 拿不到菜单就退化为"不给去改入口"，而不是抛错。
  const navigation = useContext(AuthContext)?.bootstrap?.navigation
  const moduleRoutes = useMemo(() => moduleRoutesOf(navigation ?? []), [navigation])
  const [selectedRunId, setSelectedRunId] = useState<number | null>(null)
  // null = 还没动过：默认八层全选。留成 null 而不是把列表抄进状态，是为了"层清单由服务端给"这一条不被缓存挡住。
  const [pickedLayers, setPickedLayers] = useState<string[] | null>(null)
  const [keyword, setKeyword] = useState('')
  const [fixFilter, setFixFilter] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const layersQuery = useQuery({
    queryKey: ['assistant-eval', 'layers'],
    queryFn: listEvalLayers,
    staleTime: 10 * 60_000,
  })
  const runsQuery = useQuery({
    queryKey: ['assistant-eval', 'runs'],
    queryFn: () => listEvalRuns(20),
  })

  const allLayers = useMemo(
    () => (layersQuery.data ?? []).map(layer => layer.layer),
    [layersQuery.data],
  )
  const selectedLayers = pickedLayers ?? allLayers

  const activeRunId = selectedRunId ?? runsQuery.data?.[0]?.runId ?? null
  const reportQuery = useQuery({
    queryKey: ['assistant-eval', 'run', activeRunId],
    queryFn: () => getEvalRun(activeRunId as number),
    enabled: activeRunId !== null,
  })

  const report = reportQuery.data
  const run = report?.run ?? null

  const runMutation = useMutation({
    mutationFn: () => runEval(selectedLayers),
    onSuccess: async (result) => {
      setSelectedRunId(result.run.runId)
      setNotice(
        `已跑完第 ${result.run.runId} 轮：通过 ${result.run.passed}、失败 ${result.run.failed}、`
        + `跳过 ${result.run.skipped}（共 ${result.run.caseTotal} 条）。这一轮没有调用模型。`,
      )
      await queryClient.invalidateQueries({ queryKey: ['assistant-eval', 'runs'] })
      await queryClient.invalidateQueries({ queryKey: ['assistant-eval', 'run'] })
    },
    onError: (exception) => setNotice(describeApiError(exception, '评估没能跑起来。')),
  })

  const rows = useMemo<AssistantEvalResult[]>(() => {
    const results = report?.results ?? []
    const text = keyword.trim().toLowerCase()
    return results.filter(result => {
      if (fixFilter && (result.fixAt ?? 'unknown') !== fixFilter) return false
      if (!text) return true
      return result.caseId.toLowerCase().includes(text)
        || result.layer.toLowerCase().includes(text)
        || (result.reason ?? '').toLowerCase().includes(text)
        || (result.fixAtLabel ?? '').toLowerCase().includes(text)
    })
  }, [fixFilter, keyword, report])

  const columns = useMemo<ColumnDef<AssistantEvalResult, unknown>[]>(() => [
    {
      id: 'outcome',
      header: '结果',
      size: 80,
      enableSorting: false,
      cell: ({ row }) => {
        const badge = OUTCOME_BADGE[(row.original.outcome as Outcome)] ?? OUTCOME_BADGE.SKIP
        return <span className={badge.className}>{badge.text}</span>
      },
    },
    { accessorKey: 'caseId', header: '用例', size: 130, cell: ({ row }) => (
      <span className="font-monospace">{row.original.caseId}</span>
    ) },
    { accessorKey: 'layerLabel', header: '层', size: 70 },
    {
      id: 'fixAt',
      header: '该去哪里改',
      size: 170,
      enableSorting: false,
      cell: ({ row }) => row.original.fixAtLabel
        ? <span className={row.original.fixAt === 'code' ? 'text-danger' : undefined}>
            {row.original.fixAtLabel}
          </span>
        : <span className="text-secondary">—</span>,
    },
    {
      id: 'reason',
      header: '原因（确定性检查）',
      size: 420,
      enableSorting: false,
      cell: ({ row }) => row.original.reason
        ? <span className="small" title={row.original.reason}>{row.original.reason}</span>
        : <span className="text-secondary small">—</span>,
    },
    {
      id: 'suggestion',
      header: '建议动作',
      size: 280,
      enableSorting: false,
      cell: ({ row }) => row.original.fixSuggestion
        ? <span className="small text-secondary">{row.original.fixSuggestion}</span>
        : <span className="text-secondary small">—</span>,
    },
    {
      id: 'permission',
      header: '所需权限',
      size: 110,
      enableSorting: false,
      cell: ({ row }) => row.original.fixPermission
        ? <span className="font-monospace small">{row.original.fixPermission}</span>
        : <span className="text-secondary small">—</span>,
    },
    {
      id: 'action',
      header: '去改',
      size: 90,
      enableSorting: false,
      enableHiding: false,
      cell: ({ row }) => {
        const { fixPermission, outcome, fixAtLabel } = row.original
        if (outcome !== 'FAIL' || !fixPermission) {
          return <span className="text-secondary small">—</span>
        }
        const route = moduleRoutes.get(Number(fixPermission))
        if (!route) {
          // 没有入口 = 没有该模块的权限（或该模块没有承载页）：把事实说出来，不给一个点进去会被拦的链接
          return <span className="text-secondary small" title={`没有模块 ${fixPermission} 的权限，去 2306 申请`}>
            需授权
          </span>
        }
        return (
          <Button
            size="sm"
            variant="ghost"
            aria-label={`去改 ${row.original.caseId}`}
            title={fixAtLabel ? `打开「${fixAtLabel}」对应的页面` : '打开对应的修改页面'}
            onClick={() => openTab(route)}
          >
            去改
          </Button>
        )
      },
    },
  ], [moduleRoutes, openTab])

  const header = (
    <div className="card-header erp-card-toolbar flex-wrap gap-2">
      <h2 className="card-title mb-0">评估运行</h2>
      {/* 标题后不写"确定性层 · 不调用模型 · 判的是……"这类说明句：跑的是哪几层、
          逐条结果与落点都在下面（层勾选 + 汇总条 + 表格），说明句只是把表里的事再说一遍 */}
      <div className="ms-auto d-flex align-items-center gap-2">
        <label className="form-label mb-0 small text-secondary" htmlFor="eval-run-select">轮次</label>
        <select
          id="eval-run-select"
          className="form-select form-select-sm"
          style={{ width: 320 }}
          value={activeRunId ?? ''}
          aria-label="选择评估轮次"
          onChange={(event) => {
            setSelectedRunId(event.target.value ? Number(event.target.value) : null)
            setFixFilter(null)
          }}
        >
          {(runsQuery.data ?? []).length === 0 && <option value="">还没有跑过</option>}
          {(runsQuery.data ?? []).map(item => (
            <option key={item.runId} value={item.runId}>{runLabel(item)}</option>
          ))}
        </select>
      </div>
    </div>
  )

  const before = (
    <div className="px-3 py-2 border-bottom">
      <div className="d-flex flex-wrap align-items-center gap-3">
        <span className="small text-secondary">跑哪几层</span>
        {(layersQuery.data ?? []).map(layer => {
          const on = selectedLayers.includes(layer.layer)
          return (
            <label key={layer.layer} className="form-check form-check-inline mb-0">
              <input
                type="checkbox"
                className="form-check-input"
                checked={on}
                aria-label={`层 ${layer.label}`}
                onChange={() => setPickedLayers(
                  on
                    ? selectedLayers.filter(item => item !== layer.layer)
                    : [...selectedLayers, layer.layer])}
              />
              <span className="form-check-label small">{layer.label}</span>
            </label>
          )
        })}
      </div>

      {run && (
        <div className="d-flex flex-wrap align-items-center gap-3 mt-2" role="status">
          <span className="small">
            第 <strong>{run.runId}</strong> 轮 · {run.runBy} · 用例 {run.caseTotal}
          </span>
          <span className="small">通过 <strong className="text-success">{run.passed}</strong></span>
          <span className="small">失败 <strong className={run.failed > 0 ? 'text-danger' : undefined}>{run.failed}</strong></span>
          <span className="small">跳过 <strong className="text-secondary">{run.skipped}</strong></span>
          <span className="small">通过率 <strong>{formatPercent(run.passRate)}</strong></span>
          {run.status !== 'SUCCEEDED' && <span className="badge text-bg-warning">{run.status}</span>}
          {(report?.byFixAt ?? []).length > 0 && (
            <span className="d-flex flex-wrap align-items-center gap-1">
              <span className="small text-secondary">失败落点</span>
              {(report?.byFixAt ?? []).map(bucket => (
                <button
                  key={bucket.fixAt}
                  type="button"
                  className={`badge ${fixFilter === bucket.fixAt ? 'text-bg-primary' : 'text-bg-light border'}`}
                  aria-label={`筛选落点 ${bucket.label}`}
                  onClick={() => setFixFilter(fixFilter === bucket.fixAt ? null : bucket.fixAt)}
                >
                  {bucket.label} {bucket.count}
                </button>
              ))}
            </span>
          )}
          {run.failed > 0 && (
            <span className="small text-secondary">
              逐条按「该去哪里改」处理；「开发商（缺陷单）」才是真正改不了的
            </span>
          )}
        </div>
      )}
    </div>
  )

  return (
    <div className="erp-base-page erp-page-fill erp-full-list-page">
      {notice && <div className="alert alert-info mb-0 rounded-0 py-2" role="status">{notice}</div>}
      <ErpListGrid
        listId="assistant-eval-results"
        ariaLabel="评估运行与结果"
        columns={columns}
        rows={rows}
        rowKey={(row) => `${row.caseId}`}
        loading={reportQuery.isPending && activeRunId !== null}
        loadingLabel="正在加载评估结果…"
        fetching={reportQuery.isFetching || runsQuery.isFetching}
        error={reportQuery.isError
          ? describeApiError(reportQuery.error, '加载评估结果失败。')
          : runsQuery.isError ? describeApiError(runsQuery.error, '加载评估轮次失败。') : null}
        onRetry={() => {
          void runsQuery.refetch()
          void reportQuery.refetch()
        }}
        empty={<EmptyState
          title={activeRunId === null ? '还没有跑过评估' : '这一轮没有符合条件的用例'}
          description={activeRunId === null
            ? '点「跑一轮」：只跑确定性检查，不调用模型、不产生费用。'
            : '换一个关键词或清掉落点筛选。'}
        />}
        search={<ErpSearchBox
          value={keyword}
          onChange={setKeyword}
          debounceMs={300}
          placeholder="搜索用例 / 层 / 原因 / 落点"
          ariaLabel="搜索评估结果"
        />}
        actions={<>
          <Button
            size="sm"
            variant="primary"
            icon={<IconPlayerPlay size={16} />}
            loading={runMutation.isPending}
            disabled={selectedLayers.length === 0}
            onClick={() => runMutation.mutate()}
          >
            跑一轮
          </Button>
          <Button
            size="sm"
            icon={<IconRefresh size={16} />}
            onClick={() => {
              void runsQuery.refetch()
              if (activeRunId !== null) void reportQuery.refetch()
            }}
          >
            刷新
          </Button>
        </>}
        header={header}
        before={before}
      />
    </div>
  )
}
