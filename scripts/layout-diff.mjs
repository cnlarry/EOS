/**
 * 统一表单布局差异工具：旧系统编辑页（自动提取） vs 当前 FORM_* 配置。
 * 输出可疑模块清单（未配置/页签或列数不符/字段覆盖率低/提取失败），供实施顾问优先核对。
 * 输入：logs/modules-config.tsv、logs/fields-config.tsv、ERP/*.aspx
 * 用法：node scripts/layout-diff.mjs
 */
import { createRequire } from 'module'
import { readFileSync, writeFileSync, existsSync } from 'fs'
import { join } from 'path'

const require = createRequire(import.meta.url)
const root = process.cwd()

const CONTROL_ID = /<cc1:Dx(?:TextBox|Calendar)\b[^>]*\bID="(txt_[A-Za-z0-9_]+)"[^>]*>/gi
const DRO_ID = /<cc1:DxDropDownList\b[^>]*\bID="(dro_[A-Za-z0-9_]+)"[^>]*>([\s\S]*?)<\/cc1:DxDropDownList>/gi
const CHK_ID = /<cc1:DxCheckBox\b[^>]*\bID="(chk_[A-Za-z0-9_]+)"[^>]*>/gi
const CHO_ID = /<cc1:DxChooser\b[^>]*\bID="(cho_[A-Za-z0-9_]+)"[^>]*>/gi
const LABEL_ID = /<cc1:DxLabel\b[^>]*\bID="(lab_[A-Za-z0-9_]+)"[^>]*>/gi

function stripPrefix(id, prefix) { return id.slice(0, prefix.length).toLowerCase() === prefix ? id.slice(prefix.length) : id }

/** 收集页面的可执行上下文（内联脚本 + 外链 JS + code-behind + Chooser 回填映射），用于判定控件是否曾被赋值 */
function collectSources(filePath, raw) {
  const parts = []
  const dir = filePath.slice(0, Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\')) + 1).replace(/\\/g, '/')
  const resolve = (ref) => {
    const cleaned = ref.replace(/^\.\//, '')
    let joined = dir + cleaned
    let prev
    do { prev = joined; joined = joined.replace(/[^/]+\/\.\.\//g, '') } while (joined !== prev)
    joined = joined.replace(/\/\.\//g, '/')
    return joined
  }
  for (const m of raw.matchAll(/<script\b[^>]*src="([^"]+)"[^>]*>/gi)) {
    try { parts.push(readFileSync(resolve(m[1]), 'utf8')) } catch { /* 引用文件缺失，跳过 */ }
  }
  const cbMatch = raw.match(/CodeBehind="([^"]+\.cs)"|CodeFile="([^"]+\.cs)"/i)
  if (cbMatch) {
    const cs = cbMatch[1] ?? cbMatch[2]
    try { parts.push(readFileSync(resolve(cs), 'utf8')) } catch { /* code-behind 缺失，跳过 */ }
  }
  // Chooser 回填映射（ReturnIdAndColumn="txt_XXX=COL,..."）会向控件赋值
  for (const m of raw.matchAll(/ReturnIdAndColumn="([^"]*)"/gi)) parts.push(m[1])
  return parts.join('\n')
}

/**
 * 判定幽灵控件是否曾被赋值：
 * 在整页可执行上下文（内联 JS、外链 JS、code-behind、Chooser/DropDown 映射）中，
 * 出现 txt_<KEY> / cho_<KEY> / dro_<KEY> / chk_<KEY> 任一引用即视为会被赋值。
 */
function buildAssignedIds(context) {
  const assigned = new Set()
  for (const prefix of ['txt_', 'cho_', 'dro_', 'chk_']) {
    const re = new RegExp(`${prefix}([A-Za-z0-9_]+)`, 'g')
    for (const m of context.matchAll(re)) assigned.add(m[1].toUpperCase())
  }
  return assigned
}

/** 解析一个页面：返回 { tabs, columns, fields:[{key,tab,order}], warnings } */
function parsePage(filePath) {
  const raw = readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '')
  const context = collectSources(filePath, raw)
  let doc = raw
  // 隐藏表格（display:none）里的控件是旧页工作变量/死控件，不是表单字段
  doc = doc.replace(/<table\b[^>]*style="[^"]*display:\s*none[^"]*"[^>]*>[\s\S]*?<\/table>/gi, '')
  const result = { tabs: [], columns: 0, fields: [], warnings: [] }
  const tabLabels = new Map()
  for (const match of doc.matchAll(/id="menuTD(\d+)"[^>]*>([\s\S]*?)<\/div>/gi)) {
    const no = Number(match[1])
    const text = match[2].replace(/<[^>]+>/g, '').trim()
    if (text) tabLabels.set(no, text)
  }
  for (const [no, title] of [...tabLabels.entries()].sort((a, b) => a[0] - b[0])) result.tabs.push({ no, title })
  const divPositions = []
  for (const match of doc.matchAll(/id="menuDIV(\d+)"/gi)) divPositions.push({ no: Number(match[1]), index: match.index })
  const tabOf = (index) => {
    let tab = 1
    for (const pos of divPositions) { if (pos.index < index) tab = pos.no; else break }
    return tab
  }
  const orderByTab = new Map()
  for (const tr of doc.matchAll(/<tr\b[^>]*>([\s\S]*?)<\/tr>/gi)) {
    const rowStart = tr.index
    const rowHtml = tr[0]
    const cells = [...rowHtml.matchAll(/<td\b[^>]*>([\s\S]*?)<\/td>/gi)].map(m => m[1])
    const rowFields = []
    let pending = null
    for (const cell of cells) {
      const labels = [...cell.matchAll(LABEL_ID)].map(m => stripPrefix(m[1], 'lab_'))
      const controls = [...cell.matchAll(CONTROL_ID)].map(m => ({ key: stripPrefix(m[1], 'txt_'), id: m[1], tag: m[0] }))
      const chks = [...cell.matchAll(CHK_ID)].map(m => ({ key: stripPrefix(m[1], 'chk_'), id: m[1], tag: m[0] }))
      const dros = [...cell.matchAll(DRO_ID)].map(m => ({ key: stripPrefix(m[1], 'dro_'), id: m[1], tag: m[0] }))
      const chos = [...cell.matchAll(CHO_ID)].map(m => stripPrefix(m[1], 'cho_'))
      if (!labels.length && !controls.length && !chks.length && !dros.length && !chos.length) continue
      for (const label of labels) { pending = { key: label, ctrl: null, ctrlId: null, hidden: false }; rowFields.push(pending) }
      if (!labels.length && pending && (controls.length || chks.length || dros.length || chos.length)) {
        // 控件在标签之后的单元格
      } else if (controls.length || chks.length || dros.length || chos.length) {
        if (!pending) {
          const first = dros[0] ?? controls[0] ?? chks[0]
          if (first) { pending = { key: first.key, ctrl: first.key, ctrlId: first.id, hidden: isHidden(first.tag) }; rowFields.push(pending) }
        }
      }
      if (pending && (controls.length || chks.length || dros.length || chos.length)) {
        const first = controls[0] ?? chks[0] ?? dros[0] ?? null
        if (first) {
          pending.ctrl = pending.ctrl ?? first.key
          pending.ctrlId = pending.ctrlId ?? first.id
          pending.hidden = pending.hidden || isHidden(first.tag)
        }
      }
    }
    if (!rowFields.length) continue
    const tab = tabOf(rowStart)
    if (!orderByTab.has(tab)) orderByTab.set(tab, 0)
    for (const field of rowFields) {
      orderByTab.set(tab, orderByTab.get(tab) + 1)
      result.fields.push({ key: field.ctrl ?? field.key, tab, order: orderByTab.get(tab), ctrlId: field.ctrlId, hidden: field.hidden })
    }
  }
  for (const tr of doc.matchAll(/<tr\b[^>]*>([\s\S]*?)<\/tr>/gi)) {
    if (!/DxLabel/.test(tr[0])) continue
    const cells = [...tr[0].matchAll(/<td\b[^>]*>([\s\S]*?)<\/td>/gi)].map(m => m[1])
    result.columns = Math.max(result.columns, cells.filter(c => /DxLabel/.test(c)).length)
  }
  if (!result.columns) result.columns = 2
  if (!result.fields.length) result.warnings.push('未提取到表单字段')
  return { ...result, raw, assignedIds: buildAssignedIds(context) }
}

function isHidden(tag) {
  return /display:\s*none/i.test(tag) || /CssClass="[^"]*hidden/i.test(tag)
}

function readTsv(path) {
  if (!existsSync(path)) throw new Error(`缺少输入：${path}`)
  return readFileSync(path, 'utf8').split(/\r?\n/).map(l => l.trim()).filter(Boolean).map(l => l.split('|').map(s => s.trim()))
}

const modules = readTsv(join(root, 'logs', 'modules-config.tsv'))
const fieldRows = readTsv(join(root, 'logs', 'fields-config.tsv'))
const columnRows = existsSync(join(root, 'logs', 'columns-config.tsv')) ? readTsv(join(root, 'logs', 'columns-config.tsv')) : []
const physicalByTable = new Map()
for (const [table, column] of columnRows) {
  if (!physicalByTable.has(table)) physicalByTable.set(table, new Set())
  physicalByTable.get(table).add(column)
}
const fieldConfigByTable = new Map()
for (const [table, key, tab, order, span, group, role, options] of fieldRows) {
  if (!fieldConfigByTable.has(table)) fieldConfigByTable.set(table, new Map())
  fieldConfigByTable.get(table).set(key, { tab, order, span, group, role, options })
}

const report = []
/** 旧页面公共/非业务控件，不计入"旧页字段不在 FIELDS" */
const NoiseKeys = new Set(['HEADER'])
for (const [id, desc, master, url, tabsRaw, columnsRaw, buttons] of modules) {
  // 仅处理有旧页面路径（~/）的工作台模块；特殊页（/admin/*、/search-center 等）无旧布局，跳过
  if (!url || url === '.' || !url.startsWith('~') || !master) continue
  const file = url.replace(/^\~?\//, '').replace(/\?.*$/, '')
  const filePath = join(root, 'ERP', ...file.split('/'))
  if (!existsSync(filePath)) { report.push({ id: Number(id), desc, level: 'HIGH', reason: `页面不存在 ${file}` }); continue }
  const page = parsePage(filePath)
  if (page.warnings.length || !page.fields.length) { report.push({ id: Number(id), desc, level: 'HIGH', reason: `页面提取失败：${page.warnings.join('；') || '0 字段'}` }); continue }

  const reasons = []
  // 模块未配置布局（走默认版式）但有页签或多字段页面
  if (!tabsRaw && !columnsRaw) {
    if (page.tabs.length > 1 || page.fields.length > 30) reasons.push(`未配置布局（旧页 ${page.tabs.length || 1} 页签/${page.fields.length} 字段，走默认版式）`)
  } else {
    if (page.tabs.length > 1 && tabsRaw) {
      const expected = page.tabs.map(t => `${t.no}=${t.title}`).join(';')
      if (expected !== tabsRaw) reasons.push(`页签与旧页不一致：[${tabsRaw}] vs [${expected}]`)
    }
    if (columnsRaw && Number(columnsRaw) !== page.columns) reasons.push(`每行对数 ${columnsRaw} vs 旧页 ${page.columns}`)
  }
  // 复刻口径：以 FIELDS 与物理列为准——FIELDS 有行或物理列有 → 应复刻；两者均无 → 新系统不理会，直接 pass
  const configured = fieldConfigByTable.get(master)
  const physical = physicalByTable.get(master) ?? new Set()
  const effectiveFields = page.fields.filter(f => configured?.has(f.key) || physical.has(f.key))
  // 字段覆盖率：旧页有效字段中有 FORM_ORDER 配置的比例
  const covered = configured ? effectiveFields.filter(f => (configured.get(f.key)?.order ?? '') !== '').length : 0
  const coverage = effectiveFields.length ? covered / effectiveFields.length : 0
  const missingFields = effectiveFields.filter(f => !configured?.has(f.key) || (configured.get(f.key)?.order ?? '') === '')
  const missingNoFields = missingFields.filter(f => !configured?.has(f.key) && physical.has(f.key))
  const missingNotConfigured = missingFields.filter(f => configured?.has(f.key))
  if (coverage < 1) {
    const parts = [`覆盖率 ${Math.round(coverage * 100)}%（${covered}/${effectiveFields.length}）`]
    if (missingNotConfigured.length) parts.push(`待复刻（FIELDS 有行未配置）${missingNotConfigured.length} 个：${missingNotConfigured.slice(0, 8).map(f => f.key).join(',')}${missingNotConfigured.length > 8 ? '…' : ''}`)
    if (missingNoFields.length) parts.push(`真缺失（物理列有但 FIELDS 无行，需补 FIELDS）${missingNoFields.length} 个：${missingNoFields.slice(0, 8).map(f => f.key).join(',')}${missingNoFields.length > 8 ? '…' : ''}`)
    reasons.push(parts.join('；'))
  }
  if (reasons.length) report.push({ id: Number(id), desc, level: reasons.length >= 2 ? 'HIGH' : 'MED', reason: reasons.join('；') })
}

report.sort((a, b) => (a.level === b.level ? 0 : a.level === 'HIGH' ? -1 : 1))
const lines = ['# 统一表单布局差异报告（旧页提取 vs 当前配置）', '',
  `> 生成：${new Date().toISOString().slice(0, 10)}。规则：①未配置但旧页复杂、页签/列数不符、字段覆盖率 <100%；②复刻口径以 FIELDS 与物理列为准——FIELDS 有行或物理列有的字段应复刻，两者均无（含隐藏控件）则直接忽略，不计入差异。`, '',
  `可疑模块：${report.length} 个（HIGH ${report.filter(r => r.level === 'HIGH').length} / MED ${report.filter(r => r.level === 'MED').length}）`, '']
for (const r of report) lines.push(`- [${r.level}] ${r.id} ${r.desc}：${r.reason}`)
writeFileSync(join(root, 'logs', 'layout-diff-report.md'), lines.join('\n'), 'utf8')
console.log(`可疑模块 ${report.length} 个；HIGH ${report.filter(r => r.level === 'HIGH').length}，MED ${report.filter(r => r.level === 'MED').length}。`)
