/** 表单选择器数据源（与 EOS.API FieldChooserSource 对应；filter 受控解析前恒为 null，不返回普通用户） */
export interface FormChooserSource {
  active: boolean
  table: string | null
  description: string | null
  moduleId: number | null
  filter: string | null
  returnMapping: string | null
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
