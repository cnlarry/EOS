import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { setConfigTarget } from '../assistant/situationSource'
import {
  IconBook,
  IconDeviceFloppy,
  IconPlayerPlay,
  IconRefresh,
} from '@tabler/icons-react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { ColumnPickerInput, MatchEditor, TablePickerInput } from './BusinessActionPickers'
import { BehaviorHandbook } from './BehaviorHandbook'
import { BusinessActionImpact } from './BusinessActionImpactPanel'
import { CloneActionsModal } from './CloneActionsModal'
import { EffectSimulationPanel } from './EffectSimulationPanel'
import { ConditionEditor } from './ConditionEditor'
import { DocumentActionParamsEditor } from './DocumentActionParamsEditor'
import { MANUAL_EVENT } from './documentActionConfig'
import { StructuredParamsEditor } from './StructuredParamsEditor'
import { RecipeView } from './RecipeView'
import {
  eventAvailability,
  recipeToActionShape,
  type EffectRecipe,
} from './effectRecipes'
import {
  ActionsView,
  ManualButtonsView,
  OpsView,
  RulesView,
} from './BusinessActionsViews'
import { cloneAction, cloneOp, mergeClonedActions } from './businessActionDraft'
import {
  actionKey,
  eventLabel,
  formatMatch,
  formatOpSentence,
  formatSourceTerms,
  labelWithCode,
  makeLabelLookup,
  makeNameLookup,
  parseMatchItems,
  reverseTextOf,
  ruleKey,
  type BusinessNameLookup,
  type MatchGroupPreset,
} from './businessActionText'
import type { MenuAdminModule } from './MenuAdminPage'

export interface BusinessActionOp {
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

export interface BusinessAction {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  condition?: string | null
  params?: string | null
  reverse?: string | null
  remark?: string | null
  sourceRef?: string | null
  ops?: BusinessActionOp[]
  /** 按钮标题（仅 EVENT_CODE='MANUAL' 使用；空则回落处理器声明的默认文案）。 */
  label?: string | null
  /** 点击后先返回"将会发生什么"、用户确认才执行（仅 MANUAL 使用）。 */
  confirmTag?: boolean
}

/** 一个可配置的自定义按钮（服务端操作注册表下发）。 */
export interface DocumentActionCatalogEntry {
  key: string
  label: string
  placement: string
}

/** 按钮授权镜子：配了没人能用是正常状态，所以界面必须显式说明。 */
export interface DocumentActionAuthorizationEntry {
  seq: number
  key: string
  label: string
  users: number
  groups: number
}

export interface ValidationRule {
  seq: number
  stage: string
  validationKey: string
  enabled: boolean
  params?: string | null
  message?: string | null
  remark?: string | null
  sourceRef?: string | null
}

interface ModuleBusinessConfig {
  moduleId: number
  actions: BusinessAction[]
  validationRules: ValidationRule[]
}

/** 面板当前编辑中的配置草稿：由主页面汇总后随模块一起保存。 */
export interface ModuleBusinessConfigDraft {
  moduleId: number
  actions: BusinessAction[]
  validationRules: ValidationRule[]
  /** 当前编辑内容与已落库配置不一致（用于「已修改未保存」状态判断）。 */
  dirty: boolean
}

export interface BusinessConfigCatalog {
  events: string[]
  failModes: string[]
  effectKeys: string[]
  opCodes: string[]
  sourceScopes: string[]
  sourceAggregates: string[]
  validationStages: string[]
  validationKeys: string[]
  /** 目录值 → 中文显示名（服务端随目录同源下发；缺标签时界面回落到目录码）。 */
  labels?: CatalogLabels | null
  /** 可配置的自定义按钮键（MANUAL 行只能从这里挑；未登记实现即发布不出去）。 */
  documentActions?: DocumentActionCatalogEntry[] | null
  /** 接不到效果链的事件（可配置但配了不会跑）：界面如实标注，不隐藏。 */
  inertEvents?: string[] | null
  /** 事件在库内的使用次数（全库）：与 inertEvents 合起来决定"库内 0 行才不可选"。 */
  eventUsage?: Record<string, number> | null
}

interface CatalogLabels {
  events?: Record<string, string>
  failModes?: Record<string, string>
  effectKeys?: Record<string, string>
  opCodes?: Record<string, string>
  sourceScopes?: Record<string, string>
  sourceAggregates?: Record<string, string>
  validationStages?: Record<string, string>
  validationKeys?: Record<string, string>
  /** 效果键说明（这个键在单据上做什么）。 */
  effectKeyDescriptions?: Record<string, string> | null
  /** 反向 kind 说明（解批到底怎么反悔）。 */
  reverseKindDescriptions?: Record<string, string> | null
}

interface EffectParamSchema {
  effectKey: string
  rootKeys: string[]
}

/** 一个效果参数的深 Schema 描述（类型/是否必填/枚举/默认值/说明/示例）。 */
interface EffectParamField {
  name: string
  type: string
  required: boolean
  enumValues?: string[] | null
  default?: string | null
  description?: string | null
  example?: string | null
}

interface BusinessConfigSchemas {
  effects: EffectParamSchema[]
  reverseKinds: string[]
  reverseKindLabels?: Record<string, string> | null
  /** 校验模板参数根键白名单（与效果参数同形；界面共用结构化参数编辑器）。 */
  validationParams?: { validationKey: string; rootKeys: string[] }[] | null
  /** 参数深 Schema（逐键描述；未登记的键仍走根键展开 + 专家模式）。 */
  paramFields?: { effectKey: string; fields: EffectParamField[] }[] | null
  /** 反向 kind 兼容矩阵：受约束的效果键 → 允许的 kind（界面据此过滤下拉）。 */
  reverseKindsByEffect?: Record<string, string[]> | null
  /** 效果配方目录（默认视图；配方只做预填，专家模式仍可逐字段编辑）。 */
  recipes?: EffectRecipe[] | null
}

/** 本模块涉及的表/字段中文名（服务端按模块解析，用于把配置渲染成人话）。 */
interface BusinessFieldLabels {
  tables?: Record<string, string>
  fields?: Record<string, string>
}

/** 目录中文标签查询器集合（各枚举一套，大小写不敏感）。 */
export interface LabelLookups {
  events: (code: string | null | undefined) => string
  failModes: (code: string | null | undefined) => string
  effectKeys: (code: string | null | undefined) => string
  opCodes: (code: string | null | undefined) => string
  sourceScopes: (code: string | null | undefined) => string
  sourceAggregates: (code: string | null | undefined) => string
  validationStages: (code: string | null | undefined) => string
  validationKeys: (code: string | null | undefined) => string
}

/** 行为配置页签：容器按当前页签只渲染对应视图，切换页签不卸载容器。 */
export type BusinessActionsView = 'actions' | 'rules' | 'manual'

interface BusinessActionsPanelProps {
  module: MenuAdminModule
  /**
   * 当前显示的行为页签；null 表示不在任何行为页签上。
   * 容器始终持有草稿与查询，页签切换只换视图，不重挂组件——否则未保存编辑会被
   * 装载副作用按服务端缓存重置掉。
   */
  view?: BusinessActionsView | null
  /** 外部要求重新装载草稿的信号（如页面「取消」丢弃改动）：每次自增都视为一个新的装载点。 */
  reloadSignal?: number
  /** 草稿上报：配置装载完成或编辑变化时回调；null 表示当前没有可提交的草稿（未装载/无表模块）。 */
  onDraftChange?: (draft: ModuleBusinessConfigDraft | null) => void
}

type EditorState =
  | { kind: 'action'; index: number | null; value: BusinessAction }
  | { kind: 'op'; actionIndex: number; index: number | null; value: BusinessActionOp }
  | { kind: 'rule'; index: number | null; value: ValidationRule }

const cloneRule = (rule: ValidationRule): ValidationRule => ({ ...rule })

/** 新建动作的初始值：按钮行从"第一个已登记的操作键"起步，其余事件仍从字段累加起步。 */
const emptyAction = (eventCode: string, seq: number, documentActionKey?: string): BusinessAction => ({
  seq,
  eventCode,
  effectKey: eventCode === MANUAL_EVENT ? (documentActionKey ?? '') : 'field-accumulate',
  effectName: '',
  enabled: true,
  failMode: 'BLOCK',
  condition: null,
  params: null,
  reverse: null,
  remark: null,
  sourceRef: null,
  ops: [],
  label: null,
  confirmTag: false,
})

const emptyOp = (): BusinessActionOp => ({
  opSeq: 1,
  targetTable: '',
  targetField: '',
  opCode: 'ASSIGN',
  sourceScope: 'MASTER',
  sourceTable: null,
  sourceField: null,
  sourceAgg: null,
  sourceConstant: null,
  sourceTerms: null,
  match: null,
  condition: null,
  remark: null,
})

const emptyRule = (stage: string): ValidationRule => ({
  seq: 1,
  stage,
  validationKey: 'qty-not-exceed',
  enabled: true,
  params: '{}',
  message: '',
  remark: null,
  sourceRef: null,
})

export function BusinessActionsPanel({
  module,
  view = 'actions',
  reloadSignal = 0,
  onDraftChange,
}: BusinessActionsPanelProps) {
  const moduleId = module.M_IDX
  const hasTables = module.MASTER_TABLE != null || module.DETAIL_TABLE != null
  const active = view != null

  // 已进入过行为页签的模块编号：容器常驻不等于提前取数——没进过行为页签的模块
  // 不该把配置拉下来。
  const [enteredModuleId, setEnteredModuleId] = useState<number | null>(null)
  useEffect(() => {
    if (active) setEnteredModuleId(moduleId)
  }, [active, moduleId])
  const entered = enteredModuleId === moduleId
  const enabled = entered && moduleId > 0 && hasTables

  const configQuery = useQuery({
    queryKey: ['module-business-config', moduleId],
    queryFn: () => apiClient.get<ModuleBusinessConfig>(`/admin/module-business-config/${moduleId}`),
    enabled,
  })
  const catalogQuery = useQuery({
    queryKey: ['module-business-config-meta'],
    queryFn: () => apiClient.get<BusinessConfigCatalog>(`/admin/module-business-config/meta`),
    enabled,
  })
  const schemasQuery = useQuery({
    queryKey: ['module-business-config-schemas'],
    queryFn: () => apiClient.get<BusinessConfigSchemas>(`/admin/module-business-config/schemas`),
    enabled,
  })
  // 表/字段中文名：只用于把配置渲染成人话，缺失时回落显示列名本身。
  const fieldLabelsQuery = useQuery({
    queryKey: ['module-business-config-field-labels', moduleId],
    queryFn: () => apiClient.get<BusinessFieldLabels>(`/admin/module-business-config/${moduleId}/field-labels`),
    enabled,
  })
  // 按钮授权镜子：授权是 fail-closed 名单，"配了没人能用"是正常状态，界面上必须说出来。
  // 只在「自定义按钮」页签里用，故随该页签懒加载。
  const authorizationQuery = useQuery({
    queryKey: ['module-business-config-action-authorization', moduleId],
    queryFn: () =>
      apiClient.get<{ buttons: DocumentActionAuthorizationEntry[] }>(
        `/admin/module-business-config/${moduleId}/action-authorization`,
      ),
    enabled: enabled && view === 'manual',
  })

  const [actions, setActions] = useState<BusinessAction[]>([])
  const [rules, setRules] = useState<ValidationRule[]>([])
  // 两个页签各记各的选中行：效果链与按钮行混用一个选中态会让加工单区显示错对象。
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [selectedManualKey, setSelectedManualKey] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorState | null>(null)
  /**
   * 效果链页签的两态视图（配方 / 专家·键级）。它只是"看哪一面"的偏好，
   * **不改数据**——两态共用同一份 `actions` 草稿，切换不丢改动。
   * null = 还没有显式选择，按下面的规则取默认。
   */
  const [viewChoice, setViewChoice] = useState<'recipes' | 'expert' | null>(null)
  // 已从服务端装载完成的模块编号：只有装载完成才向上报草稿，避免切换模块的瞬间
  // 用空配置覆盖主页面持有的草稿。
  const [loadedModuleId, setLoadedModuleId] = useState<number | null>(null)

  // 助手处境上报：正在配哪个按钮/效果对象（只报标识，不报配置内容）。
  const editingEffectKey = editor?.kind === 'action' && editor.value.effectKey ? editor.value.effectKey : undefined
  useEffect(() => {
    if (!enabled) {
      setConfigTarget(null)
      return
    }

    setConfigTarget(editingEffectKey ? { surface: 'effect', effectKey: editingEffectKey } : { surface: 'buttons' })
  }, [enabled, editingEffectKey])
  useEffect(() => () => setConfigTarget(null), [])
  // 「重新加载」的显式装载点：只有它或切换模块才把服务端配置灌回草稿。
  const [reloadToken, setReloadToken] = useState(0)
  const loadedPointRef = useRef<string | null>(null)

  useEffect(() => {
    setActions([])
    setRules([])
    setSelectedKey(null)
    setSelectedManualKey(null)
    setEditor(null)
    setLoadedModuleId(null)
    loadedPointRef.current = null
  }, [moduleId])

  useEffect(() => {
    const data = configQuery.data
    if (!data) return
    // 装载点 = 模块 + 重新加载序号 + 外部装载信号。查询后台重取（窗口聚焦等）落在同一装载点时
    // 不再灌入，避免把未保存的编辑静默冲掉。
    const point = `${moduleId}#${reloadSignal}#${reloadToken}`
    if (loadedPointRef.current === point) return
    loadedPointRef.current = point
    setActions((data.actions ?? []).map(cloneAction))
    setRules((data.validationRules ?? []).map(cloneRule))
    setLoadedModuleId(data.moduleId)
  }, [configQuery.data, moduleId, reloadSignal, reloadToken])

  const configDirty = useMemo(() => {
    if (!configQuery.data) return false
    return JSON.stringify(actions) !== JSON.stringify((configQuery.data.actions ?? []).map(cloneAction))
      || JSON.stringify(rules) !== JSON.stringify((configQuery.data.validationRules ?? []).map(cloneRule))
  }, [actions, rules, configQuery.data])

  useEffect(() => {
    if (!onDraftChange) return
    if (loadedModuleId !== moduleId) {
      onDraftChange(null)
      return
    }
    onDraftChange({ moduleId, actions, validationRules: rules, dirty: configDirty })
  }, [actions, rules, configDirty, loadedModuleId, moduleId, onDraftChange])

  const sortedActions = useMemo(
    () => [...actions].sort((a, b) => a.eventCode.localeCompare(b.eventCode) || a.seq - b.seq),
    [actions],
  )
  const sortedRules = useMemo(
    () => [...rules].sort((a, b) => a.stage.localeCompare(b.stage) || a.seq - b.seq),
    [rules],
  )
  /** 用户点击类动作（自定义按钮）：它们不参与效果链，界面按另一套字段编辑。 */
  const manualActions = useMemo(
    () => actions.filter((item) => item.eventCode === MANUAL_EVENT).sort((a, b) => a.seq - b.seq),
    [actions],
  )
  /** 效果链行（非 MANUAL）：与按钮行分属两个页签。 */
  const effectActions = useMemo(
    () => sortedActions.filter((item) => item.eventCode !== MANUAL_EVENT),
    [sortedActions],
  )
  /**
   * 两态视图的默认：**还没有效果链行的模块**默认给配方（入门路径）；
   * 已有配置的模块默认为专家（键级）——既有配置不该因为"默认换了个视图"而先被藏起来。
   * 显式切换过就以选择为准。
   */
  const recipeFirst = viewChoice != null ? viewChoice === 'recipes' : effectActions.length === 0
  /** 按钮键 → 中文名（菜单/授权镜子等处渲染人话用）。 */
  const documentActionLookup = useMemo(
    () => makeLabelLookup(Object.fromEntries((catalogQuery.data?.documentActions ?? []).map((item) => [item.key, item.label]))),
    [catalogQuery.data],
  )
  /** 效果配方（服务端下发；旧版 API 没有这个字段时为空数组，视图会给出回落入口）。 */
  const recipes = useMemo<EffectRecipe[]>(() => schemasQuery.data?.recipes ?? [], [schemasQuery.data])
  /**
   * 事件可用性：库内 0 行**且**接不到效果链的事件不可选（暂未启用）；
   * 接得到效果链的事件照常；接不到但库里已有行的事件**保持可选并标注**——
   * 隐藏它们会让既有配置再也改不动。
   */
  const eventAvailabilityOf = useMemo(() => {
    const inert = catalogQuery.data?.inertEvents ?? []
    const usage = catalogQuery.data?.eventUsage ?? null
    return (code: string) => eventAvailability(code, inert, usage)
  }, [catalogQuery.data])
  const labels = useMemo<LabelLookups>(() => {
    const source = catalogQuery.data?.labels
    return {
      events: makeLabelLookup(source?.events),
      failModes: makeLabelLookup(source?.failModes),
      effectKeys: makeLabelLookup(source?.effectKeys),
      opCodes: makeLabelLookup(source?.opCodes),
      sourceScopes: makeLabelLookup(source?.sourceScopes),
      sourceAggregates: makeLabelLookup(source?.sourceAggregates),
      validationStages: makeLabelLookup(source?.validationStages),
      validationKeys: makeLabelLookup(source?.validationKeys),
    }
  }, [catalogQuery.data])
  const [selectedOpSeq, setSelectedOpSeq] = useState<number | null>(null)
  const [selectedRuleId, setSelectedRuleId] = useState<string | null>(null)
  const [opView, setOpView] = useState<'sheet' | 'grid'>('sheet')
  const [cloneOpen, setCloneOpen] = useState(false)
  // 预演与说明书都是只读诊断：不改配置，因此不参与草稿脏判断。
  const [simulateOpen, setSimulateOpen] = useState(false)
  const [handbookOpen, setHandbookOpen] = useState(false)
  const names = useMemo(
    () => makeNameLookup(fieldLabelsQuery.data, module.MASTER_TABLE, module.DETAIL_TABLE),
    [fieldLabelsQuery.data, module.MASTER_TABLE, module.DETAIL_TABLE],
  )
  // 本模块已配好的定位键组（按 JSON 去重）：一处配、多处复用，替代逐行重抄。
  const matchPresets = useMemo<MatchGroupPreset[]>(() => {
    const groups = new Map<string, MatchGroupPreset>()
    for (const action of actions) {
      for (const op of action.ops ?? []) {
        const json = (op.match ?? '').trim()
        if (json === '') continue
        const items = parseMatchItems(json)
        if (!items || items.length === 0) continue
        const existing = groups.get(json)
        if (existing) {
          existing.count += 1
          continue
        }
        groups.set(json, {
          json,
          targetTable: (op.targetTable ?? '').trim(),
          count: 1,
          labels: items.map((item) => item.target),
        })
      }
    }
    return [...groups.values()].sort((a, b) => b.count - a.count)
  }, [actions])
  const selectedIndex = actions.findIndex((action) => actionKey(action) === selectedKey)
  const selectedAction = selectedIndex >= 0 ? actions[selectedIndex] : undefined
  const selectedOps = useMemo(
    () => [...(selectedAction?.ops ?? [])].sort((a, b) => a.opSeq - b.opSeq),
    [selectedAction],
  )
  const selectedOp = selectedOps.find((op) => op.opSeq === selectedOpSeq)
  const selectedRule = sortedRules.find((rule) => ruleKey(rule) === selectedRuleId)
  // 自定义按钮页签的选中行：按 MANUAL 行在草稿里的位置回查索引，工具栏操作按索引走。
  const selectedManualIndex = manualActions.findIndex((action) => actionKey(action) === selectedManualKey)
  const selectedManual = selectedManualIndex >= 0 ? manualActions[selectedManualIndex] : undefined
  const manualDraftIndex = selectedManual != null
    ? actions.findIndex((item) => actionKey(item) === actionKey(selectedManual))
    : -1

  // 切换动作后原选中公式行不再属于当前动作，清空选中避免误删。
  useEffect(() => {
    setSelectedOpSeq(null)
  }, [selectedKey])

  const replaceAction = (index: number, value: BusinessAction) =>
    setActions((prev) => prev.map((item, i) => (i === index ? cloneAction(value) : item)))
  const replaceRule = (index: number, value: ValidationRule) =>
    setRules((prev) => prev.map((item, i) => (i === index ? cloneRule(value) : item)))

  const confirmEditor = () => {
    if (!editor) return
    if (editor.kind === 'action') {
      if (editor.index == null) {
        const value = cloneAction(editor.value)
        setActions((prev) => [...prev, value])
        // 新增后选中它：按行类型落到对应页签的选中态上。
        if (value.eventCode === MANUAL_EVENT) setSelectedManualKey(actionKey(value))
        else setSelectedKey(actionKey(value))
      } else {
        replaceAction(editor.index, editor.value)
      }
    } else if (editor.kind === 'rule') {
      if (editor.index == null) setRules((prev) => [...prev, cloneRule(editor.value)])
      else replaceRule(editor.index, editor.value)
    } else {
      const actionIndex = editor.actionIndex
      const op = cloneOp(editor.value)
      setActions((prev) =>
        prev.map((action, i) => {
          if (i !== actionIndex) return action
          const ops = [...(action.ops ?? [])]
          if (editor.index == null) ops.push(op)
          else ops[editor.index] = op
          return { ...action, ops }
        }),
      )
    }
    setEditor(null)
  }

  const openCreateAction = () => {
    const eventCode = catalogQuery.data?.events.includes('APPROVE_EFFECT') ? 'APPROVE_EFFECT' : (catalogQuery.data?.events[0] ?? 'SAVE')
    const seq = (actions.filter((item) => item.eventCode === eventCode).reduce((max, item) => Math.max(max, item.seq), 0)) + 1
    setEditor({ kind: 'action', index: null, value: emptyAction(eventCode, seq, catalogQuery.data?.documentActions?.[0]?.key) })
  }
  /**
   * 从配方起草一行：事件/效果键/反向照配方预填（`recipeToActionShape`），其余走新建动作的默认值。
   * 打开的是**同一个编辑器**——配方只改"起步值"，不改变"能配什么"。
   */
  const openCreateFromRecipe = (recipe: EffectRecipe) => {
    const eventCode = recipe.eventCodes[0] ?? 'APPROVE_EFFECT'
    const seq = actions
      .filter((item) => item.eventCode === eventCode)
      .reduce((max, item) => Math.max(max, item.seq), 0) + 1
    const shape = recipeToActionShape(recipe, seq)
    const base = emptyAction(shape.eventCode, shape.seq)
    setEditor({
      kind: 'action',
      index: null,
      value: { ...base, effectKey: shape.effectKey, reverse: shape.reverse },
    })
  }
  const openCreateRule = () => {
    const stage = catalogQuery.data?.validationStages.includes('SAVE') ? 'SAVE' : (catalogQuery.data?.validationStages[0] ?? 'SAVE')
    const seq = (rules.filter((item) => item.stage === stage).reduce((max, item) => Math.max(max, item.seq), 0)) + 1
    setEditor({ kind: 'rule', index: null, value: { ...emptyRule(stage), seq } })
  }
  const openCreateOp = () => {
    if (selectedIndex < 0) return
    const ops = selectedAction?.ops ?? []
    const opSeq = ops.reduce((max, item) => Math.max(max, item.opSeq), 0) + 1
    setEditor({ kind: 'op', actionIndex: selectedIndex, index: null, value: { ...emptyOp(), opSeq } })
  }
  const openEditOp = (index: number) => {
    if (selectedIndex < 0 || !selectedAction?.ops) return
    setEditor({ kind: 'op', actionIndex: selectedIndex, index, value: cloneOp(selectedAction.ops[index]) })
  }
  /** 删除某个草稿位置的动作（两个页签共用）。 */
  const removeActionAt = (index: number) => {
    if (index < 0) return
    setActions((prev) => prev.filter((_, i) => i !== index))
  }
  /** 复制某个草稿位置的动作（同事件内顺延顺序号），返回副本行键。 */
  const duplicateActionAt = (index: number): string | null => {
    const source = actions[index]
    if (!source) return null
    const seq = actions
      .filter((item) => item.eventCode === source.eventCode)
      .reduce((max, item) => Math.max(max, item.seq), 0) + 1
    const copy = cloneAction({ ...source, seq })
    setActions((prev) => [...prev, copy])
    return actionKey(copy)
  }
  /** 上下移动某个草稿位置的动作：只在同事件组内换顺序号。 */
  const moveActionAt = (index: number, delta: number) => {
    const current = actions[index]
    if (!current) return
    const peers = actions
      .map((item, position) => ({ item, index: position }))
      .filter((entry) => entry.item.eventCode === current.eventCode)
      .sort((a, b) => a.item.seq - b.item.seq)
    const position = peers.findIndex((entry) => entry.index === index)
    const target = peers[position + delta]
    if (!target) return
    setActions((prev) =>
      prev.map((item, i) => {
        if (i === index) return { ...item, seq: target.item.seq }
        if (i === target.index) return { ...item, seq: current.seq }
        return item
      }),
    )
  }
  const deleteSelectedAction = () => {
    if (selectedIndex < 0) return
    removeActionAt(selectedIndex)
    setSelectedKey(null)
  }
  /** 复制选中动作（同事件内顺延顺序号）：改行为时从"相近的一步"起步，比从空白新建快。 */
  const duplicateSelectedAction = () => {
    if (!selectedAction) return
    const key = duplicateActionAt(selectedIndex)
    if (!key) return
    setSelectedKey(key)
    setSelectedOpSeq(null)
  }
  /** 新增自定义按钮：顺序号在 MANUAL 组内顺延（唯一键按 (模块, 事件, 顺序号) 约束）。 */
  const openCreateManualAction = () => {
    const documentActions = catalogQuery.data?.documentActions ?? []
    const seq = manualActions.reduce((max, item) => Math.max(max, item.seq), 0) + 1
    setEditor({ kind: 'action', index: null, value: emptyAction(MANUAL_EVENT, seq, documentActions[0]?.key) })
  }
  const editSelectedManualAction = () => {
    if (!selectedManual) return
    setEditor({ kind: 'action', index: manualDraftIndex, value: cloneAction(selectedManual) })
  }
  const deleteSelectedManualAction = () => {
    removeActionAt(manualDraftIndex)
    setSelectedManualKey(null)
  }
  const duplicateSelectedManualAction = () => {
    if (!selectedManual) return
    const key = duplicateActionAt(manualDraftIndex)
    if (!key) return
    setSelectedManualKey(key)
  }
  const moveSelectedManualAction = (delta: number) => moveActionAt(manualDraftIndex, delta)
  /** 复制选中公式行（动作内顺延顺序号）。 */
  const duplicateSelectedOp = () => {
    if (selectedIndex < 0 || !selectedOp) return
    const ops = selectedAction?.ops ?? []
    const opSeq = ops.reduce((max, item) => Math.max(max, item.opSeq), 0) + 1
    setActions((prev) =>
      prev.map((action, i) =>
        i === selectedIndex ? { ...action, ops: [...(action.ops ?? []), cloneOp({ ...selectedOp, opSeq })] } : action,
      ),
    )
    setSelectedOpSeq(opSeq)
  }
  /** 从其它模块克隆来的动作：并入草稿（过滤与序号顺延见 mergeClonedActions）。 */
  const appendClonedActions = (incoming: BusinessAction[]) => {
    setActions((prev) => mergeClonedActions(prev, incoming))
  }
  /**
   * 把一套定位键写回本动作中目标表相同的其余步骤：定位键按"目标表 + 键列"定义，   * 同表步骤共用同一套是有意约束，逐行重配只会制造不一致。
   */
  const applyMatchToSiblings = (json: string) => {
    if (selectedIndex < 0 || !selectedOp) return
    const targetTable = selectedOp.targetTable.trim().toUpperCase()
    setActions((prev) =>
      prev.map((action, i) => {
        if (i !== selectedIndex) return action
        return {
          ...action,
          ops: (action.ops ?? []).map((op) =>
            op.targetTable.trim().toUpperCase() === targetTable ? { ...op, match: json } : op,
          ),
        }
      }),
    )
  }
  const editOp = (op: BusinessActionOp) => {
    const list = selectedAction?.ops ?? []
    const index = list.findIndex((item) => item.opSeq === op.opSeq)
    if (index >= 0) openEditOp(index)
  }
  const deleteOp = (op: BusinessActionOp) => {
    if (selectedIndex < 0) return
    setActions((prev) =>
      prev.map((action, i) =>
        i === selectedIndex ? { ...action, ops: (action.ops ?? []).filter((item) => item.opSeq !== op.opSeq) } : action,
      ),
    )
  }
  const editRule = (rule: ValidationRule) => {
    const index = rules.indexOf(rule)
    if (index >= 0) setEditor({ kind: 'rule', index, value: cloneRule(rule) })
  }
  const deleteRule = (rule: ValidationRule) =>
    setRules((prev) => prev.filter((item) => item !== rule))

  const moveSelectedAction = (delta: number) => moveActionAt(selectedIndex, delta)

  // 不在行为页签上时不渲染内容，但上面的状态与查询继续保留（容器常驻）。
  if (!active) return null

  if (!hasTables || moduleId <= 0) {
    return (
      <div className="p-3 text-secondary">
        两表皆空的模块不参与行为规则。请先在「基础 / 主表 / 子表」配置操作主表/副表并保存模块后再配置。
      </div>
    )
  }

  const ready = configQuery.isSuccess && catalogQuery.isSuccess && schemasQuery.isSuccess

  return (
    <div className="d-flex flex-column gap-3">
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2">
        <div>
          <strong>{tableTitle(module.MASTER_TABLE, module.MASTER_TABLE_DESC)} / {tableTitle(module.DETAIL_TABLE, module.DETAIL_TABLE_DESC)}</strong>
        </div>
        <div className="d-flex gap-2">
          {view === 'actions' ? (
            <div className="btn-group" role="group" aria-label="效果链视图">
              <Button
                size="sm"
                variant={recipeFirst ? 'primary' : 'secondary'}
                onClick={() => setViewChoice('recipes')}
                title="按业务配方配置：先选“要发生什么”，再由配方预填落到实现键"
              >
                配方
              </Button>
              <Button
                size="sm"
                variant={recipeFirst ? 'secondary' : 'primary'}
                onClick={() => setViewChoice('expert')}
                title="键级编辑（专家模式）：逐字段编辑同一份配置，与配方视图共用草稿"
              >
                专家（键级）
              </Button>
            </div>
          ) : null}
          {view === 'actions' ? (
            <Button
              size="sm"
              icon={<IconPlayerPlay size={16} />}
              onClick={() => setSimulateOpen(true)}
              title="在事务内跑一遍真实的批核/解批链路并回滚，报告“会发生什么”"
            >
              预演（不改数据）
            </Button>
          ) : null}
          <Button
            size="sm"
            icon={<IconBook size={16} />}
            onClick={() => setHandbookOpen(true)}
            title="按事件汇总这个模块会发生什么（只读，可复制为 Markdown）"
          >
            行为说明书
          </Button>
          <Button
            size="sm"
            icon={<IconRefresh size={16} />}
            onClick={() => {
              setReloadToken((token) => token + 1)
              void configQuery.refetch()
            }}
          >
            重新加载
          </Button>
        </div>
      </div>

      {configQuery.isLoading || catalogQuery.isLoading || schemasQuery.isLoading ? <LoadingState /> : null}
      {configQuery.isError ? (
        <ErrorState
          message={`加载失败：${describeApiError(configQuery.error, '无法读取模块业务配置。')}`}
          onRetry={() => void configQuery.refetch()}
        />
      ) : null}
      {catalogQuery.isError ? (
        <ErrorState
          message={`目录加载失败：${describeApiError(catalogQuery.error, '无法读取配置目录。')}`}
          onRetry={() => void catalogQuery.refetch()}
        />
      ) : null}
      {schemasQuery.isError ? (
        <ErrorState
          message={`Schema 目录加载失败：${describeApiError(schemasQuery.error, '无法读取效果 Schema。')}`}
          onRetry={() => void schemasQuery.refetch()}
        />
      ) : null}

      {ready ? (
        <>
          {view === 'actions' && recipeFirst ? (
            <RecipeView
              recipes={recipes}
              actions={effectActions}
              labels={labels}
              onCreateFromRecipe={openCreateFromRecipe}
              onClone={() => setCloneOpen(true)}
              onExpert={() => setViewChoice('expert')}
            />
          ) : null}

          {view === 'actions' && !recipeFirst ? (
            <>
              <ActionsView
                catalog={catalogQuery.data!}
                labels={labels}
                names={names}
                actions={effectActions}
                documentActionLookup={documentActionLookup}
                selectedKey={selectedKey}
                onSelect={setSelectedKey}
                onCreate={openCreateAction}
                onEdit={() => {
                  if (selectedIndex >= 0) setEditor({ kind: 'action', index: selectedIndex, value: cloneAction(selectedAction!) })
                }}
                onDuplicate={duplicateSelectedAction}
                onClone={() => setCloneOpen(true)}
                onDelete={deleteSelectedAction}
                onMoveUp={() => moveSelectedAction(-1)}
                onMoveDown={() => moveSelectedAction(1)}
                canEdit={selectedIndex >= 0}
              />
              {selectedAction ? (
                <OpsView
                  action={selectedAction}
                  ops={selectedOps}
                  opView={opView}
                  onOpViewChange={setOpView}
                  selectedOpSeq={selectedOpSeq}
                  onSelectOp={setSelectedOpSeq}
                  names={names}
                  labels={labels}
                  eventText={eventLabel(selectedAction.eventCode, catalogQuery.data!, labels)}
                  effectText={labelWithCode(labels.effectKeys, selectedAction.effectKey)}
                  failModeText={labelWithCode(labels.failModes, selectedAction.failMode)}
                  reverseText={reverseTextOf(selectedAction.reverse, makeLabelLookup(schemasQuery.data?.reverseKindLabels))}
                  onCreateOp={openCreateOp}
                  onDuplicateOp={duplicateSelectedOp}
                  onEditOp={() => {
                    if (selectedOp) editOp(selectedOp)
                  }}
                  onDeleteOp={() => {
                    if (selectedOp) deleteOp(selectedOp)
                  }}
                  canEditOp={selectedOp != null}
                />
              ) : (
                <div className="text-secondary small">先在上方选择一个业务动作，这里显示它的加工单。</div>
              )}
              <BusinessActionImpact
                moduleId={moduleId}
                masterTable={module.MASTER_TABLE}
                detailTable={module.DETAIL_TABLE}
                names={names}
                labels={labels}
                actions={actions}
                rules={rules}
              />
              <div className="text-secondary small">
                加工单视图按「目标表.字段 运算 本单来源 @定位键 ?条件」逐句展示（字段名带中文元数据），点句子即选中该步骤；
                表名/列名经统一选择器选取，定位键可复用本模块已配好的同一套；条件与效果参数仍为结构化 JSON 文本。
                保存即服务端校验（目录值/顺序/JSON/物理表列/定位键登记），校验失败不会落库。
              </div>
            </>
          ) : null}

          {view === 'rules' ? (
            <RulesView
              labels={labels}
              rules={sortedRules}
              selectedRuleId={selectedRuleId}
              onSelect={setSelectedRuleId}
              onCreate={openCreateRule}
              onEdit={() => {
                if (selectedRule) editRule(selectedRule)
              }}
              onDelete={() => {
                if (selectedRule) deleteRule(selectedRule)
              }}
            />
          ) : null}

          {view === 'manual' ? (
            <ManualButtonsView
              actions={manualActions}
              documentActions={catalogQuery.data?.documentActions ?? []}
              authorization={authorizationQuery.data?.buttons}
              selectedKey={selectedManualKey}
              onSelect={setSelectedManualKey}
              onCreate={openCreateManualAction}
              onEdit={editSelectedManualAction}
              onDuplicate={duplicateSelectedManualAction}
              onDelete={deleteSelectedManualAction}
              onMoveUp={() => moveSelectedManualAction(-1)}
              onMoveDown={() => moveSelectedManualAction(1)}
              canEdit={selectedManual != null}
            />
          ) : null}
        </>
      ) : null}

      {cloneOpen ? (
        <CloneActionsModal
          open
          currentModuleId={moduleId}
          onClose={() => setCloneOpen(false)}
          onAppend={appendClonedActions}
        />
      ) : null}

      {simulateOpen ? (
        <EffectSimulationPanel
          moduleId={moduleId}
          moduleTitle={module.M_DESC ?? String(moduleId)}
          actions={effectActions}
          rules={rules}
          names={names}
          reverseKindLabels={schemasQuery.data?.reverseKindLabels}
          onClose={() => setSimulateOpen(false)}
        />
      ) : null}

      {handbookOpen && ready ? (
        <BehaviorHandbook
          moduleTitle={module.M_DESC ?? String(moduleId)}
          masterTable={module.MASTER_TABLE}
          detailTable={module.DETAIL_TABLE}
          actions={actions}
          rules={rules}
          catalog={catalogQuery.data!}
          labels={labels}
          names={names}
          reverseKindLabels={schemasQuery.data?.reverseKindLabels}
          onClose={() => setHandbookOpen(false)}
        />
      ) : null}

      {editor ? (
        <EditorModal
          editor={editor}
          module={module}
          catalog={catalogQuery.data!}
          schemas={schemasQuery.data!}
          labels={labels}
          names={names}
          matchPresets={matchPresets}
          eventAvailabilityOf={eventAvailabilityOf}
          onApplyMatchToSiblings={applyMatchToSiblings}
          onCancel={() => setEditor(null)}
          onConfirm={confirmEditor}
          onActionChange={(value) => setEditor((prev) => (prev?.kind === 'action' ? { ...prev, value } : prev))}
          onOpChange={(value) => setEditor((prev) => (prev?.kind === 'op' ? { ...prev, value } : prev))}
          onRuleChange={(value) => setEditor((prev) => (prev?.kind === 'rule' ? { ...prev, value } : prev))}
        />
      ) : null}
    </div>
  )
}


function EditorModal({
  editor,
  module,
  catalog,
  schemas,
  labels,
  names,
  matchPresets,
  eventAvailabilityOf,
  onApplyMatchToSiblings,
  onActionChange,
  onOpChange,
  onRuleChange,
  onCancel,
  onConfirm,
}: {
  editor: EditorState
  module: MenuAdminModule
  catalog: BusinessConfigCatalog
  schemas: BusinessConfigSchemas
  labels: LabelLookups
  names: BusinessNameLookup
  matchPresets: MatchGroupPreset[]
  eventAvailabilityOf?: (eventCode: string) => { selectable: boolean; note: string | null }
  onApplyMatchToSiblings: (json: string) => void
  onActionChange: (value: BusinessAction) => void
  onOpChange: (value: BusinessActionOp) => void
  onRuleChange: (value: ValidationRule) => void
  onCancel: () => void
  onConfirm: () => void
}) {
  // 自定义按钮与效果链共用这套编辑器，标题按行类型区分，免得在按钮行上看到"业务动作"。
  const isManualAction = editor.kind === 'action' && editor.value.eventCode === MANUAL_EVENT
  const actionTitle = isManualAction ? '自定义按钮' : '业务动作'
  const title = editor.kind === 'action' ? (editor.index == null ? `新增${actionTitle}` : `编辑${actionTitle}`)
    : editor.kind === 'op' ? (editor.index == null ? '新增公式行' : '编辑公式行')
    : (editor.index == null ? '新增校验规则' : '编辑校验规则')

  return (
    <Modal
      title={title}
      onClose={onCancel}
      size="lg"
      scrollable
      footer={
        <div className="d-flex gap-2 justify-content-end">
          <Button variant="secondary" onClick={onCancel}>取消</Button>
          <Button variant="primary" icon={<IconDeviceFloppy size={16} />} onClick={onConfirm}>保存</Button>
        </div>
      }
    >
      {editor.kind === 'action' ? (
        <ActionForm value={editor.value} catalog={catalog} schemas={schemas} labels={labels} names={names} module={module} eventAvailabilityOf={eventAvailabilityOf} onChange={onActionChange} />
      ) : editor.kind === 'op' ? (
        <OpForm
          value={editor.value}
          catalog={catalog}
          labels={labels}
          names={names}
          module={module}
          matchPresets={matchPresets}
          onApplyMatchToSiblings={onApplyMatchToSiblings}
          onChange={onOpChange}
        />
      ) : (
        <RuleForm value={editor.value} catalog={catalog} labels={labels} schemas={schemas} onChange={onRuleChange} />
      )}
    </Modal>
  )
}

function ActionForm({
  value,
  catalog,
  schemas,
  labels,
  names,
  module,
  eventAvailabilityOf,
  onChange,
}: {
  value: BusinessAction
  catalog: BusinessConfigCatalog
  schemas: BusinessConfigSchemas
  labels: LabelLookups
  names: BusinessNameLookup
  module: MenuAdminModule
  /** 事件可用性（库内 0 行 + 接不到效果链 ⇒ 不可选；接不到但有行 ⇒ 可选 + 标注）。 */
  eventAvailabilityOf?: (eventCode: string) => { selectable: boolean; note: string | null }
  onChange: (value: BusinessAction) => void
}) {
  const set = <K extends keyof BusinessAction>(key: K, next: BusinessAction[K]) => onChange({ ...value, [key]: next })
  const effectSchema = (schemas.effects ?? []).find((item) => item.effectKey === value.effectKey)
  const paramFields = (schemas.paramFields ?? []).find((item) => item.effectKey === value.effectKey)?.fields ?? []
  const effectDescription = catalog.labels?.effectKeyDescriptions?.[value.effectKey] ?? null
  // 反向 kind 按效果键过滤：选不到不支持的组合，比保存/发布时才拒绝更早一步。
  const reverseKinds = useMemo(() => {
    const allowed = schemas.reverseKindsByEffect?.[value.effectKey]
    if (!allowed || allowed.length === 0) return schemas.reverseKinds
    const order = new Map(schemas.reverseKinds.map((kind, index) => [kind.toLowerCase(), index]))
    return [...allowed].sort((left, right) => (order.get(left.toLowerCase()) ?? 0) - (order.get(right.toLowerCase()) ?? 0))
  }, [schemas.reverseKindsByEffect, schemas.reverseKinds, value.effectKey])
  const reverseLookup = useMemo(() => makeLabelLookup(schemas.reverseKindLabels), [schemas.reverseKindLabels])
  const isManual = value.eventCode === MANUAL_EVENT
  const documentActions = catalog.documentActions ?? []
  /** 当前所选事件必须让配置者看见的说明（"配了不会跑""暂未启用"这类事实）。 */
  const selectedEventNote = eventAvailabilityOf?.(value.eventCode)?.note ?? null
  return (
    <div>
      <div className="row g-2">
        <Field label="事件" className="col-4">
          <select className="form-select form-select-sm" value={value.eventCode} onChange={(e) => set('eventCode', e.target.value)}>
            {catalog.events.map((item) => {
              const availability = eventAvailabilityOf?.(item) ?? { selectable: true, note: null }
              return (
                <option key={item} value={item} disabled={!availability.selectable}>
                  {eventLabel(item, catalog, labels)}{availability.note ? `（${availability.note}）` : ''}
                </option>
              )
            })}
          </select>
          {selectedEventNote ? <div className="small text-warning-emphasis mt-1">{selectedEventNote}</div> : null}
        </Field>
        <Field label="顺序（事件内）" className="col-2">
          <input type="number" min={1} className="form-control form-control-sm" value={value.seq}
            onChange={(e) => set('seq', Number(e.target.value))} />
        </Field>
        <Field label={isManual ? '按钮实现（代码闭集）' : '效果'} className="col-6">
          {isManual ? (
            <select className="form-select form-select-sm" value={value.effectKey} onChange={(e) => set('effectKey', e.target.value)}>
              {value.effectKey !== '' && !documentActions.some((item) => item.key === value.effectKey)
                ? <option value={value.effectKey}>{value.effectKey}（未登记实现）</option>
                : null}
              {documentActions.map((item) => (
                <option key={item.key} value={item.key}>
                  {item.label}（{item.key}）· {item.placement === 'detail' ? '明细级' : '单据级'}
                </option>
              ))}
            </select>
          ) : (
            <select
              className="form-select form-select-sm"
              value={value.effectKey}
              onChange={(e) => set('effectKey', e.target.value)}
              title={effectDescription ?? undefined}
            >
              {catalog.effectKeys.map((item) => (
                <option key={item} value={item} title={catalog.labels?.effectKeyDescriptions?.[item] ?? undefined}>
                  {labelWithCode(labels.effectKeys, item)}
                </option>
              ))}
            </select>
          )}
        </Field>
        {!isManual && effectDescription ? (
          <Field label="这个效果做什么" className="col-12">
            <div className="small text-secondary">{effectDescription}</div>
          </Field>
        ) : null}
        <Field label="名称" className="col-8">
          <input className="form-control form-control-sm" value={value.effectName ?? ''}
            onChange={(e) => set('effectName', e.target.value || null)} />
        </Field>
        <Field label="失败模式" className="col-4">
          <select className="form-select form-select-sm" value={value.failMode} onChange={(e) => set('failMode', e.target.value)}>
            {catalog.failModes.map((item) => <option key={item} value={item}>{labelWithCode(labels.failModes, item)}</option>)}
          </select>
        </Field>
        {isManual ? (
          <>
            <Field label="按钮标题（显示在单据上）" className="col-8">
              <input className="form-control form-control-sm" value={value.label ?? ''}
                placeholder="留空则用处理器声明的默认文案"
                onChange={(e) => set('label', e.target.value || null)} />
            </Field>
            <Field label="点击前二次确认" className="col-4">
              <div className="form-check">
                <input id="action-confirm-tag" className="form-check-input" type="checkbox" checked={value.confirmTag === true}
                  onChange={(e) => set('confirmTag', e.target.checked)} />
                <label className="form-check-label" htmlFor="action-confirm-tag">先返回"将会发生什么"</label>
              </div>
            </Field>
          </>
        ) : null}
        <Field label="启用" className="col-12">
          <div className="form-check">
            <input id="action-enabled" className="form-check-input" type="checkbox" checked={value.enabled}
              onChange={(e) => set('enabled', e.target.checked)} />
            <label className="form-check-label" htmlFor="action-enabled">启用该动作</label>
          </div>
        </Field>
        <Field label="说明" className="col-6">
          <input className="form-control form-control-sm" value={value.remark ?? ''}
            onChange={(e) => set('remark', e.target.value || null)} />
        </Field>
        <Field label="溯源（可选）" className="col-6">
          <input className="form-control form-control-sm" value={value.sourceRef ?? ''}
            onChange={(e) => set('sourceRef', e.target.value || null)} />
        </Field>
        <Field label="条件（什么时候执行）" className="col-12">
          <ConditionEditor
            value={value.condition ?? null}
            names={names}
            masterTable={module.MASTER_TABLE}
            detailTable={module.DETAIL_TABLE}
            onChange={(next) => set('condition', next)}
          />
        </Field>
        {isManual ? (
          <Field label="点击时让用户填的参数" className="col-12">
            <DocumentActionParamsEditor json={value.params ?? null} onChange={(json) => set('params', json)} />
          </Field>
        ) : (
          <Field label="参数（按效果 Schema 编辑）" className="col-12">
            <StructuredParamsEditor
              hint={`效果键：${value.effectKey}${effectSchema ? `（根键 ${effectSchema.rootKeys.join(' / ')}）` : '（未登记 Schema，禁止携带参数）'}`}
              rootKeys={effectSchema?.rootKeys ?? []}
              descriptors={paramFields}
              json={value.params ?? null}
              emptyHint="该效果不允许配置参数。"
              onChange={(json) => set('params', json)}
            />
          </Field>
        )}
        {isManual ? null : (
          <Field label="反向（解批语义）" className="col-12">
            <ReverseEditor
              kinds={reverseKinds}
              lookup={reverseLookup}
              descriptions={catalog.labels?.reverseKindDescriptions}
              json={value.reverse ?? null}
              onChange={(json) => set('reverse', json || null)}
            />
          </Field>
        )}
      </div>
    </div>
  )
}

function OpForm({
  value,
  catalog,
  labels,
  names,
  module,
  matchPresets,
  onApplyMatchToSiblings,
  onChange,
}: {
  value: BusinessActionOp
  catalog: BusinessConfigCatalog
  labels: LabelLookups
  names: BusinessNameLookup
  module: MenuAdminModule
  matchPresets: MatchGroupPreset[]
  onApplyMatchToSiblings: (json: string) => void
  onChange: (value: BusinessActionOp) => void
}) {
  const set = <K extends keyof BusinessActionOp>(key: K, next: BusinessActionOp[K]) => onChange({ ...value, [key]: next })
  const isConstant = value.sourceScope === 'CONSTANT'
  const isTable = value.sourceScope === 'TABLE'
  const scopeLabels = catalog.labels?.sourceScopes ?? {}
  const sourceTable = value.sourceScope === 'MASTER'
    ? module.MASTER_TABLE
    : value.sourceScope === 'DETAIL'
      ? module.DETAIL_TABLE
      : value.sourceTable ?? null
  return (
    <div className="row g-2">
      <div className="col-12">
        <div className="alert alert-light border py-1 px-2 small mb-0">
          公式预览：<code>{formatOpSentence(value, names)}</code>
          {formatMatch(value.match, names) ? <span className="ms-2 text-secondary">{formatMatch(value.match, names)}</span> : null}
        </div>
      </div>
      <Field label="顺序" className="col-2">
        <input type="number" min={1} className="form-control form-control-sm" value={value.opSeq}
          onChange={(e) => set('opSeq', Number(e.target.value))} />
      </Field>
      <Field label="目标表" className="col-5">
        <TablePickerInput
          value={value.targetTable}
          title="选择目标表"
          onChange={(next) => set('targetTable', next)}
        />
      </Field>
      <Field label="目标字段" className="col-5">
        <ColumnPickerInput
          table={value.targetTable}
          value={value.targetField}
          title="选择目标字段"
          names={names}
          onChange={(next) => set('targetField', next)}
        />
      </Field>
      <Field label="运算" className="col-4">
        <select className="form-select form-select-sm" value={value.opCode} onChange={(e) => set('opCode', e.target.value)}>
          {catalog.opCodes.map((item) => <option key={item} value={item}>{labelWithCode(labels.opCodes, item)}</option>)}
        </select>
      </Field>
      <Field label="源范围" className="col-4">
        <select className="form-select form-select-sm" value={value.sourceScope}
          onChange={(e) => set('sourceScope', e.target.value)}>
          {catalog.sourceScopes.map((item) => <option key={item} value={item}>{labelWithCode(labels.sourceScopes, item)}</option>)}
        </select>
      </Field>
      <Field label="源聚合（空=单值）" className="col-4">
        <select className="form-select form-select-sm" value={value.sourceAgg ?? ''}
          onChange={(e) => set('sourceAgg', e.target.value || null)}>
          <option value="">（单值）</option>
          {catalog.sourceAggregates.map((item) => <option key={item} value={item}>{labelWithCode(labels.sourceAggregates, item)}</option>)}
        </select>
      </Field>
      {isTable ? (
        <Field label="源表（已登记上下文表）" className="col-6">
          <TablePickerInput
            value={value.sourceTable ?? ''}
            title="选择上下文表"
            onChange={(next) => set('sourceTable', next || null)}
          />
        </Field>
      ) : null}
      {isConstant ? (
        <Field label="常量值" className="col-6">
          <input className="form-control form-control-sm" value={value.sourceConstant ?? ''}
            onChange={(e) => set('sourceConstant', e.target.value)} />
        </Field>
      ) : (
        <>
          <Field label="源字段（与源加减项二选一）" className="col-6">
            <ColumnPickerInput
              table={sourceTable}
              value={value.sourceField ?? ''}
              title="选择源字段"
              names={names}
              onChange={(next) => set('sourceField', next || null)}
            />
          </Field>
          <Field label="源加减项（结构化 JSON，可选）" className="col-12">
            <JsonArea value={value.sourceTerms ?? ''} onChange={(text) => set('sourceTerms', text || null)} />
            {formatSourceTerms(value.sourceTerms) ? (
              <div className="small text-secondary mt-1">解析为：{formatSourceTerms(value.sourceTerms)}</div>
            ) : null}
          </Field>
        </>
      )}
      <Field label="定位键" className="col-12">
        <MatchEditor
          moduleId={module.M_IDX}
          masterTable={module.MASTER_TABLE}
          detailTable={module.DETAIL_TABLE}
          targetTable={value.targetTable}
          contextTable={value.sourceTable ?? null}
          value={value.match ?? null}
          scopeLabels={scopeLabels}
          names={names}
          presets={matchPresets.filter((preset) => preset.targetTable.toUpperCase() === value.targetTable.trim().toUpperCase())}
          onApplyToSiblings={onApplyMatchToSiblings}
          onChange={(next) => set('match', next)}
        />
      </Field>
      <Field label="条件（什么时候执行）" className="col-12">
        <ConditionEditor
          value={value.condition ?? null}
          names={names}
          targetTable={value.targetTable}
          masterTable={module.MASTER_TABLE}
          detailTable={module.DETAIL_TABLE}
          onChange={(next) => set('condition', next)}
        />
      </Field>
      <Field label="说明" className="col-12">
        <input className="form-control form-control-sm" value={value.remark ?? ''}
          onChange={(e) => set('remark', e.target.value || null)} />
      </Field>
    </div>
  )
}

function RuleForm({
  value,
  catalog,
  labels,
  schemas,
  onChange,
}: {
  value: ValidationRule
  catalog: BusinessConfigCatalog
  labels: LabelLookups
  schemas: BusinessConfigSchemas
  onChange: (value: ValidationRule) => void
}) {
  const set = <K extends keyof ValidationRule>(key: K, next: ValidationRule[K]) => onChange({ ...value, [key]: next })
  const paramSchema = (schemas.validationParams ?? []).find((item) => item.validationKey === value.validationKey)
  return (
    <div className="row g-2">
      <Field label="阶段" className="col-4">
        <select className="form-select form-select-sm" value={value.stage} onChange={(e) => set('stage', e.target.value)}>
          {catalog.validationStages.map((item) => <option key={item} value={item}>{labelWithCode(labels.validationStages, item)}</option>)}
        </select>
      </Field>
      <Field label="顺序（阶段内）" className="col-2">
        <input type="number" min={1} className="form-control form-control-sm" value={value.seq}
          onChange={(e) => set('seq', Number(e.target.value))} />
      </Field>
      <Field label="校验模板" className="col-6">
        <select className="form-select form-select-sm" value={value.validationKey}
          onChange={(e) => set('validationKey', e.target.value)}>
          {catalog.validationKeys.map((item) => <option key={item} value={item}>{labelWithCode(labels.validationKeys, item)}</option>)}
        </select>
      </Field>
      <Field label="启用" className="col-12">
        <div className="form-check">
          <input id="rule-enabled" className="form-check-input" type="checkbox" checked={value.enabled}
            onChange={(e) => set('enabled', e.target.checked)} />
          <label className="form-check-label" htmlFor="rule-enabled">启用该规则</label>
        </div>
      </Field>
      <Field label="参数（按校验模板 Schema 编辑）" className="col-12">
        <StructuredParamsEditor
          hint={`校验模板：${value.validationKey}${paramSchema && paramSchema.rootKeys.length > 0 ? `（根键 ${paramSchema.rootKeys.join(' / ')}）` : '（未登记 Schema，禁止携带参数）'}`}
          rootKeys={paramSchema?.rootKeys ?? []}
          json={value.params ?? null}
          emptyHint="该校验模板不允许配置参数。"
          onChange={(json) => set('params', json)}
        />
      </Field>
      <Field label="失败文案（可选）" className="col-6">
        <input className="form-control form-control-sm" value={value.message ?? ''}
          onChange={(e) => set('message', e.target.value || null)} />
      </Field>
      <Field label="溯源（可选）" className="col-6">
        <input className="form-control form-control-sm" value={value.sourceRef ?? ''}
          onChange={(e) => set('sourceRef', e.target.value || null)} />
      </Field>
      <Field label="说明（可选）" className="col-12">
        <input className="form-control form-control-sm" value={value.remark ?? ''}
          onChange={(e) => set('remark', e.target.value || null)} />
      </Field>
    </div>
  )
}

function ReverseEditor({
  kinds,
  lookup,
  descriptions,
  json,
  onChange,
}: {
  kinds: string[]
  lookup: (code: string | null | undefined) => string
  descriptions?: Record<string, string> | null
  json: string | null
  onChange: (json: string | null) => void
}) {
  const parsed = useMemo(() => {
    if (!json) return { kind: 'auto-reverse' as string, note: '' as string }
    try {
      const value = JSON.parse(json)
      return {
        kind: typeof value?.kind === 'string' ? value.kind : 'auto-reverse',
        note: typeof value?.note === 'string' ? value.note : '',
      }
    } catch {
      return { kind: 'auto-reverse', note: '' }
    }
  }, [json])
  const options = (kinds ?? []).length > 0 ? kinds : ['auto-reverse', 'no-reverse', 'recompute', 'reverse-flow', 'snapshot']
  const commit = (kind: string, note: string) => {
    if (!kind) {
      onChange(null)
      return
    }
    onChange(JSON.stringify(note ? { kind, note } : { kind }))
  }
  return (
    <div className="row g-2">
      <div className="col-4">
        <select className="form-select form-select-sm" value={parsed.kind}
          onChange={(event) => commit(event.target.value, parsed.note)}>
          <option value="">（无反向配置）</option>
          {options.map((kind) => (
            <option key={kind} value={kind} title={descriptions?.[kind] ?? undefined}>
              {labelWithCode(lookup, kind)}
            </option>
          ))}
        </select>
      </div>
      <div className="col-8">
        <input className="form-control form-control-sm" value={parsed.note} placeholder="反向说明（可选）"
          onChange={(event) => commit(parsed.kind, event.target.value)} />
      </div>
      {descriptions?.[parsed.kind] ? (
        <div className="col-12 small text-secondary">{descriptions[parsed.kind]}</div>
      ) : null}
    </div>
  )
}

function Field({
  label,
  className = 'col-12',
  children,
}: {
  label: string
  className?: string
  children: ReactNode
}) {
  return (
    <div className={className}>
      <label className="form-label small mb-1">{label}</label>
      {children}
    </div>
  )
}

function JsonArea({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return (
    <textarea
      className="form-control form-control-sm font-monospace"
      rows={3}
      spellCheck={false}
      value={value}
      onChange={(event) => onChange(event.target.value)}
      placeholder="{}"
    />
  )
}

/** 表标识：描述(表名)；缺描述时退化为表名，表名为空显示占位符。 */
function tableTitle(table: string | null, description?: string | null): string {
  const tableId = (table ?? '').trim()
  if (tableId === '') return '—'
  return `${(description ?? '').trim() || tableId}(${tableId})`
}
