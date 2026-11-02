import { IconAlertTriangle, IconPlayerPlay, IconRefresh } from '@tabler/icons-react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { Button } from '../../components/ui/Button'
import { describeApiError } from '../../lib/errors'
import { getAssistantVerdicts, runAssistantJudge } from './api'
import type { AssistantAnswerQuality, AssistantJudgeClauseStat, AssistantQualityAlert, AssistantVerdictRow } from './api'

const CLAUSE_COLUMNS: ColumnDef<AssistantJudgeClauseStat>[] = [
  { header: '条款', accessorKey: 'clause' },
  { header: '判什么', accessorKey: 'title' },
  { header: '通过', accessorKey: 'pass' },
  { header: '不通过', accessorKey: 'fail' },
  { header: '不适用', accessorKey: 'na' },
  { header: '无法判定', accessorKey: 'unsure' },
  {
    header: '通过率',
    id: 'passRate',
    cell: ({ row }) => formatRate(row.original.passRate),
  },
]

const VERDICT_COLUMNS: ColumnDef<AssistantVerdictRow>[] = [
  {
    header: '判定时间',
    id: 'judgedAt',
    cell: ({ row }) => new Date(row.original.judgedAt).toLocaleString('zh-CN'),
  },
  { header: '会话', accessorKey: 'sessionId' },
  { header: '条款', accessorKey: 'clause' },
  {
    header: '结论',
    id: 'verdict',
    cell: ({ row }) => (
      <span className={row.original.verdict === 'FAIL' ? 'text-danger fw-semibold' : undefined}>
        {verdictLabel(row.original.verdict)}
      </span>
    ),
  },
  {
    header: '证据（回答原文引用）',
    id: 'evidence',
    meta: { truncate: false },
    cell: ({ row }) => row.original.evidence || <span className="text-secondary">（无）</span>,
  },
  {
    header: '备注',
    id: 'note',
    meta: { truncate: false },
    cell: ({ row }) => row.original.note || '',
  },
  {
    header: '判官模型',
    id: 'judgeModel',
    cell: ({ row }) => (
      <>
        {row.original.judgeModel}
        {row.original.fallback ? <span className="text-secondary"> · 回落</span> : null}
        {row.original.currentRubric ? null : <span className="text-warning"> · 旧条款版本</span>}
      </>
    ),
  },
]

const verdictLabel = (verdict: string) => ({
  PASS: '通过', FAIL: '不通过', NA: '不适用', UNSURE: '无法判定',
}[verdict] ?? verdict)

const formatRate = (rate: number | null | undefined) =>
  rate == null ? '—' : `${(rate * 100).toFixed(1)}%`

/**
 * 3102「质量」页签的**回答质量**区块（ADR-044 J1）。
 *
 * <p>
 * 四个数字的口径由服务端 `AssistantJudgeMetrics` 一处定义，这里只显示：
 * 口径标注率、范围一致率、越界承诺数（目标恒 0）、无法判定率。**越界承诺 > 0 用红色**——
 * 它不是"略差"，而是红线被击穿，必须在界面上立刻看得见（ADR-044 §7）。
 * </p>
 *
 * <p>
 * 「已达上限」单独一行说清楚：判定到顶后**没有新结论**，这与"判定结果是好的"完全不是一回事，
 * 界面上不写出来就会被读成后者。
 * </p>
 */
export function AnswerQualityPanel({
  days, answerQuality, alerts, onRefresh,
}: {
  days: number
  answerQuality: AssistantAnswerQuality | null
  alerts: AssistantQualityAlert[]
  onRefresh: () => void
}) {
  const queryClient = useQueryClient()
  const [clause, setClause] = useState('')
  const [sessionId, setSessionId] = useState('')
  const [verdict, setVerdict] = useState('')
  const [page, setPage] = useState(1)

  const parsedSession = sessionId.trim() === '' ? undefined : Number(sessionId)
  const sessionValid = parsedSession === undefined || Number.isFinite(parsedSession)

  const verdicts = useQuery({
    queryKey: ['assistant-answer-verdicts', days, clause, parsedSession ?? null, verdict, page],
    queryFn: () => getAssistantVerdicts({
      days, clause: clause || undefined, sessionId: parsedSession, verdict: verdict || undefined,
      page, pageSize: 50,
    }),
    enabled: sessionValid,
    placeholderData: keepPreviousData,
  })

  const runJudge = useMutation({
    mutationFn: () => runAssistantJudge(),
    onSuccess: () => {
      // 手动触发与后台扫描共用一条实现：跑完两处数据都要重取，不能只刷其中一个
      void queryClient.invalidateQueries({ queryKey: ['assistant-answer-verdicts'] })
      onRefresh()
    },
  })

  const clauses = answerQuality?.clauses ?? []
  const rows = verdicts.data?.items ?? []
  const total = verdicts.data?.total ?? 0
  const pageCount = Math.max(1, Math.ceil(total / (verdicts.data?.pageSize ?? 50)))

  return (
    <>
      <section className="card erp-detail-card" aria-label="回答质量">
        <div className="card-header erp-card-toolbar">
          <span className="small fw-semibold">回答质量</span>
          <span className="text-secondary small ms-2">
            条款 {answerQuality?.rubricVersion ?? '—'} · 只判「有没有如实回答」，不判数值真值、不判权限
          </span>
          <div className="ms-auto d-flex align-items-center gap-2">
            {answerQuality?.judgeModels.length ? (
              <span className="text-secondary small">判官：{answerQuality.judgeModels.join('、')}</span>
            ) : (
              <span className="text-secondary small">还没有判定记录</span>
            )}
            <Button
              size="sm"
              variant="ghost"
              icon={<IconPlayerPlay size={14} />}
              disabled={runJudge.isPending}
              onClick={() => runJudge.mutate()}
            >
              判一轮
            </Button>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={14} />}
              onClick={() => { onRefresh(); void verdicts.refetch() }}>
              刷新
            </Button>
          </div>
        </div>

        {alerts.length > 0 ? (
          <div className="px-3 pt-3 d-flex flex-column gap-2">
            {alerts.map((alert) => (
              <div key={alert.code}
                className={`alert ${alert.critical ? 'alert-danger' : 'alert-warning'} py-2 mb-0 small`}
                role={alert.critical ? 'alert' : 'status'}>
                <strong>{alert.title}：</strong>{alert.message}
              </div>
            ))}
          </div>
        ) : null}

        {runJudge.isError ? (
          <div className="px-3 pt-3">
            <ErrorState message={describeApiError(runJudge.error, '手动判定失败。')}
              onRetry={() => runJudge.mutate()} />
          </div>
        ) : null}

        {runJudge.data ? (
          <div className="px-3 pt-3">
            <div className="alert alert-info py-2 mb-0 small">
              本轮：取到 {runJudge.data.candidates} 条，判了 {runJudge.data.judged} 条，
              抽样跳过 {runJudge.data.skippedBySampling} 条，失败 {runJudge.data.failed} 条，
              花费 {runJudge.data.costYuan.toFixed(4)} 元。
              {runJudge.data.stopReason ? ` ${runJudge.data.stopReason}` : ''}
            </div>
          </div>
        ) : null}

        {answerQuality?.capped ? (
          <div className="px-3 pt-3">
            <div className="alert alert-warning py-2 mb-0 small d-flex align-items-center gap-2">
              <IconAlertTriangle size={16} />
              <span>
                今日判定已达上限（{answerQuality.dailyCalls}/{answerQuality.dailyMaxCalls} 次，
                {answerQuality.dailySpentYuan.toFixed(4)}/{answerQuality.dailyCostCapYuan.toFixed(2)} 元）：
                此后不再产出新结论——这是「没判」，不是「没问题」。
              </span>
            </div>
          </div>
        ) : null}

        <div className="erp-quality-metrics">
          <MetricCard
            title="口径标注率"
            value={formatRate(answerQuality?.citationRate)}
            hint="A · 涉及金额/数量时点名口径与来源"
          />
          <MetricCard
            title="范围一致率"
            value={formatRate(answerQuality?.scopeRate)}
            hint="B · 回答范围与用户问的范围一致"
          />
          <MetricCard
            title="越界承诺数"
            value={String(answerQuality?.overreachCount ?? 0)}
            hint="D · 声称做了不可代签的动作（目标恒 0）"
            danger={(answerQuality?.overreachCount ?? 0) > 0}
          />
          <MetricCard
            title="无法判定率"
            value={formatRate(answerQuality?.unsureRate)}
            hint="UNSURE 占比 · 高了说明条款写不清"
          />
        </div>

        <div className="px-3 pb-2 text-secondary small">
          近 {days} 天判了 <strong>{answerQuality?.judged ?? 0}</strong> 条回答；
          今日已判 {answerQuality?.dailyCalls ?? 0} 次、花费 {(answerQuality?.dailySpentYuan ?? 0).toFixed(4)} 元
          （判定用量记在系统账户，不扣任何人的日上限）；
          区间内判定花费 {(answerQuality?.spentYuan ?? 0).toFixed(4)} 元。
        </div>

        <div className="erp-master-table-region">
          {clauses.length === 0 ? (
            <EmptyState title="这段时间还没有判定结论"
              description="后台会按抽样自动判；也可以点上方「判一轮」先跑几条看看。" />
          ) : (
            <ErpListGrid
              plain
              listId="assistant-answer-quality-clauses"
              columns={CLAUSE_COLUMNS}
              rows={clauses}
              rowKey={(row) => row.clause}
              sorting={{ mode: 'client' }}
            />
          )}
        </div>
      </section>

      <section className="card erp-detail-card" aria-label="判定结论">
        <div className="card-header erp-card-toolbar">
          <span className="small fw-semibold">判定结论</span>
          <div className="ms-auto d-flex align-items-center gap-2">
            <select className="form-select form-select-sm" style={{ width: 110 }} value={clause}
              aria-label="按条款筛选" onChange={(event) => { setClause(event.target.value); setPage(1) }}>
              <option value="">全部条款</option>
              {clauses.map((item) => (
                <option key={item.clause} value={item.clause}>{item.clause} · {item.title}</option>
              ))}
            </select>
            <select className="form-select form-select-sm" style={{ width: 110 }} value={verdict}
              aria-label="按结论筛选" onChange={(event) => { setVerdict(event.target.value); setPage(1) }}>
              <option value="">全部结论</option>
              <option value="FAIL">不通过</option>
              <option value="UNSURE">无法判定</option>
              <option value="PASS">通过</option>
              <option value="NA">不适用</option>
            </select>
            <input className="form-control form-control-sm" style={{ width: 130 }} value={sessionId}
              aria-label="按会话编号筛选" placeholder="会话编号"
              onChange={(event) => { setSessionId(event.target.value); setPage(1) }} />
          </div>
        </div>

        <div className="erp-master-table-region">
          {!sessionValid ? (
            <div className="p-3 text-danger small">会话编号必须是数字。</div>
          ) : verdicts.isPending ? (
            <LoadingState label="正在读取判定结论…" />
          ) : verdicts.isError ? (
            <ErrorState message={describeApiError(verdicts.error, '加载判定结论失败。')}
              onRetry={() => void verdicts.refetch()} />
          ) : rows.length === 0 ? (
            <EmptyState title="没有符合条件的结论" description="换个条款或清空会话编号再试。" />
          ) : (
            <ErpListGrid
              plain
              listId="assistant-answer-verdicts"
              columns={VERDICT_COLUMNS}
              rows={rows}
              rowKey={(row) => String(row.verdictId)}
              fetching={verdicts.isFetching}
              sorting={{ mode: 'client' }}
            />
          )}
        </div>

        <div className="card-footer erp-pagination-footer">
          <span className="text-secondary small">共 {total} 条</span>
          <div className="ms-auto d-flex align-items-center gap-2">
            <Button size="sm" variant="ghost" disabled={page <= 1 || verdicts.isFetching}
              onClick={() => setPage((current) => Math.max(1, current - 1))}>
              上一页
            </Button>
            <span className="text-secondary small">{page} / {pageCount}</span>
            <Button size="sm" variant="ghost" disabled={page >= pageCount || verdicts.isFetching}
              onClick={() => setPage((current) => current + 1)}>
              下一页
            </Button>
          </div>
        </div>
      </section>
    </>
  )
}

function MetricCard({
  title, value, hint, danger = false,
}: { title: string; value: string; hint: string; danger?: boolean }) {
  return (
    <article className="card erp-metric-card">
      <div className="card-body py-2">
        <div className="text-secondary fw-medium">{title}</div>
        <div className={`h2 mb-1 mt-1 ${danger ? 'text-danger' : ''}`}>{value}</div>
        <div className="text-secondary small">{hint}</div>
      </div>
    </article>
  )
}
