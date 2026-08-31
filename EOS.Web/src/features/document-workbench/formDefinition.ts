/**
 * 表单选择器数据源（与 EOS.API FieldChooserSource 对应；ADR-008）：
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
}

/** 下拉选项（解析自 FIELDS.FORM_OPTIONS，如 'O=外含税;I=内含税'） */
export interface FormOptionItem {
  value: string
  label: string
}

/** 工作台/表单业务按钮（解析自 MODULES.FORM_BUTTONS，如 '1=copy;2=approve;3=print'） */
export interface WorkbenchButton {
  action: string
}

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
   /** 数值精度/小数位（sys.types，仅 decimal/numeric；ADR-006 决策 2.4 超精度校验依据） */
   precision?: number | null
   scale?: number | null
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
  /** 模块是否具备批核工作流（MODULES.UPDATE_SP → ModuleBusinessMap.WorkflowSproc 非空） */
  hasWorkflow: boolean
  /** 模块允许复制（MODULES.IF_COPY） */
  ifCopy: boolean
  /** 模块纳入通用查询（MODULES.SEARCH_1/SEARCH_2） */
  searchMaster: boolean
  searchDetail: boolean
  /** 新增模式服务端默认值（单别/单号/日期等），edit 模式为空对象 */
  defaultValues: Record<string, string>
   /** 删除/批核/结案/附件操作权限（服务端 LegacyModuleRights 下发，视图按位显隐） */
   canDelete: boolean
   canApprove: boolean
   canDeapprove: boolean
   canEndCase: boolean
   canUnEndCase: boolean
   canFileView: boolean
   canFileUpda: boolean
   canFileEdit: boolean
   canFileDele: boolean
   /** 新增/编辑用户权限（ADR-006 决策 6 浏览态按钮显隐：模块能力 ∧ 用户权限） */
   canAddNew: boolean
   canEdit: boolean
   /** 帮助页地址（MODULES.HELP_URL，非空时浏览态显示帮助按钮） */
   helpUrl?: string | null
   /** 字段设置权限（2302 字段维护 CanSetup）：为 true 时表单标签右键可进入字段设置页 */
   canSetup: boolean
   }

/** 模块权限（与 EOS.API LegacyModuleRights 对应，M0 扩展后） */
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
