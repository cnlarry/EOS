import { apiClient } from '../../services/api'

/**
 * 产品 BOM（模块 1204）三个结构视图的数据层：多层展开、毛需求、反查用途。
 *
 * 三个端点都是「视图」而不是独立模块（ADR-039 D10），权限门槛统一取 1204 的浏览权，
 * 所以这里不传模块号、也不按模块号做前端权限判断——权限由路由层的 RequirePermission 与
 * 服务端各自把关，前端只是调用方。
 */

/** 结构树的一行。`edition` 是这一层实际用的产品 BOM 版次，`isFallback` 表示它是回退来的。 */
export interface BomTreeRow {
  level: number
  proNo: string
  edition: string
  qtyPer: number
  qtyCum: number
  lostRate: number
  lostFactor: number
  /** 该子件在父件 BOM 明细里的项次。 */
  serialNo: number
  /** 该明细行的底数（用量按"每 BASE_QTY 个父件"计）。 */
  baseQty: number
  /** 该明细行的备注。 */
  remark: string
  /**
   * 该料号**自己的** BOM 版次（它作为父件时选中的版次）；没有下阶 BOM 时为空串。
   * 与 `edition`（父件那条 BOM 的版次）不是一回事。
   */
  ownEdition: string
  path: string
  proName: string
  proSpec: string
  unitId: string
  /** 产品性质：1 成品 / 2 半成品 / 3 材料 / 4 辅料。 */
  proType: string
  /** 主要来源：1 采购 / 2 自制 / 3 外包。 */
  mainSource: string
  /** 现有库存数量。 */
  stockQty: number
  /** 是否已停止使用。 */
  stopTag: boolean
  hasChildren: boolean
  isFallback: boolean
}

export interface BomTreeResponse {
  proNo: string
  /** 根节点的品名：它不在任何明细行里，由服务端单独带出。 */
  proName: string
  edition: string
  /** 该产品的最新版次：用来提示"你正在看过期版"。 */
  latestEdition: string
  /** 该产品的全部版次（升序）：树页顶部的切换控件用它。 */
  editions: string[]
  maxDepth: number
  rows: BomTreeRow[]
}

/** 毛需求的一行（叶子按料号合并后的结果，不含层级）。 */
export interface BomGrossRow {
  proNo: string
  qtyPerUnit: number
  qtyPerUnitWithLoss: number
  lostQtyPerUnit: number
  proName: string
  proSpec: string
  unitId: string
}

export interface BomGrossResponse {
  proNo: string
  edition: string
  qty: number
  maxDepth: number
  rows: BomGrossRow[]
}

/** 反查用途的一行：用到了目标料号的一个父件。 */
export interface BomWhereUsedRow {
  level: number
  parentNo: string
  parentEdition: string
  serialNo: number
  qtyPer: number
  path: string
  parentName: string
  parentSpec: string
}

export interface BomWhereUsedResponse {
  proNo: string
  maxDepth: number
  rows: BomWhereUsedRow[]
}

/** 多层展开。版次留空表示取该料号的最大版次。 */
export function expandBom(proNo: string, edition?: string, maxDepth?: number, signal?: AbortSignal) {
  return apiClient.get<BomTreeResponse>('/bom/expand', {
    query: { proNo, edition: edition || undefined, maxDepth },
    signal,
  })
}

/** 毛需求。`qty` 是制令数量，服务端按它折算。 */
export function grossRequirement(proNo: string, edition?: string, qty?: number, maxDepth?: number) {
  return apiClient.get<BomGrossResponse>('/bom/gross-requirement', {
    query: { proNo, edition: edition || undefined, qty, maxDepth },
  })
}

/** 反查用途：给定料号，向上找用它的父件。 */
export function whereUsedBom(proNo: string, edition?: string, maxDepth?: number, signal?: AbortSignal) {
  return apiClient.get<BomWhereUsedResponse>('/bom/where-used', {
    query: { proNo, edition: edition || undefined, maxDepth },
    signal,
  })
}

/** 版次格式与元数据同口径：两位数字。留空表示"取最大版次"。 */
export const EDITION_PATTERN = /^\d{2}$/

/** 导入的一行明细；项次由服务端分配，不在这一层出现。 */
export interface BomImportRow {
  elementProNo: string
  /** 子件自己的 BOM 版次：半成品必填、材料留空（判据在服务端）。 */
  elementEdition: string | null
  qty: number
  baseQty: number
  lostRate: number
  remark: string | null
}

/** 取试算表：`replace` 为真时先清空该版次明细再插入。 */
export function importBomLines(proNo: string, edition: string, replace: boolean, rows: BomImportRow[]) {
  return apiClient.post<{ imported: number; replaced: boolean }>('/bom/import-lines', {
    proNo,
    edition,
    replace,
    rows,
  })
}

/**
 * 解析试算表 CSV：列约定「料号, 用量, 底数, 损耗率, 备注」，首行含"料号"二字视为表头跳过。
 *
 * 逐行报错而不是"整体失败"：试算表最常见的毛病是某一行数字带中文单位或空着，
 * 用户需要知道改哪一行，而不是重填整张表。
 */
export function parseBomCsv(text: string): { rows: BomImportRow[]; errors: string[] } {
  const rows: BomImportRow[] = []
  const errors: string[] = []
  const table = splitCsv(text)

  for (let index = 0; index < table.length; index++) {
    const cells = table[index].map(cell => cell.trim())
    if (cells.every(cell => cell === '')) continue
    if (index === 0 && cells[0].includes('料号')) continue

    const elementProNo = cells[0] ?? ''
    if (elementProNo === '') {
      errors.push(`第 ${index + 1} 行：缺少子件料号`)
      continue
    }
    // 子件版次：格式只在这里拦一道，**必填与否交给服务端**——
    // 前端不知道这个料有没有下阶 BOM，猜必填会把材料行全部误判
    const elementEdition = cells[1] ?? ''
    if (elementEdition !== '' && !/^\d{2}$/.test(elementEdition)) {
      errors.push(`第 ${index + 1} 行：子件版次「${elementEdition}」不是两位数字`)
      continue
    }
    const qty = toNumber(cells[2])
    const baseQty = toNumber(cells[3])
    const lostRate = toNumber(cells[4])
    if (qty === null) errors.push(`第 ${index + 1} 行：用量「${cells[2] ?? ''}」不是数字`)
    if (baseQty === null) errors.push(`第 ${index + 1} 行：底数「${cells[3] ?? ''}」不是数字`)
    if (lostRate === null) errors.push(`第 ${index + 1} 行：损耗率「${cells[4] ?? ''}」不是数字`)
    if (qty === null || baseQty === null || lostRate === null) continue

    rows.push({
      elementProNo,
      elementEdition: elementEdition === '' ? null : elementEdition,
      qty,
      baseQty: baseQty === 0 ? 1 : baseQty,
      lostRate,
      remark: cells[5] ? cells[5] : null,
    })
  }
  return { rows, errors }
}

/** 极简 CSV 切分：支持引号包裹与 `""` 转义，够读 Excel 另存的表。 */
function splitCsv(text: string): string[][] {
  const table: string[][] = []
  let row: string[] = []
  let cell = ''
  let quoted = false
  for (let index = 0; index < text.length; index++) {
    const character = text[index]
    if (quoted) {
      if (character === '"') {
        if (text[index + 1] === '"') {
          cell += '"'
          index++
        } else {
          quoted = false
        }
      } else {
        cell += character
      }
      continue
    }
    if (character === '"') quoted = true
    else if (character === ',') {
      row.push(cell)
      cell = ''
    } else if (character === '\n') {
      row.push(cell)
      table.push(row)
      row = []
      cell = ''
    } else if (character !== '\r') cell += character
  }
  if (cell !== '' || row.length > 0) {
    row.push(cell)
    table.push(row)
  }
  return table
}

function toNumber(value: string | undefined): number | null {
  if (value === undefined || value.trim() === '') return null
  const numeric = Number(value.trim())
  return Number.isFinite(numeric) ? numeric : null
}

/** 版本对比的一行：按子件对齐，某侧为 null 表示该版次没有这个子件。 */
export interface BomComparisonRow {
  proNo: string
  serialNoA: number | null
  serialNoB: number | null
  /** 子件自己的 BOM 版次（A 版 / B 版）。只换版次时 `status=changed`，靠这两列看出改了什么。 */
  elementEditionA: string | null
  elementEditionB: string | null
  qtyA: number | null
  qtyB: number | null
  baseQtyA: number | null
  baseQtyB: number | null
  lostRateA: number | null
  lostRateB: number | null
  remarkA: string | null
  remarkB: string | null
  /** added = 只在新版、removed = 只在旧版、changed = 两版都有但值不同、same = 一致 */
  status: 'added' | 'removed' | 'changed' | 'same'
}

export interface BomComparison {
  proNo: string
  editionA: string
  editionB: string
  rows: BomComparisonRow[]
}

/** 版本对比：比的是原始列（用量/底数/损耗率/备注），不是折算后的单件用量。 */
export function compareBom(proNo: string, editionA: string, editionB: string, signal?: AbortSignal) {
  return apiClient.get<BomComparison>('/bom/compare', {
    query: { proNo, editionA, editionB },
    signal,
  })
}

/**
 * 新建版本：把当前版次的整棵结构复制到下一版次。
 *
 * 只提交"复制哪一版"，不提交"复制成哪一版"——版次是主键，由服务端算。
 * 这是模块的标配能力（判定是 `AddNew` 权限），不是可配置按钮。
 */
export function createBomVersion(proNo: string, edition: string) {
  return apiClient.post<{ proNo: string; edition: string }>('/bom/new-version', { proNo, edition })
}
