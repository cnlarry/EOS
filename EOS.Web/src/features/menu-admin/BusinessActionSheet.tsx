import {
  formatCondition,
  formatMatch,
  formatOpSentence,
  formatSourceTerms,
  parseJsonObject,
  withTargetTable,
  type BusinessNameLookup,
} from './businessActionText'
/**
 * 「加工单」视图：把一个业务动作的效果链渲染成按顺序的人话步骤
 * （`目标表.字段 运算 本单来源 @定位键 ?条件`），字段带中文名。
 * 它是阅读面——点步骤即选中，编辑仍走上方工具栏与弹窗，避免行内控件堆叠。
 */

export interface SheetOp {
  opSeq: number
  targetTable: string
  targetField: string
  opCode: string
  sourceScope: string
  sourceTable?: string | null
  sourceField?: string | null
  sourceAgg?: string | null
  sourceConstant?: string | null
  sourceTerms?: string | null
  match?: string | null
  condition?: string | null
  remark?: string | null
}

export interface SheetAction {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  condition?: string | null
  params?: string | null
  reverse?: string | null
  ops?: SheetOp[] | null
}

export function BusinessActionSheet({
  action,
  names,
  eventText,
  effectText,
  failModeText,
  reverseText,
  selectedOpSeq,
  onSelectOp,
}: {
  action: SheetAction
  names: BusinessNameLookup
  eventText: string
  effectText: string
  failModeText: string
  reverseText: string
  selectedOpSeq: number | null
  onSelectOp: (opSeq: number) => void
}) {
  const ops = [...(action.ops ?? [])].sort((a, b) => a.opSeq - b.opSeq)
  const actionCondition = formatCondition(action.condition, names)
  const params = parseJsonObject(action.params)
  return (
    <div className="card border">
      <div className="card-header py-2 px-3 d-flex flex-wrap align-items-center gap-2">
        <strong className="fs-6">{action.effectName?.trim() || effectText}</strong>
        <span className="badge text-bg-secondary">{eventText}</span>
        <span className="badge text-bg-light text-dark border">{effectText}</span>
        <span className="badge text-bg-light text-dark border">失败：{failModeText}</span>
        <span className="badge text-bg-light text-dark border">解批：{reverseText}</span>
        {!action.enabled ? <span className="badge text-bg-warning">已停用</span> : null}
        {actionCondition ? <span className="text-secondary small">整体条件：{actionCondition}</span> : null}
      </div>
      <div className="card-body py-2 px-3">
        {ops.length === 0 ? (
          <div className="text-secondary small">
            参数型效果，无字段级公式行。
            {params && Object.keys(params).length > 0 ? (
              <span className="ms-1">
                参数：
                {Object.entries(params).map(([key, value]) => (
                  <code className="ms-1" key={key} title={JSON.stringify(value)}>
                    {key}
                  </code>
                ))}
              </span>
            ) : null}
          </div>
        ) : (
          <ol className="list-group list-group-numbered">
            {ops.map((op) => {
              const active = selectedOpSeq === op.opSeq
              const match = formatMatch(op.match, names)
              const condition = formatCondition(op.condition, withTargetTable(names, op.targetTable))
              const terms = formatSourceTerms(op.sourceTerms)
              const comment = (op.remark ?? '').trim()
              return (
                <li
                  key={op.opSeq}
                  className={`list-group-item list-group-item-action py-2 px-3${active ? ' active' : ''}`}
                  onClick={() => onSelectOp(op.opSeq)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') onSelectOp(op.opSeq)
                  }}
                >
                  <div className="font-monospace">{formatOpSentence(op, names)}</div>
                  {match || condition || terms || comment ? (
                    <div className={`small mt-1${active ? '' : ' text-secondary'}`}>
                      {match ? <span className="me-3">{match}</span> : null}
                      {condition ? <span className="me-3">当 {condition}</span> : null}
                      {terms ? <span className="me-3">加减项：{terms}</span> : null}
                      {comment ? <span>说明：{comment}</span> : null}
                    </div>
                  ) : null}
                </li>
              )
            })}
          </ol>
        )}
      </div>
    </div>
  )
}
