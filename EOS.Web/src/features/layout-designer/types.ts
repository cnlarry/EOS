// layout.json schema v1 类型（与 EOS.API/Models/FormatPackageModels.cs 对齐）

export interface LayoutMargin {
  top: number
  right: number
  bottom: number
  left: number
}

export interface LayoutPage {
  size: string
  orientation: string
  margin: LayoutMargin
}

export interface LayoutElementStyle {
  fontSize?: number
  bold?: boolean
  semiBold?: boolean
  italic?: boolean
  align?: string
  color?: string
  backgroundColor?: string
  borderColor?: string
  borderWidth?: number
  padding?: number
  lineWidth?: number
}

export interface LayoutColumn {
  field: string
  label: string
  width?: number
  align?: string
  format?: string
  suffix?: string
  isAmount?: boolean
}

export interface LayoutElement {
  id: string
  type: 'text' | 'field' | 'image' | 'line' | 'rect' | 'table'
  x: number
  y: number
  w: number
  h: number
  visible?: boolean
  content?: string
  field?: string
  format?: string
  resourceId?: string
  dataSource?: string
  columns?: LayoutColumn[]
  showHeader?: boolean
  repeatHeaderOnPageBreak?: boolean
  showTotals?: boolean
  totalsLabel?: string
  totalsField?: string
  maxRows?: number
  title?: string
  style?: LayoutElementStyle
}

export interface LayoutSection {
  height?: number
  elements: LayoutElement[]
}

export interface LayoutDocument {
  schemaVersion: number
  kind: string
  page: LayoutPage
  sections: {
    header: LayoutSection
    content: LayoutSection
    footer: LayoutSection
  }
}

export interface ContractColumn {
  key: string
  label: string
  type: string
  width?: number
}

export interface DataContract {
  columns: ContractColumn[]
  detailColumns: ContractColumn[]
}

export interface DesignerMode {
  canDesign: boolean
  canAdjust: boolean
}

export interface DesignerDefinition {
  formatId: string
  title: string
  isCustom: boolean
  layoutId: number | null
  mode: DesignerMode
  layoutJson: string
  dataContract: DataContract
  systemFields: string[]
  headerId: string | null
  tailId: string | null
  printPrice: boolean | null
}

export interface LayoutHeaderOption {
  headerId: string
  name: string
  company: string | null
  companyEn: string | null
  headerText: string | null
  logoPath: string | null
}
