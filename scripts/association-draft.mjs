/**
 * 关联键草案生成：对简单显示型虚拟字段，推断 JOIN 条件（源表主键 = 当前表对应列）。
 * 输入：logs/_vexp-simple.tsv（T|F|SRC|COL）、logs/columns-config.tsv（物理列）
 * 输出：logs/association-whitelist-draft.md
 */
import { readFileSync, writeFileSync } from 'fs'

const simple = readFileSync('logs/_vexp-simple.tsv', 'utf8').split(/\r?\n/).filter(Boolean)
  .map(l => { const [t, f, s, c] = l.split('|'); return { t, f, s, c } })

// 物理列：表 → 列集合
const colsByTable = new Map()
for (const line of readFileSync('logs/columns-config.tsv', 'utf8').split(/\r?\n/)) {
  const [t, c] = line.split('|').map(x => x.trim().toUpperCase())
  if (!t || !c) continue
  if (!colsByTable.has(t)) colsByTable.set(t, new Set())
  colsByTable.get(t).add(c)
}

// 源表主键（手工补视图映射）
const pk = {
  PRODUCT: ['PRO_NO'], COLOR: ['COLOR_ID'], CLIENT: ['CLIENT_ID'], DEPT: ['DEPT_ID'],
  DEPT_M: ['DEPT_ID'], BILLKIND: ['BILL_CODE'], SYSDN: ['EMP_ID'], HR_EMPLOYEE: ['EMP_ID'],
  SUPPLIER: ['SUPPLIER_ID'], DEPOT: ['DEPOT_ID'], TAX: ['TAX_ID'], TAX_M: ['TAX_ID'],
  LINE: ['LINE_ID'], UNIT: ['UNIT_ID'], STUFF: ['STUFF_ID'], SFC_PROCEDURE: ['PROCEDURE_ID'],
  CUS_PRODUCT: ['PRO_ID'], MODULES: ['M_IDX'], SYSDN_FOLLOW: ['EMP_ID'], SYSDN_MOTOR: ['EMP_ID'],
  COLOR_M: ['COLOR_ID'], DEPOT_M: ['DEPOT_ID'], TAX_M2: ['TAX_ID'], UNIT_LWH: ['UNIT_ID'],
  REPORT_HEADER: ['HEADER_ID'], CURR: ['CURR_ID'], BANK: ['BANK_ID'], SFC_PROCEDURE_TYPE: ['PROCEDURE_TYPE_ID'],
  TYPE_FORMULA: ['TYPE_ID'], PAP_BARCODE_M: ['BARCODE_NO'], ACC_ACCOUNT: ['ACCOUNT_ID'],
  ACC_ACCOUNT_TYPE: ['TYPE_ID'], ACCOUNT_TYPE: ['ACCOUNT_TYPE_ID'], RECEIVE: ['RECEIVE_ID'],
  CAR: ['CAR_ID'], HR_DUTY: ['DUTY_ID'], MOU_TYPE: ['TYPE_ID'], MOU_OWNER: ['OWNER_ID'],
  SORT: ['SORT_ID'], COMPANY: ['COMPANY_ID'], PRODUCE_STUFF: ['STUFF_ID'], HALF_PRO: ['PRO_NO'],
  SAMPLE_PRO: ['PRO_NO'], PRODUCT_M: ['PRO_NO'], CUS_MANUAL_PRO: ['PRO_ID'], CUS_COUNTRY: ['COUNTRY_ID'],
  CUS_IMPOSE: ['IMPOSE_ID'], CUS_CUSTOMS: ['CUSTOMS_ID'], CUS_DEPOT: ['DEPOT_ID'],
  HR_DIMISSION: ['DIMISSION_ID'], HR_EVECTION: ['EVECTION_ID'], HR_LEAVE: ['LEAVE_ID'],
  HR_AMERCE: ['AMERCE_ID'], HR_AWARD: ['AWARD_ID'], HR_CERTIFY: ['CERTIFY_ID'], HR_BED: ['BED_ID'],
  HR_BLOOD: ['BLOOD_ID'], HR_DIPLOMA: ['DIPLOMA_ID'], HR_DORM: ['DORM_ID'], HR_GRADE: ['GRADE_ID'],
  HR_LANGUAGE: ['LANGUAGE_ID'], HR_NATION: ['NATION_ID'], HR_POLITY: ['POLITY_ID'],
  HR_POST: ['POST_ID'], HR_PROVINCE: ['PROVINCE_ID'], HR_TITLE: ['TITLE_ID'], HR_SAFE: ['SAFE_ID'],
  HR_WORKTYPE: ['WORKTYPE_ID'], SYSDG: ['G_IDX'], WFFORM: ['WF_M_IDX'], REPORT: ['REPORT_ID'],
  SYSQR_TYPE: ['TYPE_ID'], TABLES: ['T_ID'], FIELDS: ['T_ID', 'F_ID'], V_SYSDL_SYSDN: ['EMP_ID'],
  MOU_MOULD: ['MOULD_ID'], MOU_ASSESS_M: ['MOU_ASSESS_NO'], MOU_ACCEPT_M: ['ACCEPT_NO'],
  MOU_APPLY_M: ['APPLY_NO'], MOU_PRO_M: ['PRO_NO'], NOPE_TABLE: ['NOPE_ID'],
}

const specialLink = { // 源表 → 当前表列名（当主键同名/变体都找不到时）
  BILLKIND: ['BILL_TYPE', 'BILL_CODE', 'TYPE', 'BACK_CODE', 'GET_TYPE', 'CANCEL_TYPE'],
  PRODUCT: ['PRO_NO', 'ELEMENT_PRO_NO', 'CLIENT_PRO_NO', 'PRODUCE_NO'],
  HR_EMPLOYEE: ['EMP_ID', 'EMP_NO', 'MOTORMAN', 'LEADER_EMP_ID', 'PLAN_PERSON', 'PROCESS_MAN'],
  SYSDN: ['EMP_ID', 'EMP_NO', 'OWNER'],
  DEPT: ['DEPT_ID', 'SEND_DEPT_ID', 'RECEIVE_DEPT_ID', 'DEPT_ID1'],
  CLIENT: ['CLIENT_ID'],
  SUPPLIER: ['SUPPLIER_ID'],
  DEPOT: ['DEPOT_ID', 'DEPOT_ID1', 'IN_DEPOT', 'OUT_DEPOT'],
  COLOR: ['COLOR_ID'],
  TAX: ['TAX_ID'],
  LINE: ['LINE_ID', 'LINE_ID1'],
  UNIT: ['UNIT_ID', 'UNIT_ID_1', 'SIZE_UNIT_ID', 'LAST_PURCHASE_UNIT_ID'],
  STUFF: ['STUFF_ID', 'PRODUCE_STUFF_ID'],
  MODULES: ['M_IDX', 'R_M_IDX', 'Q_M_IDX', 'B_M_IDX'],
}

function findLink(t, s) {
  const cols = colsByTable.get(t) ?? new Set()
  const pks = pk[s] ?? []
  for (const p of pks) if (cols.has(p)) return p
  // 变体：当前表列以主键结尾（ELEMENT_PRO_NO → PRO_NO）
  for (const p of pks) {
    if (p.length < 3) continue
    const matches = [...cols].filter(c => c.endsWith(p) && c.length > p.length)
    if (matches.length) return matches.sort((a, b) => a.length - b.length)[0]
  }
  // 特殊映射
  const sp = specialLink[s]
  if (sp) for (const c of sp) if (cols.has(c)) return c
  return null
}

const bySource = new Map()
for (const r of simple) {
  if (!bySource.has(r.s)) bySource.set(r.s, { direct: 0, variant: 0, miss: 0, samples: [] })
  const g = bySource.get(r.s)
  const link = findLink(r.t, r.s)
  if (link === null) g.miss++
  else {
    const isDirect = (pk[r.s] ?? []).includes(link)
    if (isDirect) g.direct++; else g.variant++
    if (g.samples.length < 3) g.samples.push(`${r.t}.${r.f} → ${r.s}.${r.c} via ${r.t}.${link}`)
  }
}

const md = []
md.push('# 关联键草案（VIRTUAL_EXP 简单显示型 → JOIN 条件）')
md.push('')
md.push(`> 生成：2026-08-11。${simple.length} 条简单显示型按源表聚合，推断关联键 = 当前表列 == 源表主键。`)
md.push('> 状态：直接同名/变体推断（draft），需人工复核后固化为 JOIN 白名单。')
md.push('')
md.push('| 源表 | 主键 | 直接 | 变体 | 待定 | 样例（当前表.虚拟字段 → 源表.列 via 当前表.关联列） |')
md.push('|---|---|---|---|---|---|')
const order = [...bySource.entries()].sort((a, b) => (b[1].direct + b[1].variant) - (a[1].direct + a[1].variant))
for (const [s, g] of order) {
  md.push(`| ${s} | ${(pk[s] ?? []).join('/') || '?'} | ${g.direct} | ${g.variant} | ${g.miss} | ${g.samples.join('；')} |`)
}
const totalMiss = [...bySource.values()].reduce((a, g) => a + g.miss, 0)
md.push('')
md.push(`## 统计：推断成功 ${simple.length - totalMiss} / ${simple.length}，待定 ${totalMiss}`)
md.push('')
md.push('待定明细（无法自动推断关联列，需人工指定）：')
for (const [s, g] of bySource) {
  if (g.miss === 0) continue
  const misses = simple.filter(r => r.s === s && findLink(r.t, r.s) === null)
  for (const r of misses) md.push(`- ${r.t}.${r.f} = ${r.s}.${r.c}`)
}
writeFileSync('logs/association-whitelist-draft.md', md.join('\n'), 'utf8')
console.log(`草案已生成：成功 ${simple.length - totalMiss} / ${simple.length}，待定 ${totalMiss}`)
