/**
 * 表单设计态的数据形状（与后端 /api/v1/admin/form-layout 契约一一对应）。
 *
 * 版式是**模块级**的：同一个字段在不同模块里可以排在不同的位置、藏或不藏；
 * 页签集合也属于模块。设计态只改版式，不改字段定义（标签/类型/必填/格式仍在字段维护）。
 */

export interface DesignTab {
  no: number
  title: string
}

/** 版式行：一行 = 一个字段在某张表（模块主表或明细表）表单上的位置与占位。 */
export interface DesignRow {
  key: string
  label: string
  dataType: string
  tabNo: number
  orderNo: number
  span: number
  rowSpan: number
  newLine: boolean
  sectionId: string | null
  cellGroup: string | null
  cellRole: number
  hidden: boolean
  /** 不可移除：主键、单据系统列、用户可填的必填列（服务端保存期同样拒绝）。 */
  locked: boolean
  lockReason: string | null
  /** 当前用户在该字段上是否可见（成本/保密/禁止字段在池中仍列出，但带锁图标）。 */
  userVisible: boolean
  required: boolean
  isPrimaryKey: boolean
  hasChooser: boolean
  isVirtual: boolean
}

/** 字段池条目：已注册但未排进本模块表单的字段。 */
export interface PoolField {
  key: string
  label: string
  dataType: string
  userVisible: boolean
  required: boolean
  isPrimaryKey: boolean
  hasChooser: boolean
  isVirtual: boolean
  locked: boolean
  lockReason: string | null
}

export interface TableDesign {
  table: string
  layout: DesignRow[]
  pool: PoolField[]
}

export interface DesignState {
  moduleId: number
  title: string
  masterTable: string
  detailTable: string | null
  columns: number
  tabs: DesignTab[]
  master: TableDesign
  detail: TableDesign
  baseUpdatedAt: string | null
}

export interface TabInput {
  no: number
  title: string
}

export interface RowInput {
  key: string
  tabNo: number
  span: number
  rowSpan: number
  newLine: boolean
  sectionId: string | null
  cellGroup: string | null
  cellRole: number
  hidden: boolean
}

export interface DetailRowInput {
  key: string
  hidden: boolean
}

export interface SavePayload {
  baseUpdatedAt: string | null
  idempotencyKey: string
  tabs: TabInput[]
  master: RowInput[]
  detail: DetailRowInput[]
}

export interface SaveResponse {
  status: string
  message: string | null
  definitionVersion: string | null
  state: DesignState | null
}

export interface TemplateOption {
  moduleId: number
  title: string
  masterTable: string
  columns: number
  hasDetail: boolean
}

/** 本地草稿：字段池与版式行在同一份结构里编辑，保存时整份提交（全量替换）。 */
export interface DesignDraft {
  columns: number
  masterTable: string
  detailTable: string | null
  tabs: DesignTab[]
  master: DesignRow[]
  masterPool: PoolField[]
  detail: DesignRow[]
  detailPool: PoolField[]
  /** 加载时的原始版式，供"恢复该字段默认"与"放弃修改"使用。 */
  baseline: {
    tabs: DesignTab[]
    master: DesignRow[]
    detail: DesignRow[]
  }
}

export type DesignTable = 'master' | 'detail'
