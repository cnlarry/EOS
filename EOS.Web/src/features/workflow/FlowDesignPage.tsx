import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconPlus,
  IconRefresh,
  IconTrash,
  IconUsers,
} from '@tabler/icons-react'
import type { ColumnDef } from '@tanstack/react-table'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { describeApiError } from '../../lib/errors'

interface FlowSummary {
  moduleId: number
  title: string
  flowName: string
  remark: string
  stepCount: number
  updatedBy: string
  updatedAt: string | null
}

interface EligibleModule {
  moduleId: number
  title: string
  masterTable: string
  autoApprove: boolean
}

interface FlowStep {
  sortNo: string
  desc: string
  people: string[]
  execCondition: string
  personConditions: string[]
  approvePowers: string[]
  forwardPowers: string[]
  autoExecCondition: string
  isAutoExec: boolean
  isSign: boolean
  passPercent: number
  isEffect: boolean
  preMustUnder: boolean
  canSirAgency: boolean
  mustSigners: string[]
  remark: string
}

interface FlowDetail {
  moduleId: number
  title: string
  masterTable: string
  flowName: string
  remark: string
  updatedBy: string
  updatedAt: string | null
  steps: FlowStep[]
}

interface Person {
  userId: string
  name: string
  deptId: string
  active: boolean
}

const emptyStep = (sortNo: string): FlowStep => ({
  sortNo,
  desc: '',
  people: [],
  execCondition: '',
  personConditions: [],
  approvePowers: [],
  forwardPowers: [],
  autoExecCondition: '',
  isAutoExec: false,
  isSign: false,
  passPercent: 0,
  isEffect: true,
  preMustUnder: false,
  canSirAgency: false,
  mustSigners: [],
  remark: '',
})

/** 人员多选（设计器审批人/必签/权限串通用）：搜索即拉取，点选切换，chip 展示已选。 */
function PersonMultiSelect({ value, onChange, placeholder }: { value: string[]; onChange: (next: string[]) => void; placeholder: string }) {
  const [keyword, setKeyword] = useState('')
  const [open, setOpen] = useState(false)
  const peopleQuery = useQuery({
    queryKey: ['flow-people', keyword.trim()],
    queryFn: () => apiClient.get<{ people: Person[] }>(`/workflow/definitions/people?keyword=${encodeURIComponent(keyword.trim())}`),
    enabled: open,
  })
  const results = peopleQuery.data?.people ?? []
  const labelMap = new Map<string, string>()
  for (const person of results) labelMap.set(person.userId, person.name)

  const toggle = (userId: string) => {
    onChange(value.includes(userId) ? value.filter((item) => item !== userId) : [...value, userId])
  }

  return (
    <div className="position-relative">
      <div className="d-flex flex-wrap gap-1 mb-1">
        {value.map((userId) => (
          <span key={userId} className="badge text-bg-light border d-inline-flex align-items-center gap-1">
            {userId}
            {labelMap.has(userId) && <span className="text-secondary">（{labelMap.get(userId)}）</span>}
            <button
              type="button"
              className="btn-close btn-close-xs"
              aria-label={`移除 ${userId}`}
              onClick={() => onChange(value.filter((item) => item !== userId))}
            />
          </span>
        ))}
        {value.length === 0 && <span className="small text-secondary">未选择</span>}
      </div>
      <div className="input-group input-group-sm">
        <span className="input-group-text"><IconUsers size={14} /></span>
        <input
          className="form-control form-control-sm"
          value={keyword}
          onChange={(event) => { setKeyword(event.target.value); setOpen(true) }}
          onFocus={() => setOpen(true)}
          placeholder={placeholder}
          aria-label={placeholder}
        />
        <button type="button" className="btn btn-outline-secondary btn-sm" aria-label="收起" onClick={() => setOpen((value) => !value)}>
          <IconChevronDown size={14} />
        </button>
      </div>
      {open && (
        <div className="position-absolute start-0 end-0 z-3 bg-white border rounded-2 shadow-sm mt-1" style={{ maxHeight: 200, overflowY: 'auto' }}>
          {peopleQuery.isPending ? (
            <div className="p-2 small text-secondary">加载中…</div>
          ) : peopleQuery.isError ? (
            <div className="p-2 small text-danger">人员加载失败</div>
          ) : results.length === 0 ? (
            <div className="p-2 small text-secondary">无匹配用户</div>
          ) : (
            results.map((person) => (
              <label key={person.userId} className="d-flex align-items-center gap-2 px-2 py-1 small user-select-none" style={{ cursor: 'pointer' }}>
                <input type="checkbox" checked={value.includes(person.userId)} onChange={() => toggle(person.userId)} />
                <span className="font-monospace">{person.userId}</span>
                <span className="text-secondary">（{person.name || '—'}）</span>
                {person.deptId && <span className="ms-auto text-secondary">{person.deptId}</span>}
              </label>
            ))
          )}
        </div>
      )}
    </div>
  )
}

function StepCard({
  step,
  index,
  total,
  onUpdate,
  onRemove,
  onMove,
}: {
  step: FlowStep
  index: number
  total: number
  onUpdate: (next: FlowStep) => void
  onRemove: () => void
  onMove: (direction: -1 | 1) => void
}) {
  const set = <K extends keyof FlowStep>(key: K, value: FlowStep[K]) => onUpdate({ ...step, [key]: value })
  return (
    <div className="card mb-2">
      <div className="card-header py-2 d-flex align-items-center gap-2">
        <span className="fw-semibold small">步骤 {index + 1}</span>
        <input
          className="form-control form-control-sm font-monospace"
          style={{ width: 90 }}
          value={step.sortNo}
          onChange={(event) => set('sortNo', event.target.value)}
          placeholder="序号"
          aria-label="步骤序号"
        />
        <input
          className="form-control form-control-sm flex-grow-1"
          value={step.desc}
          onChange={(event) => set('desc', event.target.value)}
          placeholder="步骤名称（如：一级审批）"
          aria-label="步骤名称"
        />
        <div className="btn-group btn-group-sm">
          <button type="button" className="btn btn-outline-secondary" aria-label="上移" disabled={index === 0} onClick={() => onMove(-1)}>
            <IconArrowUp size={14} />
          </button>
          <button type="button" className="btn btn-outline-secondary" aria-label="下移" disabled={index === total - 1} onClick={() => onMove(1)}>
            <IconArrowDown size={14} />
          </button>
        </div>
        <button type="button" className="btn btn-sm btn-outline-danger" aria-label="删除步骤" onClick={onRemove}>
          <IconTrash size={14} />
        </button>
      </div>
      <div className="card-body py-2">
        <div className="row g-2">
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">审批人（分号分隔，可多选）</label>
            <PersonMultiSelect value={step.people} onChange={(people) => set('people', people)} placeholder="输入用户/姓名搜索…" />
          </div>
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">审批权（留空=全部有审批权）</label>
            <PersonMultiSelect value={step.approvePowers} onChange={(approvePowers) => set('approvePowers', approvePowers)} placeholder="输入用户/姓名搜索…" />
          </div>
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">跳转/解批权（留空=全部有权限）</label>
            <PersonMultiSelect value={step.forwardPowers} onChange={(forwardPowers) => set('forwardPowers', forwardPowers)} placeholder="输入用户/姓名搜索…" />
          </div>
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">步骤执行条件（EXEC_CONDITION，留空=无条件）</label>
            <textarea
              className="form-control form-control-sm"
              rows={2}
              value={step.execCondition}
              onChange={(event) => set('execCondition', event.target.value)}
              placeholder="如：AMOUNT>1000"
            />
          </div>
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">审批人条件（与审批人一一对应，分号分隔）</label>
            <textarea
              className="form-control form-control-sm"
              rows={2}
              value={step.personConditions.join(';')}
              onChange={(event) => set('personConditions', event.target.value.split(';'))}
              placeholder="每审批人一个条件，用 ; 分隔"
            />
          </div>
          <div className="col-12 col-md-6">
            <label className="form-label small mb-1">自动执行条件（IS_AUTO_EXEC 开启时生效）</label>
            <textarea
              className="form-control form-control-sm"
              rows={2}
              value={step.autoExecCondition}
              onChange={(event) => set('autoExecCondition', event.target.value)}
              placeholder="留空=无条件自动执行"
            />
          </div>
          <div className="col-12">
            <div className="d-flex flex-wrap gap-3 align-items-center">
              <label className="form-check form-switch mb-0">
                <input className="form-check-input" type="checkbox" checked={step.isAutoExec} onChange={(event) => set('isAutoExec', event.target.checked)} />
                <span className="form-check-label small">自动执行</span>
              </label>
              <label className="form-check form-switch mb-0">
                <input className="form-check-input" type="checkbox" checked={step.isSign} onChange={(event) => set('isSign', event.target.checked)} />
                <span className="form-check-label small">会签</span>
              </label>
              <label className="d-flex align-items-center gap-1 small mb-0">
                通过阈值
                <input
                  type="number"
                  className="form-control form-control-sm"
                  style={{ width: 80 }}
                  min={0}
                  max={100}
                  value={step.passPercent}
                  onChange={(event) => set('passPercent', Number(event.target.value) || 0)}
                  aria-label="会签通过阈值"
                />
              </label>
              <label className="form-check form-switch mb-0">
                <input className="form-check-input" type="checkbox" checked={step.isEffect} onChange={(event) => set('isEffect', event.target.checked)} />
                <span className="form-check-label small">生效</span>
              </label>
              <label className="form-check form-switch mb-0">
                <input className="form-check-input" type="checkbox" checked={step.preMustUnder} onChange={(event) => set('preMustUnder', event.target.checked)} />
                <span className="form-check-label small">前置必须处理</span>
              </label>
              <label className="form-check form-switch mb-0">
                <input className="form-check-input" type="checkbox" checked={step.canSirAgency} onChange={(event) => set('canSirAgency', event.target.checked)} />
                <span className="form-check-label small">允许代理</span>
              </label>
            </div>
          </div>
          {step.isSign && (
            <div className="col-12 col-md-6">
              <label className="form-label small mb-1">必签人（未到阈值也须全部通过）</label>
              <PersonMultiSelect value={step.mustSigners} onChange={(mustSigners) => set('mustSigners', mustSigners)} placeholder="输入用户/姓名搜索…" />
            </div>
          )}
          <div className="col-12">
            <label className="form-label small mb-1">备注</label>
            <input className="form-control form-control-sm" value={step.remark} onChange={(event) => set('remark', event.target.value)} />
          </div>
        </div>
      </div>
    </div>
  )
}

/** 流程设计器（2101 表单流程设计）：维护各单据模块的审批流程定义。 */
export function FlowDesignPage() {
  const queryClient = useQueryClient()
  const list = useQuery({
    queryKey: ['flow-definitions'],
    queryFn: () => apiClient.get<{ flows: FlowSummary[]; eligible: EligibleModule[] }>('/workflow/definitions'),
  })
  const [selectedModuleId, setSelectedModuleId] = useState<number | null>(null)
  const [keyword, setKeyword] = useState('')

  const detail = useQuery({
    queryKey: ['flow-definition', selectedModuleId],
    queryFn: () => apiClient.get<FlowDetail>(`/workflow/definitions/${selectedModuleId}`),
    enabled: selectedModuleId != null,
    retry: false,
  })

  const [form, setForm] = useState<{ flowName: string; remark: string; steps: FlowStep[] }>({ flowName: '', remark: '', steps: [] })

  useEffect(() => {
    if (selectedModuleId == null) return
    if (detail.isPending || detail.isError) return
    const data = detail.data
    setForm({
      flowName: data.flowName,
      remark: data.remark,
      steps: data.steps.map((step) => ({ ...step })),
    })
  }, [detail.data, detail.isPending, detail.isError, selectedModuleId])

  const save = useMutation({
    mutationFn: async () => {
      if (selectedModuleId == null) throw new Error('请先选择模块。')
      return apiClient.post(`/workflow/definitions/${selectedModuleId}`, {
        flowName: form.flowName,
        remark: form.remark,
        steps: form.steps,
      })
    },
    onSuccess: async () => {
      window.alert('流程定义已保存。')
      await queryClient.invalidateQueries({ queryKey: ['flow-definitions'] })
      await queryClient.invalidateQueries({ queryKey: ['flow-definition', selectedModuleId] })
    },
    onError: (cause) => {
      const body = cause instanceof ApiError ? cause.body : undefined
      window.alert(body?.message ?? '保存失败，请稍后重试。')
    },
  })

  const removeFlow = useMutation({
    mutationFn: async () => {
      if (selectedModuleId == null) throw new Error('请先选择模块。')
      return apiClient.delete(`/workflow/definitions/${selectedModuleId}`)
    },
    onSuccess: async () => {
      window.alert('流程定义已删除。')
      setSelectedModuleId(null)
      setForm({ flowName: '', remark: '', steps: [] })
      await queryClient.invalidateQueries({ queryKey: ['flow-definitions'] })
    },
    onError: (cause) => {
      const body = cause instanceof ApiError ? cause.body : undefined
      window.alert(body?.message ?? '删除失败，请稍后重试。')
    },
  })

  const flows = list.data?.flows ?? []
  const eligible = list.data?.eligible ?? []
  const configuredSet = new Set(flows.map((flow) => flow.moduleId))
  const moduleRows = eligible.map((module) => {
    const flow = flows.find((item) => item.moduleId === module.moduleId)
    return {
      moduleId: module.moduleId,
      title: module.title,
      masterTable: module.masterTable,

      flowName: flow?.flowName ?? '',
      stepCount: flow?.stepCount ?? 0,
      configured: configuredSet.has(module.moduleId),
    }
  }).filter((row) => {
    const kw = keyword.trim().toLowerCase()
    return kw.length === 0 || row.moduleId.toString().includes(kw) || row.title.toLowerCase().includes(kw) || row.flowName.toLowerCase().includes(kw)
  })

  const selectedConfigured = selectedModuleId != null && configuredSet.has(selectedModuleId)
  const selectedTitle = eligible.find((module) => module.moduleId === selectedModuleId)?.title ?? detail.data?.title ?? ''

  const addStep = () => {
    const maxNo = form.steps.reduce((max, step) => {
      const parsed = Number(step.sortNo)
      return Number.isFinite(parsed) && parsed > max ? parsed : max
    }, 0)
    const sortNo = String(maxNo + 1).padStart(3, '0')
    setForm((current) => ({ ...current, steps: [...current.steps, emptyStep(sortNo)] }))
  }

  const updateStep = (index: number, next: FlowStep) => {
    setForm((current) => ({ ...current, steps: current.steps.map((step, i) => (i === index ? next : step)) }))
  }

  const moveStep = (index: number, direction: -1 | 1) => {
    setForm((current) => {
      const steps = [...current.steps]
      const target = index + direction
      if (target < 0 || target >= steps.length) return current
      ;[steps[index], steps[target]] = [steps[target], steps[index]]
      return { ...current, steps }
    })
  }

  const removeStep = (index: number) => {
    setForm((current) => ({ ...current, steps: current.steps.filter((_, i) => i !== index) }))
  }

  const columns: ColumnDef<(typeof moduleRows)[number], unknown>[] = [
    { accessorKey: 'moduleId', header: '模块号', cell: (info) => <span className="font-monospace">{String(info.getValue())}</span> },
    { accessorKey: 'title', header: '模块标题', cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'flowName', header: '流程名', cell: (info) => { const value = String(info.getValue() ?? ''); return value ? <span>{value}</span> : <span className="text-secondary">未配置</span> } },
    { accessorKey: 'stepCount', header: '步骤数', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? 0)}</span> },
  ]

  const errorMessage = describeApiError(list.error, '加载失败，请稍后重试。')

  return (
    <div className="d-flex flex-column erp-full-list-page" style={{ height: 'calc(100dvh - 96px)' }}>
      <div className="d-flex gap-2 flex-grow-1" style={{ minHeight: 0 }}>
        <div className="card d-flex flex-column" style={{ width: 460 }}>
          <div className="card-header py-2 d-flex gap-2 align-items-center">
            <input
              className="form-control form-control-sm"
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
              placeholder="搜索模块号/标题/流程名…"
              aria-label="搜索模块"
            />
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void list.refetch()}>刷新</Button>
          </div>
          <div className="card-body p-0 overflow-auto" style={{ flex: 1, minHeight: 0 }}>
            {list.isPending ? <LoadingState label="正在加载模块…" /> : list.isError ? (
              <ErrorState message={errorMessage} onRetry={() => void list.refetch()} />
            ) : (
              <ErpTable
                columns={columns}
                data={moduleRows}
                getRowId={(row) => String(row.moduleId)}
                clientSideSorting
                resizable
                storageKey="flow-design-modules"
                rowClickSingleSelect
                empty={<div className="text-center text-secondary py-4">无具备批核能力的模块</div>}
                onRowClick={(row) => setSelectedModuleId(Number(row.moduleId))}
              />
            )}
          </div>
        </div>
        <div className="card d-flex flex-column flex-grow-1" style={{ minWidth: 0 }}>
          {selectedModuleId == null ? (
            <div className="d-flex align-items-center justify-content-center text-secondary flex-grow-1">← 从左侧选择模块配置审批流程</div>
          ) : (
            <>
              <div className="card-header py-2 d-flex gap-2 align-items-center">
                <span className="fw-semibold">{selectedTitle}（{selectedModuleId}）</span>
                <span className={`badge ${selectedConfigured ? 'text-bg-success' : 'text-bg-secondary'}`}>{selectedConfigured ? '已配置流程' : '未配置'}</span>
                <div className="ms-auto d-flex gap-2">
                  <Button size="sm" icon={<IconPlus size={16} />} onClick={addStep}>添加步骤</Button>
                  <Button size="sm" variant="primary" loading={save.isPending} onClick={() => save.mutate()}>保存流程</Button>
                  {selectedConfigured && (
                    <Button size="sm" variant="danger" loading={removeFlow.isPending} onClick={() => { if (window.confirm(`确认删除「${selectedTitle}」的流程定义？`)) removeFlow.mutate() }}>删除</Button>
                  )}
                </div>
              </div>
              <div className="card-body overflow-auto p-2" style={{ flex: 1, minHeight: 0 }}>
                <div className="row g-2 mb-2">
                  <div className="col-12 col-md-6">
                    <label className="form-label small mb-1">流程名称</label>
                    <input className="form-control form-control-sm" value={form.flowName} onChange={(event) => setForm((current) => ({ ...current, flowName: event.target.value }))} placeholder="如：油卡充值单二级审批" />
                  </div>
                  <div className="col-12 col-md-6">
                    <label className="form-label small mb-1">备注（REMARK，幂等/审计标记）</label>
                    <input className="form-control form-control-sm" value={form.remark} onChange={(event) => setForm((current) => ({ ...current, remark: event.target.value }))} placeholder="留空即可" />
                  </div>
                </div>
                <div className="small text-secondary mb-2">
                  审批步骤按序号升序执行；保存将整体替换当前流程定义。执行条件/审批人条件需与主表字段匹配，保存前会做安全校验。
                </div>
                {form.steps.length === 0 ? (
                  <div className="text-center text-secondary py-4 border rounded">尚未添加审批步骤</div>
                ) : (
                  form.steps.map((step, index) => (
                    <StepCard
                      key={`${step.sortNo}-${index}`}
                      step={step}
                      index={index}
                      total={form.steps.length}
                      onUpdate={(next) => updateStep(index, next)}
                      onRemove={() => removeStep(index)}
                      onMove={(direction) => moveStep(index, direction)}
                    />
                  ))
                )}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
