import { useEffect, useState } from 'react'
import { Button } from '../../components/ui/Button'

/**
 * 2301「行为动作·演示」沙盒（仅前端原型）：
 * - 演示「模块加工单 = 事件 → 效果链」及字段级映射形态（A表.B字段 ← 本表.某字段）；
 * - 全部状态仅存于当前页面内存，不调用任何保存/后端接口；
 * - 内置收料单 1607 / 送货单 1406 / 生产入库单 1505 三份加工单示例（单据行为元模型草案附录 A/B/C）。
 */

export interface BehaviorDraftShape {
  M_IDX: number
  M_DESC: string
  MASTER_TABLE: string | null
  DETAIL_TABLE: string | null
  M_TAG: boolean
}

type DemoEventKey = 'approve' | 'deapprove' | 'save'

const DEMO_EVENTS: { key: DemoEventKey; label: string; hint: string }[] = [
  { key: 'approve', label: '批核生效', hint: '无流程直接批核 与 流程末步通过，同一触发点' },
  { key: 'deapprove', label: '解批', hint: '正向效果链自动反向（演示）' },
  { key: 'save', label: '保存后', hint: '保存成功点（新增/修改不拆）；示例未预置' },
]

type FieldOp = '累加' | '减' | '覆盖' | '取最大' | '取最小' | '去重追加' | '置为' | '清零'

interface FieldMapping {
  target: string
  op: FieldOp
  source: string
}

interface DemoEffectRow {
  id: number
  template: string
  condition?: string
  reverse: string
  locked?: boolean
  summary?: string
  mappings?: FieldMapping[]
  locate?: string
}

const EFFECT_CATALOG = [
  { key: 'accumulate', label: '量额累加回填', hint: '目标字段 = 目标字段 + 本单量/额（可顺带写日期/来源单号）' },
  { key: 'completion-close', label: '完成度自动结案 / 完工标记', hint: '累计量达到目标量后置结案或完工标志，可带抵减字段组' },
  { key: 'inventory-move', label: '库存移动', hint: '入库/出库：余额、均价、批次、流水、料件汇总' },
  { key: 'stamp-last-activity', label: '主档戳记', hint: '档案表的最近交易日期 = MAX(本单日期)' },
  { key: 'adjust-projection', label: '预计量调整', hint: 'MRP 计划量字段 = 计划量字段 − 本单数量' },
  { key: 'set-state', label: '状态 / 标志翻转', hint: '目标表状态列 = 指定值（含经办人/日期）' },
  { key: 'meta-link', label: '元数据联动', hint: '权限 / 字段字典 / 系统表联动' },
  { key: 'flow-trigger', label: '触发后续流程 / 作业', hint: '启动或推进另一单据流程 / 后台作业' },
  { key: 'legacy-sproc', label: '旧存储过程过渡', hint: '白名单内调用旧 SP（只减不增）' },
] as const

const opBadgeClass: Record<FieldOp, string> = {
  '累加': 'text-bg-primary',
  '减': 'text-bg-danger',
  '覆盖': 'text-bg-dark',
  '取最大': 'text-bg-success',
  '取最小': 'text-bg-success',
  '去重追加': 'text-bg-warning',
  '置为': 'text-bg-info',
  '清零': 'text-bg-secondary',
}

const noteRow = (summary: string): DemoEffectRow => ({
  id: 0,
  template: '固有约束 · 校验 / 守卫',
  summary,
  reverse: '不开放配置',
  locked: true,
})

const effectRow = (
  template: string,
  locate: string | undefined,
  reverse: string,
  mappings: FieldMapping[],
  condition?: string,
): DemoEffectRow => ({ id: 0, template, condition, reverse, mappings, locate })

const f = (target: string, op: FieldOp, source: string): FieldMapping => ({ target, op, source })

const PRESET_1607: DemoEffectRow[] = [
  noteRow('批核前置校验：收料数量不超过采购单可收数量；解批守卫'),
  effectRow(
    '量额累加回填',
    '按 本单明细 PURCHASE_TYPE / PURCHASE_NO / PURCHASE_SERIAL_NO 分组，定位采购单明细行',
    '解批：全部减回（日期不回退）',
    [
      f('PUR_PURCHASE_D.RECEIVE_QTY', '累加', '本单明细合计 QTY'),
      f('PUR_PURCHASE_D.RECEIVE_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
      f('PUR_PURCHASE_D.REAL_DELIVERY_DATE', '覆盖', '本单主表 RECEIVE_DATE'),
    ],
  ),
  effectRow(
    '量额累加回填',
    '按 本单明细 ORDER_TYPE / ORDER_NO / PRO_NO 定位客户订单收货汇总行',
    '解批：减回',
    [f('COP_ORDER_MORE.RECEIVE_QTY', '累加', '本单明细合计 QTY')],
    '仅明细带客户订单引用',
  ),
  effectRow(
    '主档戳记',
    'SUPPLIER.SUPPLIER_ID = 本单主表 SUPPLIER_ID',
    '不反向',
    [f('SUPPLIER.LAST_TRADE_DATE', '取最大', '本单主表 RECEIVE_DATE')],
  ),
  effectRow(
    '主档戳记',
    'PRODUCT.PRO_NO = 本单明细 PRO_NO',
    '不反向',
    [f('PRODUCT.LAST_TRADE_DATE', '取最大', '本单主表 RECEIVE_DATE')],
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '遍历本单引用的采购单行；全部满足才置主单',
    '不再满足时解除结案',
    [
      f('PUR_PURCHASE_D.FINISHED_TAG', '置为', '1（当 RECEIVE_QTY ≥ QTY 且 RECEIVE_SPARE_QTY ≥ SPARE_QTY）'),
      f('PUR_PURCHASE_M.FINISHED_TAG / FINISHED_PERSON / FINISHED_DATE', '置为', '1 / SYSTEM / 当前时间（所有行均结案时）'),
    ],
  ),
  effectRow(
    '库存移动',
    '按 料号 + 仓库 定位库存',
    '解批：反向流水（不删除）',
    [
      f('INV_PRO_DEPOT.QTY / COST_AMOUNT / COST_PRICE', '累加', '本单明细（QTY+SPARE_QTY 折库存单位）'),
      f('INV_BATCH_M.IN_SUM / INV_BATCH_D', '累加', '本单明细（批号管理料）'),
      f('INV_DEPOT_LOG', '置为', '写入入库流水（单据/料号/仓别/数量/成本）'),
      f('PRODUCT.QTY / AMOUNT / LAST_IN_DATE', '累加', '本单明细合计'),
    ],
  ),
  effectRow(
    '预计量调整',
    '按 本单明细 PRO_NO 汇总',
    '解批：加回',
    [f('PRODUCT.IN_BUY_QTY', '减', '本单明细合计（QTY+SPARE_QTY）')],
    'SYSSS.PRO_MRP=1',
  ),
]

const PRESET_1406: DemoEffectRow[] = [
  noteRow('批核前置校验（不超可送数量）；解批守卫按 SYSSS 开关检查订单 / 工单 / 备货是否已出完'),
  effectRow(
    '量额累加回填',
    '按 本单明细 ORDER_TYPE / ORDER_NO / ORDER_SERIAL_NO 分组，定位订单明细行',
    '解批：全部减回（日期不回退）',
    [
      f('COP_ORDER_D.FINISHED_SEND_QTY', '累加', '本单明细合计 QTY'),
      f('COP_ORDER_D.FINISHED_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
      f('COP_ORDER_D.FACT_SEND_DATE', '取最大', '本单主表 SEND_DATE'),
    ],
  ),
  effectRow(
    '量额累加回填',
    '按 本单明细 SHIPMENT_TYPE / SHIPMENT_NO / SHIPMENT_SERIAL_NO 定位排程明细行',
    '解批：全部减回',
    [
      f('COP_SHIPMENT_D.FINISHED_QTY', '累加', '本单明细合计 QTY'),
      f('COP_SHIPMENT_D.FACT_SEND_DATE', '取最大', '本单主表 SEND_DATE'),
      f('COP_SHIPMENT_D.SEND_TYPE / SEND_NO', '覆盖', '本单主表 SEND_TYPE / SEND_NO（来源单号回写）'),
    ],
  ),
  effectRow(
    '主档戳记',
    'CLIENT.CLIENT_ID = 本单主表 CLIENT_ID',
    '不反向',
    [f('CLIENT.LAST_TRADE_DATE', '取最大', '本单主表 SEND_DATE')],
  ),
  effectRow(
    '主档戳记',
    'PRODUCT.PRO_NO = 本单明细 PRO_NO',
    '不反向',
    [f('PRODUCT.LAST_TRADE_DATE', '取最大', '本单主表 SEND_DATE')],
  ),
  effectRow(
    '量额累加回填',
    '按 本单明细 PRODUCE_TYPE / PRODUCE_NO 分组，定位工单主表',
    '解批：减回（日期不回退）',
    [
      f('MOC_PRODUCE_M.FINISHED_SEND_QTY', '累加', '本单明细合计 QTY'),
      f('MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
      f('MOC_PRODUCE_M.FIRST_SEND_DATE', '取最小', '本单建立日期'),
      f('MOC_PRODUCE_M.FINISHED_SEND_DATE', '取最大', '本单建立日期'),
    ],
    'SYSSS.SEND_PRODUCE_TAG=1',
  ),
  effectRow(
    '量额累加回填',
    '按 本单明细 FITOUT_TYPE / FITOUT_NO / FITOUT_SERIAL_NO 定位备货明细行',
    '解批：减回',
    [
      f('COP_FITOUT_D.FINISHED_QTY', '累加', '本单明细合计 QTY'),
      f('COP_FITOUT_D.FINISHED_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
    ],
    'SYSSS.SEND_FITOUT_TAG=1',
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '遍历本单引用的订单行；全部满足才置主单',
    '不再满足时解除结案',
    [
      f('COP_ORDER_D.FINISHED_TAG', '置为', '1（当 QTY ≤ FINISHED_SEND_QTY + BACK_MATERIAL + BACK_BAD 且 SPARE_QTY ≤ FINISHED_SPARE_QTY）'),
      f('COP_ORDER_M.FINISHED_TAG / FINISHED_PERSON / FINISHED_DATE', '置为', '1 / SYSTEM / 当前时间（所有行均结案时）'),
    ],
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '遍历本单引用的排程行',
    '不再满足时解除结案',
    [f('COP_SHIPMENT_D.FINISHED_TAG / COP_SHIPMENT_M.FINISHED_TAG', '置为', '1 / 1（按各自完成度判定）')],
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '遍历本单引用的备货行',
    '不再满足时解除结案',
    [f('COP_FITOUT_D.FINISHED_TAG / COP_FITOUT_M.FINISHED_TAG', '置为', '1 / 1（按各自完成度判定）')],
  ),
  effectRow(
    '库存移动',
    '按 料号 + 仓库 定位库存',
    '解批：反向流水（不删除）',
    [
      f('INV_PRO_DEPOT.QTY / COST_AMOUNT / COST_PRICE', '减', '本单明细（QTY+SPARE_QTY 折库存单位）'),
      f('INV_BATCH_M.OUT_SUM / INV_BATCH_D', '累加', '本单明细（批号管理料）'),
      f('INV_DEPOT_LOG', '置为', '写入出库流水（单据/料号/仓别/数量/成本）'),
      f('PRODUCT.QTY / AMOUNT / LAST_OUT_DATE', '减', '本单明细合计'),
    ],
    'SYSSS.SEND_TAG=1',
  ),
  effectRow(
    '预计量调整',
    '按 本单明细 PRO_NO 汇总（仅 ORDER_NO 非空行）',
    '解批：加回',
    [f('PRODUCT.NOT_SEND_QTY', '减', '本单明细合计（QTY+SPARE_QTY）')],
    'SYSSS.PRO_MRP=1',
  ),
]

const PRESET_1505: DemoEffectRow[] = [
  noteRow('批核前置校验（入库不超制令 / 订单）；解批守卫：解批后入库量不得小于已备货量'),
  effectRow(
    '量额累加回填',
    '按 本单明细 PRODUCE_TYPE / PRODUCE_NO 分组，定位制令主表',
    '解批：减回（日期 / 仓区位不回退）',
    [
      f('MOC_PRODUCE_M.FINISHED_QTY', '累加', '本单明细合计 QTY'),
      f('MOC_PRODUCE_M.FINISHED_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
      f('MOC_PRODUCE_M.FINISHED_IN_DATE', '覆盖', '本单主表 CREATE_DATE（待确认是否用 PRODUCT_IN_DATE）'),
      f('MOC_PRODUCE_M.DEPOT_PLACE', '去重追加', '本单明细 DEPOT_PLACE（不存在才追加）'),
    ],
  ),
  effectRow(
    '量额累加回填',
    '按 本单明细 ORDER_TYPE / ORDER_NO / ORDER_SERIAL_NO 分组，定位订单明细行',
    '解批：减回',
    [
      f('COP_ORDER_D.FINISHED_PRODUCE_QTY', '累加', '本单明细合计 QTY'),
      f('COP_ORDER_D.FINISHED_PRODUCE_SPARE_QTY', '累加', '本单明细合计 SPARE_QTY'),
    ],
    'SYSSS.PRODUCE_IN_ORDER_TAG=1',
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '定位制令主表；按数量是否收满判定',
    '解批：直接清零 END_TAG / INFACT_END（旧逻辑，待业务确认）',
    [
      f('MOC_PRODUCE_M.END_TAG', '置为', '1（当 FINISHED_QTY ≥ QTY 且 FINISHED_SPARE_QTY ≥ SPARE_QTY）'),
      f('MOC_PRODUCE_M.INFACT_END', '覆盖', '本单主表 PRODUCT_IN_DATE'),
    ],
  ),
  effectRow(
    '完成度自动结案 / 完工标记',
    '遍历本单引用的制令；全部满足才置主单',
    '不再满足时解除结案',
    [f('MOC_PRODUCE_M.FINISHED_TAG / FINISHED_PERSON / FINISHED_DATE', '置为', '1 / SYSTEM / 当前时间')],
  ),
  effectRow(
    '预计量调整',
    '按 本单明细 PRO_NO 汇总（仅 PRODUCE_NO 非空行）',
    '解批：加回',
    [f('PRODUCT.NOT_IN_QTY', '减', '本单明细合计（QTY+SPARE_QTY）')],
    'SYSSS.PRO_MRP=1',
  ),
  effectRow(
    '库存移动',
    '按 料号 + 仓库 定位库存',
    '解批：反向流水（不删除）',
    [
      f('INV_PRO_DEPOT.QTY / COST_AMOUNT / COST_PRICE', '累加', '本单明细（QTY+SPARE_QTY 折库存单位）'),
      f('INV_BATCH_M.IN_SUM / INV_BATCH_D', '累加', '本单明细（批号管理料）'),
      f('INV_DEPOT_LOG', '置为', '写入入库流水（单据/料号/仓别/数量/成本）'),
      f('PRODUCT.QTY / AMOUNT / LAST_IN_DATE', '累加', '本单明细合计'),
    ],
  ),
]

const PRESETS: Record<number, DemoEffectRow[]> = {
  1607: PRESET_1607,
  1406: PRESET_1406,
  1505: PRESET_1505,
}

const withIds = (rows: DemoEffectRow[]) => rows.map((item, index) => ({ ...item, id: index + 1 }))

const defaultMappings = (templateKey: string): FieldMapping[] => {
  switch (templateKey) {
    case 'accumulate':
      return [f('目标表.累计字段', '累加', '本表明细.数量字段（按单据关系分组）')]
    case 'completion-close':
      return [f('目标表.FINISHED_TAG / END_TAG', '置为', '1（当 累计量 ≥ 目标量）')]
    case 'inventory-move':
      return [f('INV_PRO_DEPOT.QTY / INV_DEPOT_LOG', '累加', '本表明细（QTY+SPARE_QTY 折库存单位）')]
    case 'stamp-last-activity':
      return [f('档案表.LAST_TRADE_DATE', '取最大', '本表主表日期字段')]
    case 'adjust-projection':
      return [f('PRODUCT.计划量字段', '减', '本表明细合计数量')]
    case 'set-state':
      return [f('目标表.状态列', '置为', '目标状态值')]
    default:
      return [f('（该模板参数待 Phase B 定义）', '置为', '演示占位')]
  }
}

const defaultLocate = (templateKey: string): string | undefined => {
  switch (templateKey) {
    case 'accumulate':
    case 'completion-close':
      return '按 已登记单据关系（FIELD_RELATION）定位目标行（演示占位）'
    case 'inventory-move':
      return '按 料号 + 仓库 定位库存'
    case 'stamp-last-activity':
    case 'adjust-projection':
      return '按 本表关联的主档键定位'
    default:
      return undefined
  }
}

function lintIssues(rows: DemoEffectRow[]): string[] {
  const issues: string[] = []
  const firstCompletion = rows.findIndex((item) => item.template.includes('完成度自动结案'))
  const anyAccumulateBefore = rows.slice(0, Math.max(firstCompletion, 0)).some((item) => item.template.includes('量额累加回填'))
  if (firstCompletion >= 0 && !anyAccumulateBefore) {
    issues.push('顺序 lint：自动结案应排在量额累加回写之后（它依赖回写后的累计量）')
  }
  return issues
}

function MappingLine({ mapping }: { mapping: FieldMapping }) {
  return (
    <div className="d-flex align-items-center gap-2 small flex-wrap">
      <code className="erp-effect-target">{mapping.target}</code>
      <span className={`badge ${opBadgeClass[mapping.op]}`}>{mapping.op}</span>
      <span className="text-secondary">←</span>
      <span className="text-body-secondary">{mapping.source}</span>
    </div>
  )
}

function ChainRow({ item, seq, index, total, onMove, onRemove }: {
  item: DemoEffectRow
  seq: number
  index: number
  total: number
  onMove: (index: number, direction: -1 | 1) => void
  onRemove: (index: number) => void
}) {
  return (
    <div className={`card mb-1 ${item.locked ? 'erp-menu-group-card' : ''}`} style={{ opacity: item.locked ? 0.85 : 1 }}>
      <div className="card-body py-2 px-3 d-flex align-items-start gap-3">
        <div className="text-secondary fw-bold mt-1">{item.locked ? '0' : String(seq)}</div>
        <div className="flex-grow-1">
          <div className="d-flex align-items-center gap-2 flex-wrap mb-1">
            <span className="badge text-bg-secondary">{item.template}</span>
            {item.condition && <span className="badge text-bg-light border">{item.condition}</span>}
            <span className="badge text-bg-light border">反向：{item.reverse}</span>
          </div>
          {item.locked ? (
            <div className="small">{item.summary}</div>
          ) : (
            <>
              {item.mappings?.map((mapping, i) => <MappingLine key={i} mapping={mapping} />)}
              {item.locate && <div className="small text-secondary mt-1">定位：{item.locate}</div>}
            </>
          )}
        </div>
        {!item.locked && (
          <div className="d-flex flex-column gap-1">
            <Button size="sm" variant="ghost" disabled={index <= 1} title="上移" onClick={() => onMove(index, -1)}>↑</Button>
            <Button size="sm" variant="ghost" disabled={index >= total - 1} title="下移" onClick={() => onMove(index, 1)}>↓</Button>
            <Button size="sm" variant="ghost" title="移除" onClick={() => onRemove(index)}>×</Button>
          </div>
        )}
      </div>
    </div>
  )
}

export function BehaviorActionsDemoPanel({ draft }: { draft: BehaviorDraftShape }) {
  const [event, setEvent] = useState<DemoEventKey>('approve')
  const [rows, setRows] = useState<DemoEffectRow[]>(() => withIds(PRESETS[draft.M_IDX] ?? []))
  const [selectedTemplate, setSelectedTemplate] = useState<string>(EFFECT_CATALOG[0].key)

  useEffect(() => {
    const preset = PRESETS[draft.M_IDX]
    setRows(preset ? withIds(preset) : [])
  }, [draft.M_IDX])

  const noTables = !draft.MASTER_TABLE && !draft.DETAIL_TABLE
  const masterOnly = Boolean(draft.MASTER_TABLE) && !draft.DETAIL_TABLE
  const issues = lintIssues(rows)

  const addRow = () => {
    const template = EFFECT_CATALOG.find((item) => item.key === selectedTemplate)
    if (!template) return
    setRows((current) => [
      ...current,
      {
        id: Math.max(0, ...current.map((item) => item.id)) + 1,
        template: template.label,
        condition: undefined,
        reverse: '解批：按模板反向语义',
        mappings: defaultMappings(template.key),
        locate: defaultLocate(template.key),
      },
    ])
  }

  const moveRow = (index: number, direction: -1 | 1) => {
    setRows((current) => {
      const next = [...current]
      const target = index + direction
      if (target <= 0 || target >= next.length) return current
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
  }

  const removeRow = (index: number) => {
    setRows((current) => current.filter((_, i) => i !== index))
  }

  const loadPreset = (moduleId: number) => {
    setRows(withIds(PRESETS[moduleId] ?? []))
  }

  return (
    <div className="erp-menu-behavior-demo">
      <div className="alert alert-info py-2 mb-2 d-flex justify-content-between align-items-center flex-wrap gap-2">
        <span>演示模式：改动仅存于本页面内存，<strong>不保存</strong>、不调用任何后端接口。</span>
        <span className="d-flex gap-1">
          <Button size="sm" variant="secondary" onClick={() => loadPreset(1607)}>载入收料单 1607</Button>
          <Button size="sm" variant="secondary" onClick={() => loadPreset(1406)}>载入送货单 1406</Button>
          <Button size="sm" variant="secondary" onClick={() => loadPreset(1505)}>载入生产入库单 1505</Button>
        </span>
      </div>

      {noTables ? (
        <div className="alert alert-secondary py-2 mb-2 small">两表皆空：本模块不参与行为规则（菜单组 / 特殊页）。下方可载入示例仅用于预览界面。</div>
      ) : masterOnly ? (
        <div className="alert alert-warning py-2 mb-2 small">仅主表（无明细）：仍可配保存/批核效果；按明细行聚合的模板（如明细累加回写、按明细判定完成度）不适用。</div>
      ) : (
        <div className="alert alert-success py-2 mb-2 small">主表 + 明细表：完整参与，全部效果模板可用。</div>
      )}

      <div className="mb-2">
        <div className="text-secondary small fw-semibold mb-1">触发事件</div>
        <div className="d-flex gap-2 flex-wrap">
          {DEMO_EVENTS.map((item) => (
            <Button
              key={item.key}
              size="sm"
              variant={event === item.key ? undefined : 'ghost'}
              title={item.hint}
              onClick={() => setEvent(item.key)}
            >
              {item.label}
            </Button>
          ))}
        </div>
        <div className="text-secondary small mt-1">{DEMO_EVENTS.find((item) => item.key === event)?.hint}</div>
      </div>

      <div className="d-flex align-items-center gap-2 mb-2 flex-wrap">
        <label className="small mb-0 text-secondary fw-semibold" htmlFor="effect-template-select">添加效果（从已注册目录选择）</label>
        <select
          id="effect-template-select"
          className="form-select form-select-sm w-auto"
          value={selectedTemplate}
          onChange={(e) => setSelectedTemplate(e.target.value)}
        >
          {EFFECT_CATALOG.map((item) => (
            <option key={item.key} value={item.key}>{item.label}</option>
          ))}
        </select>
        <Button size="sm" onClick={addRow} disabled={noTables}>添加到链尾</Button>
        <Button size="sm" variant="ghost" onClick={() => setRows([])} disabled={noTables || rows.length === 0}>清空（演示）</Button>
      </div>

      {issues.length > 0 && (
        <div className="alert alert-danger py-2 mb-2 small">
          {issues.map((issue) => <div key={issue}>{issue}</div>)}
        </div>
      )}

      {rows.length === 0 ? (
        <div className="text-secondary text-center py-4 border rounded">该事件暂无效果链。左侧选择 1607 / 1406 / 1505 可查看示例加工单。</div>
      ) : (
        <div>
          <div className="text-secondary small fw-semibold mb-1">效果链（顺序即执行顺序）</div>
          {rows.map((item, index) => {
            const seq = rows.slice(0, index + 1).filter((row) => !row.locked).length
            return (
              <ChainRow
                key={item.id}
                item={item}
                seq={seq}
                index={index}
                total={rows.length}
                onMove={moveRow}
                onRemove={removeRow}
              />
            )
          })}
        </div>
      )}
    </div>
  )
}
