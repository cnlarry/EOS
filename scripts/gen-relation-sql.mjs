/**
 * 虚拟字段 QUERY_RELATION 补全（别名口径 v2）。
 * 已覆盖 = 虚拟字段引用的源表名出现在现有 QUERY_RELATION 的 JOIN 别名集合（基表/AS 别名/隐式别名）。
 * 仅对"真缺失且无别名冲突"的表生成追加 SQL；有冲突/无法安全的表输出人工清单。
 */
import { readFileSync, writeFileSync } from 'fs'

const wl = readFileSync('logs/association-whitelist.tsv', 'utf8').split(/\r?\n/).filter(Boolean).slice(1)
  .map(l => { const [t, f, s, c, link] = l.split('|'); return { t, f, s, c, link } })

const qr = new Map()
for (const line of readFileSync('logs/_qr-snapshot.tsv', 'utf8').split(/\r?\n/).filter(Boolean)) {
  const i = line.indexOf('|')
  qr.set(line.slice(0, i).trim().toUpperCase(), line.slice(i + 1).trim())
}

const pk = {
  PRODUCT: 'PRO_NO', PRODUCT_M: 'PRO_NO', HALF_PRO: 'PRO_NO', SAMPLE_PRO: 'PRO_NO',
  CUS_PRODUCT: 'PRO_ID', CUS_MANUAL_PRO: 'PRO_ID', HR_EMPLOYEE: 'EMP_ID', SYSDN: 'EMP_ID',
  SFC_PROCEDURE: 'PROCEDURE_ID', SFC_PROCEDURE_TYPE: 'PROCEDURE_TYPE_ID', PRODUCE_STUFF: 'STUFF_ID',
  STUFF: 'STUFF_ID', UNIT_LWH: 'UNIT_ID', UNIT: 'UNIT_ID', DEPOT_M: 'DEPOT_ID', DEPOT: 'DEPOT_ID',
  TAX_M: 'TAX_ID', TAX: 'TAX_ID', CLIENT: 'CLIENT_ID', SUPPLIER: 'SUPPLIER_ID', COLOR: 'COLOR_ID',
  COLOR_M: 'COLOR_ID', DEPT_M: 'DEPT_ID', DEPT: 'DEPT_ID', LINE: 'LINE_ID', BILLKIND: 'BILL_CODE',
  MODULES: 'M_IDX', CURR: 'CURR_ID', BANK: 'BANK_ID', MOU_MOULD: 'MOULD_ID', TYPE_FORMULA: 'TYPE_ID',
  MOU_TYPE: 'TYPE_ID', SORT: 'SORT_ID', MOU_OWNER: 'OWNER_ID', ACC_ACCOUNT: 'ACCOUNT_ID',
  ACC_ACCOUNT_TYPE: 'TYPE_ID', ACCOUNT_TYPE: 'ACCOUNT_TYPE_ID', RECEIVE: 'RECEIVE_ID',
  CUS_COUNTRY: 'COUNTRY_ID', CUS_IMPOSE: 'IMPOSE_ID', CUS_CUSTOMS: 'CUSTOMS_ID', CUS_DEPOT: 'DEPOT_ID',
  HR_DUTY: 'DUTY_ID', CAR: 'CAR_ID', REPORT_HEADER: 'HEADER_ID', COMPANY: 'COMPANY_ID',
  REPORT: 'REPORT_ID', SYSQR_TYPE: 'TYPE_ID', WFFORM: 'WF_M_IDX', SYSDG: 'G_IDX', TABLES: 'T_ID',
  FIELDS: 'F_ID', HR_DIMISSION: 'DIMISSION_ID', HR_EVECTION: 'EVECTION_ID', HR_LEAVE: 'LEAVE_ID',
  HR_AMERCE: 'AMERCE_ID', HR_AWARD: 'AWARD_ID', HR_CERTIFY: 'CERTIFY_ID', HR_BED: 'BED_ID',
  HR_BLOOD: 'BLOOD_ID', HR_DIPLOMA: 'DIPLOMA_ID', HR_DORM: 'DORM_ID', HR_GRADE: 'GRADE_ID',
  HR_LANGUAGE: 'LANGUAGE_ID', HR_NATION: 'NATION_ID', HR_POLITY: 'POLITY_ID', HR_POST: 'POST_ID',
  HR_PROVINCE: 'PROVINCE_ID', HR_TITLE: 'TITLE_ID', HR_SAFE: 'SAFE_ID', HR_WORKTYPE: 'WORKTYPE_ID',
  V_SYSDL_SYSDN: 'EMP_ID', SYSDN_FOLLOW: 'EMP_ID', SYSDN_MOTOR: 'EMP_ID', MOU_ASSESS_M: 'MOU_ASSESS_NO',
  MOU_ACCEPT_M: 'ACCEPT_NO', MOU_APPLY_M: 'APPLY_NO', MOU_PRO_M: 'PRO_NO', NOPE_TABLE: 'NOPE_ID',
}

// 解析现有 QUERY_RELATION 的 JOIN 别名集合（含基表）
function aliasSet(table, relation) {
  const set = new Set([table])
  if (!relation) return set
  const normalized = relation.replace(/\)\s*ON/gi, ') ON')
  const segments = normalized.split(/\s+LEFT\s+JOIN\s+/i)
  for (const seg of segments.slice(1)) {
    // 与 VirtualExpressionParser.JoinSegment 等价：支持「表」或「表 别名」（无 AS 语法）
    const m = seg.match(/^([A-Za-z_][A-Za-z0-9_]*)(?:\s+WITH\s*\(\s*NOLOCK\s*\)|\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+WITH\s*\(\s*NOLOCK\s*\))?)?\s+ON/i)
    if (m) set.add((m[2] ?? m[1]).toUpperCase())
  }
  return set
}

const covered = []
const missing = []
for (const row of wl) {
  const rel = qr.get(row.t) ?? ''
  if (aliasSet(row.t, rel).has(row.s.toUpperCase())) covered.push(row)
  else missing.push(row)
}

// 真缺失：按当前表聚合，追加 JOIN（源表名作别名）；若别名与现有冲突则标记人工
const sql = ['/* 虚拟字段 QUERY_RELATION 补全 v2（别名口径；仅追加真缺失且无冲突） */']
const manual = []
const byTable = new Map()
for (const row of missing) {
  if (!byTable.has(row.t)) byTable.set(row.t, { rel: qr.get(row.t) ?? '', rows: [] })
  byTable.get(row.t).rows.push(row)
}
for (const [table, g] of byTable) {
  const existingAliases = aliasSet(table, g.rel)
  const pending = []
  let conflict = false
  for (const row of g.rows) {
    if (existingAliases.has(row.s.toUpperCase()) || pending.some(p => p.s === row.s)) continue
    const pkCol = pk[row.s]
    if (!pkCol) { manual.push(`${table}.${row.f} → ${row.s}.${row.c}（源表主键未知）`); conflict = true; continue }
    // 追加 JOIN 会引入别名 row.s；若现有配置已有同名列但指向别的表，由解析器拒绝，这里仅防明显重复
    pending.push({ s: row.s, link: row.link, pkCol, c: row.c })
  }
  if (conflict) { manual.push(`— ${table}：存在无法自动补全的字段，整表跳过，需人工`); continue }
  if (pending.length === 0) continue
  // ON 条件用裸「表.列」（VirtualExpressionParser.Condition 不支持方括号）
  const joins = pending.map(p => `LEFT JOIN ${p.s} WITH (NOLOCK) ON ${table}.${p.link}=${p.s}.${p.pkCol}`)
  const base = g.rel.trim() || `${table} WITH (NOLOCK)`
  const newRelation = `${base} ${joins.join(' ')}`
  sql.push(`UPDATE dbo.TABLES SET QUERY_RELATION=N'${newRelation.replace(/'/g, "''")}' WHERE T_ID=N'${table}';`)
}

writeFileSync('logs/relation-backfill.sql', sql.join('\n'), 'utf8')
writeFileSync('logs/relation-manual.tsv', manual.join('\n'), 'utf8')
console.log(`已覆盖 ${covered.length} / ${wl.length}；真缺失 ${missing.length} 条；生成补全 ${sql.length - 1} 张表；人工 ${manual.length} 项`)
console.log('补全表:', [...byTable.keys()].filter(t => sql.some(l => l.includes(`WHERE T_ID=N'${t}'`))).join(', '))
