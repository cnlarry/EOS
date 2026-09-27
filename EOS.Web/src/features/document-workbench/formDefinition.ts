/**
 * 表单选择器数据源（与 EOS.API FieldChooserSource 对应）：
 * - filter 恒为 null（FILTER_STRUCT 仅字段设置可见，运行期由 form-chooser 端点按 serialNo 取权威定义）；
 * - returnMapping = RETURN_ITEMS JSON（有序回填映射，[{target,column}]）；
 * - serialNo：多来源「各是各的入口」时传给 form-chooser 指定来源。
 */
export interface FormChooserSource {
  active: boolean
  table: string | null
  description: string | null
  moduleId: number | null
  filter: string | null
  returnMapping: string | null
  serialNo: number | null
  /**
   * 服务端注册数据源的键（非空时该来源用统一选择器的 sourceKey 分支取数）：
   * 列与排序由服务端注册表给出，前端只传过滤值。
   */
  sourceKey?: string | null
}

/**
 * 下拉选项（解析自 FIELDS.FORM_OPTIONS，如 'O=外含税;I=内含税'）。
 * 标签以 `!` 结尾的项为**可见但不可选**（未实现的档位要让人看得见、但选不了），服务端解析为 disabled。
 */
export interface FormOptionItem {
  value: string
  label: string
  disabled?: boolean
}

import type { DocumentActionMeta } from './documentActionRunner'

/** 工作台/表单业务按钮（解析自 MODULES.FORM_BUTTONS，如 '1=copy;2=approve;3=print'） */
export interface WorkbenchButton {
  action: string
}

/** 自定义按钮（单据操作）的元数据；类型与执行器共用一份定义（此处再导出，页面只依赖本文件）。 */
export type { DocumentActionMeta }

/** 表单页签（解析自 MODULES.FORM_TABS，如 '1=基本资料;2=其它'） */
export interface FormTab {
  no: number
  title: string
}

/** 单个录入字段（GET /api/document-workbench/{moduleId}/form-definition 返回项） */
export interface FormFieldDefinition {
  key: string
  label: string
  dataType: string
  displayLength: number
  displayFormat: string | null
  isRequired: boolean
  verifyIndex: number | null
  regex: string | null
  defaultValue: string | null
  isReadonly: boolean
  isVisible: boolean
  onlyChoose: boolean
  chooseMultiple: boolean
  choosePage: string | null
  choosers: FormChooserSource[]
  isPrimaryKey: boolean
  isAutoIncrement: boolean
  isVirtual: boolean
  isCost: boolean
  isSecrecy: boolean
  serverFilled: boolean
   maxLength: number | null
   tabNo: number
   formOrder: number | null
   span: number
   newLine: boolean
   cellGroup: string | null
   cellRole: number
   options: FormOptionItem[]
   displayOnly: boolean
   /** 复制（IF_COPY）时是否带出该字段值（FIELDS.CAN_COPY，默认 true） */
   canCopy: boolean
   /** 数值精度/小数位（sys.types，仅 decimal/numeric； 超精度校验依据） */
   precision?: number | null
   scale?: number | null
   /** 行跨度（模块级版式：1..3） */
   rowSpan?: number | null
   /** 所属分节（模块级版式；与复合格解耦） */
   sectionId?: string | null
   }

/** 统一表单定义（按当前用户权限过滤后的录入视图） */
export interface FormDefinition {
  moduleId: number
  title: string
  masterTable: string
  detailTable: string | null
  hasAdd: boolean
  hasEdit: boolean
  mode: 'new' | 'edit'
  masterFields: FormFieldDefinition[]
  detailFields: FormFieldDefinition[]
  masterPkOrder: string[]
  detailNoFields: string
  detailDfVerify: string
  tabs: FormTab[]
  columns: number
  buttons: WorkbenchButton[] | null
  /** 模块是否具备批核工作流（效果引擎接管 / 已配置流程 / 自动批核） */
  hasWorkflow: boolean
  /** 无副作用批核能力（自动批核且无过程/效果链/流程定义）：批核/解批按钮据此与 hasWorkflow 取并集显隐 */
  hasStatelessApprove: boolean
  /**
   * 批核/解批入口能力：流程 / 遗留批核过程 / 效果链 / 无副作用自动批核四者取并集（服务端判定）。
   * 效果链接管的模块没有过程也没有流程，只有这个标志为真，按钮必须据此显示。
   */
  hasApproveCapability?: boolean
  /** 模块允许复制（MODULES.IF_COPY） */
  ifCopy: boolean
  /** 模块纳入通用查询（MODULES.SEARCH_1/SEARCH_2） */
  searchMaster: boolean
  searchDetail: boolean
  /** 新增模式服务端默认值（单别/单号/日期等），edit 模式为空对象 */
  defaultValues: Record<string, string>
   /** 删除/批核/结案/附件操作权限（服务端 ModuleRights 下发，视图按位显隐） */
   canDelete: boolean
   canApprove: boolean
   canDeapprove: boolean
   canEndCase: boolean
   canUnEndCase: boolean
   canFileView: boolean
   canFileUpda: boolean
   canFileEdit: boolean
   canFileDele: boolean
   /** 新增/编辑用户权限 */
   canAddNew: boolean
   canEdit: boolean
   /** 帮助页地址（MODULES.HELP_URL，非空时浏览态显示帮助按钮） */
   helpUrl?: string | null
   /** 字段设置权限（2302 字段维护 CanSetup）：为 true 时表单标签右键可进入字段设置页 */
   canSetup: boolean
   /** 版式设计权（FORM_DESIGN_TAG）：为 true 时表单标签右键出现【表单设计】；服务端写端点独立鉴权 */
   canFormDesign: boolean
   /**
    * 自定义按钮（服务端随定义下发的、当前用户已获授权的单据操作）。
    * 未授权的操作不会出现在这里——按钮是"不存在"，不是"禁用"；服务端对每次点击仍独立鉴权。
    */
   userActions?: DocumentActionMeta[] | null
   }

/** 模块权限（与 EOS.API ModuleRights 对应，M0 扩展后） */
export interface ModuleRights {
  canBrowse: boolean
  canViewCost: boolean
  canViewSecrecy: boolean
  canSetup: boolean
  deniedMasterFields: string[]
  deniedDetailFields: string[]
  canAddNew: boolean
  canEdit: boolean
  canDelete: boolean
  denyNewMasterFields: string[]
  denyNewDetailFields: string[]
  denyModiMasterFields: string[]
  denyModiDetailFields: string[]
  dataFilter: string
}
