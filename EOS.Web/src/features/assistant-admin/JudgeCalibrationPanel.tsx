import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { describeApiError } from '../../lib/errors'
import { getJudgeCalibration } from './api'
import type { AssistantCalibrationClause, AssistantCalibrationMismatch } from './api'

const verdictLabel = (verdict: string) => ({
  PASS: '通过', FAIL: '不通过', NA: '不适用', UNSURE: '无法判定',
}[verdict] ?? verdict)

const CLAUSE_COLUMNS: ColumnDef<AssistantCalibrationClause>[] = [
  { header: '条款', accessorKey: 'clause' },
  { header: '判什么', accessorKey: 'title' },
  { header: '比对', accessorKey: 'compared' },
  { header: '一致', accessorKey: 'agreed' },
  { header: '不一致', accessorKey: 'mismatched' },
  {
    header: '一致率',
    id: 'rate',
    cell: ({ row }) => (row.original.rate == null ? '—' : `${(row.original.rate * 100).toFixed(1)}%`),
  },
]

const MISMATCH_COLUMNS: ColumnDef<AssistantCalibrationMismatch>[] = [
  { header: '样本', accessorKey: 'caseId' },
  { header: '回答', accessorKey: 'messageId' },
  { header: '条款', accessorKey: 'clause' },
  {
    header: '人的判定',
    id: 'human',
    meta: { truncate: false },
    cell: ({ row }) => (
      <>
        <span className="fw-semibold">{verdictLabel(row.original.humanVerdict)}</span>
        {row.original.humanEvidence ? <div className="text-secondary small">{row.original.humanEvidence}</div> : null}
      </>
    ),
  },
  {
    header: '判官的判定',
    id: 'judge',
    meta: { truncate: false },
    cell: ({ row }) => (
      <>
        <span className="fw-semibold">{verdictLabel(row.original.judgeVerdict)}</span>
        {row.original.judgeEvidence ? <div className="text-secondary small">{row.original.judgeEvidence}</div> : null}
      </>
    ),
  },
  { header: '判官模型', accessorKey: 'judgeModel' },
  {
    header: '性质',
    id: 'kind',
    cell: ({ row }) => (row.original.dangerous
      ? <span className="text-danger fw-semibold">漏报</span>
      : <span className="text-secondary">不一致</span>),
  },
]

/**
 * 判官校准读数（ADR-044 §5 J2）。
 *
 * <p>
 * 它回答的是"这把尺子准不准"：**用户出的题**（3106 的 `judge` 层，一条真实回答 + 六条条款的人判定 + 引文）
 * 与**库内判官结论**按 `messageId` 对齐，逐条款比。与「回答质量」那四个数字分工不同——
 * 那边是判官自己的结论，这里是与人的比对。
 * </p>
 *
 * <p>
 * 「漏报」（人判不通过、判官判通过）用红色单独计：它会把错误答案留在库里当成没问题，
 * 因此比"误报"更值得盯。不一致逐条把两边的话都摆出来，改条款时才看得见人为什么这么判。
 * </p>
 */
export function JudgeCalibrationPanel() {
  const calibration = useQuery({
    queryKey: ['assistant-judge-calibration'],
    queryFn: () => getJudgeCalibration(),
  })

  const data = calibration.data
  const clauses = data?.clauses ?? []
  const mismatches = data?.mismatches ?? []
  const notes = data?.notes ?? []

  return (
    <section className="card erp-detail-card" aria-label="判官校准">
      <div className="card-header erp-card-toolbar">
        <span className="small fw-semibold">判官校准</span>
        <span className="text-secondary small ms-2">
          用户维护的样本 vs 判官结论 · 只统计已审阅样本 · 条款 {data?.rubricVersion ?? '—'}
        </span>
        <div className="ms-auto d-flex align-items-center gap-2 small">
          {data ? (
            <>
              <span className="text-secondary">
                样本 {data.samples} / 已审阅 {data.reviewed} / 可比对 {data.compared}
                {data.uncompared > 0 ? ` / 未比对 ${data.uncompared}` : ''}
              </span>
              <span className={data.dangerousCount > 0 ? 'text-danger fw-semibold' : 'fw-semibold'}>
                一致率 {data.agreementRate == null ? '—' : `${(data.agreementRate * 100).toFixed(1)}%`}
              </span>
            </>
          ) : null}
        </div>
      </div>

      {calibration.isPending ? <LoadingState label="正在比对校准样本…" /> : calibration.isError ? (
        <ErrorState message={describeApiError(calibration.error, '加载判官校准失败。')}
          onRetry={() => void calibration.refetch()} />
      ) : !data || data.samples === 0 ? (
        <EmptyState
          title="还没有校准样本"
          description="到 3106「评估用例」新建一条 LAYER=judge 的用例：一条真实回答 + 六条条款的人判定 + 引文，再回来这里看一致率。"
        />
      ) : (
        <>
          {data.dangerousCount > 0 ? (
            <div className="px-3 pt-3">
              <div className="alert alert-danger py-2 mb-0 small">
                有 {data.dangerousCount} 条漏报：人判「不通过」而判官判「通过」。
                这类错会把错误答案留在库里当成没问题——先改条款文本（或换判官模型），不要调采样率。
              </div>
            </div>
          ) : null}

          <div className="erp-master-table-region erp-quality-subtable">
            <ErpListGrid
              plain
              listId="assistant-judge-calibration-clauses"
              columns={CLAUSE_COLUMNS}
              rows={clauses}
              rowKey={(row) => row.clause}
              sorting={{ mode: 'client' }}
            />
          </div>

          {mismatches.length > 0 ? (
            <div className="erp-master-table-region erp-quality-subtable">
              <ErpListGrid
                plain
                listId="assistant-judge-calibration-mismatches"
                columns={MISMATCH_COLUMNS}
                rows={mismatches}
                rowKey={(row) => `${row.caseId}-${row.clause}`}
                sorting={{ mode: 'client' }}
              />
            </div>
          ) : null}
        </>
      )}

      {notes.length > 0 ? (
        <div className="px-3 pb-3 text-secondary small">
          {notes.map((note) => <div key={note}>· {note}</div>)}
        </div>
      ) : null}
    </section>
  )
}
