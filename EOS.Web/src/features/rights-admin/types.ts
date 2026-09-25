export type RightsSource = 'personal' | 'group' | 'none' | 'default_open'

export interface EffectiveModuleRights {
  source: RightsSource
  canBrowse: boolean
  execTag: string
  addNew: boolean
  edit: boolean
  delete: boolean
  approve: boolean
  deapprove: boolean
  report: boolean
  cost: boolean
  setup: boolean
  moduleConfig: boolean
  formDesign: boolean
  secrecy: boolean
  endCase: boolean
  unEndCase: boolean
  other1: boolean
  other2: boolean
  other3: boolean
  other4: boolean
  fileView: boolean
  fileUpda: boolean
  fileEdit: boolean
  fileDele: boolean
  denyViewMaster: string[]
  denyViewDetail: string[]
  denyNewMaster: string[]
  denyNewDetail: string[]
  denyModiMaster: string[]
  denyModiDetail: string[]
  dataFilter: string
}

export interface ModuleRightsRow {
  moduleId: number
  title: string
  groupPath: string
  icon?: string | null
  parentId: number
  rootId: number
  sortIndex: number
  execTag: string | null
  addNew: boolean
  edit: boolean
  delete: boolean
  approve: boolean
  deapprove: boolean
  report: boolean
  cost: boolean
  setup: boolean
  moduleConfig: boolean
  formDesign: boolean
  secrecy: boolean
  endCase: boolean
  unEndCase: boolean
  other1: boolean
  other2: boolean
  other3: boolean
  other4: boolean
  fileView: boolean
  fileUpda: boolean
  fileEdit: boolean
  fileDele: boolean
  denyViewMaster: string
  denyViewDetail: string
  denyNewMaster: string
  denyNewDetail: string
  denyModiMaster: string
  denyModiDetail: string
  dataFilter: string
  hasPersonal: boolean
  effective: EffectiveModuleRights
}

export interface ModuleRightsInput {
  moduleId: number
  execTag: string | null
  addNew: boolean
  edit: boolean
  delete: boolean
  approve: boolean
  deapprove: boolean
  report: boolean
  cost: boolean
  setup: boolean
  moduleConfig: boolean
  formDesign: boolean
  secrecy: boolean
  endCase: boolean
  unEndCase: boolean
  other1: boolean
  other2: boolean
  other3: boolean
  other4: boolean
  fileView: boolean
  fileUpda: boolean
  fileEdit: boolean
  fileDele: boolean
  denyViewMaster: string | null
  denyViewDetail: string | null
  denyNewMaster: string | null
  denyNewDetail: string | null
  denyModiMaster: string | null
  denyModiDetail: string | null
  dataFilter: string | null
}

export interface EffectiveReportRights {
  source: RightsSource
  preview: boolean
  print: boolean
  export: boolean
  dataFilter: string
}

export interface ReportRightsRow {
  moduleId: number
  moduleTitle: string
  reportId: string
  reportName: string
  preview: boolean
  print: boolean
  export: boolean
  dataFilter: string
  hasPersonal: boolean
  effective: EffectiveReportRights
}

export interface ReportRightsInput {
  moduleId: number
  reportId: string
  preview: boolean
  print: boolean
  export: boolean
  dataFilter: string | null
}

export interface UserGroupSummary {
  groupId: string
  groupDescription: string
  memberCount: number
  remark?: string | null
}

export interface GroupMemberSummary {
  userId: string
  employeeId: string
  employeeName: string
}

export interface UserGroupItem {
  groupId: string
  groupDescription: string
}

export interface RightsFieldInfo {
  fieldId: string
  description: string
  isCost: boolean
  isSecrecy: boolean
}

export interface RightsModuleFields {
  masterTable: string
  detailTable: string | null
  masterFields: RightsFieldInfo[]
  detailFields: RightsFieldInfo[]
}

export interface EffectiveRightsDetail {
  source: RightsSource
  execTag: string
  canBrowse: boolean
  canAddNew: boolean
  canEdit: boolean
  canDelete: boolean
  canViewCost: boolean
  canViewSecrecy: boolean
  canSetup: boolean
  deniedMasterFields: string[]
  deniedDetailFields: string[]
  denyNewMasterFields: string[]
  denyNewDetailFields: string[]
  denyModiMasterFields: string[]
  denyModiDetailFields: string[]
  dataFilter: string
}

export type DenyFieldKey = 'denyViewMaster' | 'denyViewDetail' | 'denyNewMaster' | 'denyNewDetail' | 'denyModiMaster' | 'denyModiDetail'

export const BASIC_RIGHTS: { key: keyof Omit<ModuleRightsInput, 'moduleId' | 'execTag' | 'denyViewMaster' | 'denyViewDetail' | 'denyNewMaster' | 'denyNewDetail' | 'denyModiMaster' | 'denyModiDetail' | 'dataFilter'>; label: string }[] = [
  { key: 'addNew', label: '新增' },
  { key: 'edit', label: '编辑' },
  { key: 'delete', label: '删除' },
  { key: 'approve', label: '审批' },
  { key: 'deapprove', label: '解批' },
  { key: 'report', label: '报表' },
  { key: 'cost', label: '成本' },
  { key: 'setup', label: '设置' },
  { key: 'moduleConfig', label: '模块配置' },
  { key: 'formDesign', label: '表单设计' },
  { key: 'secrecy', label: '保密' },
  { key: 'endCase', label: '结案' },
  { key: 'unEndCase', label: '未结案' },
]

export const FILE_RIGHTS: { key: 'fileView' | 'fileUpda' | 'fileEdit' | 'fileDele'; label: string }[] = [
  { key: 'fileView', label: '查看文档' },
  { key: 'fileUpda', label: '上传文档' },
  { key: 'fileEdit', label: '编辑文档' },
  { key: 'fileDele', label: '删除文档' },
]

export const DENY_FIELDS: { key: DenyFieldKey; label: string }[] = [
  { key: 'denyViewMaster', label: '主表禁止查看' },
  { key: 'denyViewDetail', label: '副表禁止查看' },
  { key: 'denyNewMaster', label: '主表禁止新增' },
  { key: 'denyNewDetail', label: '副表禁止新增' },
  { key: 'denyModiMaster', label: '主表禁止修改' },
  { key: 'denyModiDetail', label: '副表禁止修改' },
]

export function emptyModuleInput(moduleId: number): ModuleRightsInput {
  return {
    moduleId,
    execTag: null,
    addNew: false,
    edit: false,
    delete: false,
    approve: false,
    deapprove: false,
    report: false,
    cost: false,
    setup: false,
    moduleConfig: false,
    formDesign: false,
    secrecy: false,
    endCase: false,
    unEndCase: false,
    other1: false,
    other2: false,
    other3: false,
    other4: false,
    fileView: false,
    fileUpda: false,
    fileEdit: false,
    fileDele: false,
    denyViewMaster: null,
    denyViewDetail: null,
    denyNewMaster: null,
    denyNewDetail: null,
    denyModiMaster: null,
    denyModiDetail: null,
    dataFilter: null,
  }
}

export function rowToInput(row: ModuleRightsRow): ModuleRightsInput {
  return {
    moduleId: row.moduleId,
    execTag: row.execTag,
    addNew: row.addNew,
    edit: row.edit,
    delete: row.delete,
    approve: row.approve,
    deapprove: row.deapprove,
    report: row.report,
    cost: row.cost,
    setup: row.setup,
    moduleConfig: row.moduleConfig,
    formDesign: row.formDesign,
    secrecy: row.secrecy,
    endCase: row.endCase,
    unEndCase: row.unEndCase,
    other1: row.other1,
    other2: row.other2,
    other3: row.other3,
    other4: row.other4,
    fileView: row.fileView,
    fileUpda: row.fileUpda,
    fileEdit: row.fileEdit,
    fileDele: row.fileDele,
    denyViewMaster: row.denyViewMaster || null,
    denyViewDetail: row.denyViewDetail || null,
    denyNewMaster: row.denyNewMaster || null,
    denyNewDetail: row.denyNewDetail || null,
    denyModiMaster: row.denyModiMaster || null,
    denyModiDetail: row.denyModiDetail || null,
    dataFilter: row.dataFilter || null,
  }
}
