/**
 * 固化关联白名单 + 生成待确认清单。
 * 输入：logs/_vexp-simple.tsv、logs/columns-config.tsv
 * 输出：logs/association-whitelist.tsv（已推断）、logs/association-pending.tsv（待确认）
 */
import { readFileSync, writeFileSync } from 'fs'

const simple = readFileSync('logs/_vexp-simple.tsv', 'utf8').split(/\r?\n/).filter(Boolean)
  .map(l => { const [t, f, s, c] = l.split('|'); return { t, f, s, c } })

const colsByTable = new Map()
for (const line of readFileSync('logs/columns-config.tsv', 'utf8').split(/\r?\n/)) {
  const [t, c] = line.split('|').map(x => x.trim().toUpperCase())
  if (!t || !c) continue
  if (!colsByTable.has(t)) colsByTable.set(t, new Set())
  colsByTable.get(t).add(c)
}

const pk = {
  PRODUCT: ['PRO_NO'], COLOR: ['COLOR_ID'], CLIENT: ['CLIENT_ID'], DEPT: ['DEPT_ID'],
  DEPT_M: ['DEPT_ID'], BILLKIND: ['BILL_CODE'], SYSDN: ['EMP_ID'], HR_EMPLOYEE: ['EMP_ID'],
  SUPPLIER: ['SUPPLIER_ID'], DEPOT: ['DEPOT_ID'], TAX: ['TAX_ID'], TAX_M: ['TAX_ID'],
  LINE: ['LINE_ID'], UNIT: ['UNIT_ID'], STUFF: ['STUFF_ID'], SFC_PROCEDURE: ['PROCEDURE_ID'],
  CUS_PRODUCT: ['PRO_ID'], MODULES: ['M_IDX'], SYSDN_FOLLOW: ['EMP_ID'], SYSDN_MOTOR: ['EMP_ID'],
  COLOR_M: ['COLOR_ID'], DEPOT_M: ['DEPOT_ID'], UNIT_LWH: ['UNIT_ID'],
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

function findLink(t, s) {
  const cols = colsByTable.get(t) ?? new Set()
  const pks = pk[s] ?? []
  for (const p of pks) if (cols.has(p)) return { col: p, kind: 'direct' }
  for (const p of pks) {
    if (p.length < 3) continue
    const matches = [...cols].filter(c => c.endsWith(p) && c.length > p.length)
    if (matches.length) return { col: matches.sort((a, b) => a.length - b.length)[0], kind: 'variant' }
  }
  return null
}

const wl = []
const pending = []
for (const r of simple) {
  const link = findLink(r.t, r.s)
  if (link) {
    wl.push(`${r.t}|${r.f}|${r.s}|${r.c}|${link.col}|${link.kind}`)
  } else {
    // 候选：当前表列名包含源表主键（取最短），供人工确认
    const pks = pk[r.s] ?? []
    const cands = []
    for (const p of pks) {
      if (p.length < 3) continue
      for (const c of (colsByTable.get(r.t) ?? [])) {
        if (c.includes(p) && c !== p) cands.push(c)
      }
    }
    pending.push(`${r.t}|${r.f}|${r.s}|${r.c}|${[...new Set(cands)].sort((a, b) => a.length - b.length).join(',')}`)
  }
}

writeFileSync('logs/association-whitelist.tsv', 'CURRENT_TABLE|VIRTUAL_FIELD|SOURCE_TABLE|SOURCE_COL|LINK_COL|KIND\n' + wl.join('\n'), 'utf8')
writeFileSync('logs/association-pending.tsv', 'CURRENT_TABLE|VIRTUAL_FIELD|SOURCE_TABLE|SOURCE_COL|CANDIDATE_LINK_COLS\n' + pending.join('\n'), 'utf8')
console.log(`白名单固化：${wl.length} 条；待确认：${pending.length} 条`)
const noCand = pending.filter(l => !l.split('|')[4]).length
console.log(`待确认中无候选列的：${noCand} 条`)
