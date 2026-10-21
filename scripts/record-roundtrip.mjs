/**
 * 统一表单「提交回环」扫描（广扫体检，非门禁）
 *
 * A 层（`render-sweep.mjs`）验的是"元数据说的，界面给不给"；本脚本验的是另一句话：
 * **提交进去的值，必须原样回来**。逐模块走一遍 建单 → 读回逐字段比对 → 改 → 再比 → 删 → 确认消失，
 * 抓的是界面看不见的那一类缺陷：保存静默丢字段、只读派生列覆盖用户输入、精度被截断、
 * 选择器回填丢失、明细行丢失、删了还查得到。
 *
 * 判据（FAIL 级）：
 *   VALUE_LOST             提交了非空值，读回是空 ★核心
 *   VALUE_MISMATCH         读回值 ≠ 提交值（按字段类型归一后比较）★核心
 *   DETAIL_LOST            提交了明细行，读回没有（或行数少于提交数）
 *   DETAIL_VALUE_LOST      明细某个字段提交了非空值，读回是空
 *   CREATE_REJECTED        建单被服务端拒绝（4xx，带错误码）——多半是用例的值造得不合业务校验
 *   CREATE_SERVER_ERROR    建单 5xx
 *   UPDATE_REJECTED        改单被拒
 *   UPDATE_VALUE_LOST      改单后值丢了
 *   DELETE_FAILED          删除被拒
 *   DELETE_NOT_EFFECTIVE   删完仍能读到
 *   ORPHAN_LEFT            清理失败，记录还留在库里（报告里给出主键，需人工处理）
 *
 * 另记 info（不作为失败，但要看）：NO_ADD（只读模块）、SIDE_EFFECT（自动批核/有流程，跳过）、
 * NOT_WRITABLE、UNFILLABLE（本脚本造不出合法值，附带卡在哪个字段）。
 *
 * 安全边界（**必须守住，否则这个工具会污染真库**）：
 *   · 只建自己造的单，只删自己造的单——绝不遍历、绝不改既有记录；
 *   · 自动批核模块一律跳过：保存即批核会触发效果链（库存/账务），那不是"测试"该做的事；
 *   · 每条记录都带 ZZ 前缀的主键，跑完的报告里能一眼认出是本脚本留下的；
 *   · 每个模块 try/finally 收尾，异常也要尝试删除；删不掉就报 ORPHAN_LEFT 并给出主键。
 *
 * 用法：
 *   node scripts/record-roundtrip.mjs [webBaseUrl] [选项]
 *     --modules=1401,1606   只跑点名模块
 *     --limit=20            只跑前 N 个
 *     --plan-only           只构建值不提交（看值生成质量，不写库）
 *     --keep                跑完不删（排查用；报告会列出留下的主键）
 *     --boundary            追加 C 层负面边界：在已建单成功的模块上逐因子投毒
 *                           （清空必填/超长/非数字/非法日期/负数/超长数字/只读字段/
 *                            未知字段/畸形 JSON），断言"必须 4xx 带 code，绝不 5xx"
 *     --baseline=<路径>     与该基线比对，只报新增（棘轮）
 *     --update-baseline     把本次结果写回基线
 *     --out=<目录>          报告输出目录，默认 logs/record-roundtrip
 *   环境变量：PW_CORE_PATH / EOS_CHROME_PATH / EOS_SWEEP_USER / EOS_SWEEP_PASSWORD
 */
import { createRequire } from 'module'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'fs'
import { join, resolve } from 'path'

const require = createRequire(import.meta.url)
/**
 * playwright-core 不随仓库安装，按候选顺序探测；浏览器一律用系统 Chrome。
 * 这里复用浏览器只为拿一份带登录 Cookie 的会话——所有读写都走页面里的同源 fetch。
 */
function loadChromium() {
  const candidates = [
    process.env.PW_CORE_PATH,
    join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core'),
    join(process.env.USERPROFILE || '', '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules', 'playwright-core'),
    'playwright-core',
  ].filter(Boolean)
  const tried = []
  for (const candidate of candidates) {
    try {
      const loaded = require(candidate)
      if (loaded.chromium) return loaded.chromium
      tried.push(`${candidate}（无 chromium 导出）`)
    } catch (error) {
      tried.push(`${candidate}（${error.code ?? error.message}）`)
    }
  }
  throw new Error(`找不到可用的 playwright-core，试过：\n  ${tried.join('\n  ')}\n请用 PW_CORE_PATH 指定其安装目录。`)
}
const chromium = loadChromium()

const args = process.argv.slice(2)
const baseUrl = args.find(item => !item.startsWith('--')) || process.env.EOS_WEB_URL || 'http://localhost'
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const option = (name, fallback = null) => {
  const hit = args.find(item => item.startsWith(`--${name}=`))
  return hit ? hit.slice(name.length + 3) : (args.includes(`--${name}`) ? true : fallback)
}
const onlyModules = String(option('modules', '') || '').split(',').map(item => item.trim()).filter(Boolean).map(Number)
const limit = Number(option('limit', 0)) || 0
const outDir = resolve(String(option('out', join('logs', 'record-roundtrip'))))
const baselinePath = option('baseline', null)
const updateBaseline = Boolean(option('update-baseline', false))
const planOnly = Boolean(option('plan-only', false))
const keepRecords = Boolean(option('keep', false))
/** C 层：在已建单成功的模块上跑一轮负面边界（变异因子），断言"绝不 5xx"。 */
const boundary = Boolean(option('boundary', false))

/** 探针值前缀：本脚本造出来的主键一律带它，事后在库里能一眼认出。 */
const VALUE_PREFIX = 'ZZ'

// ===== 请求封装（同源 fetch，凭浏览器 Cookie）=====
async function api(page, method, path, body, extraHeaders) {
  return await page.evaluate(async ({ method, path, body, extraHeaders }) => {
    const options = { method, headers: { accept: 'application/json', ...(extraHeaders ?? {}) }, credentials: 'include' }
    if (body !== undefined && body !== null) {
      options.headers['content-type'] = 'application/json'
      options.body = JSON.stringify(body)
    }
    const response = await fetch(path, options)
    const text = await response.text()
    let json = null
    try { json = text ? JSON.parse(text) : null } catch { /* ProblemDetails 之外的形态 */ }
    return { status: response.status, json, text: text.slice(0, 900) }
  }, { method, path, body, extraHeaders })
}

// ===== 值生成 =====
const isBit = field => String(field.dataType ?? '').toLowerCase().includes('bit')
const isNumeric = field => /decimal|numeric|money|smallmoney|float|real/.test(String(field.dataType ?? '').toLowerCase())
const isInteger = field => /(^|[^a-z])(tinyint|smallint|int|bigint)/.test(String(field.dataType ?? '').toLowerCase())
const isDateOnly = field => /^date$/i.test(String(field.dataType ?? '').trim())
const isDateTime = field => /datetime|smalldatetime|datetime2/.test(String(field.dataType ?? '').toLowerCase())
const activeSources = field => (field.choosers ?? []).filter(source => source.active && source.table)

/**
 * 与服务端同源的提交字段集（镜像 `formEditorUtils.writableFields`）：
 * 只读但"必填或带选择器"的联动列必须提交，否则服务端必填校验失败；
 * serverFilled / 虚拟 / displayOnly 由服务端维护，不提交；主键不进提交体（新增态例外见调用处）。
 */
function writableFields(fields) {
  return (fields ?? []).filter(field => field.isVisible && !field.serverFilled && !field.isVirtual && !field.displayOnly
    && (!field.isReadonly
      || (!field.isPrimaryKey && (field.isRequired || activeSources(field).length > 0))))
}

/**
 * 主表金额列在「明细表有 AMOUNT 列」时由服务端按明细权威复算（前端也把它锁成只读）。
 * 这类列提交上去会被覆盖，读回自然与提交值不同——那不是"值丢了"，是设计如此。
 * 镜像 FormEditorPage 的 masterAmountLocked 与 amountCalculator 的 AMOUNT_COLUMN_KEYS。
 */
const DERIVED_AMOUNT_KEYS = new Set(['AMOUNT', 'TAX_SUM', 'AMOUNT_TAX', 'RECEIVE_SUM', 'PAYOUT_SUM'])
const derivedAmountKeys = form =>
  (form.detailFields ?? []).some(field => String(field.key ?? '').toUpperCase() === 'AMOUNT')
    ? DERIVED_AMOUNT_KEYS
    : new Set()

/**
 * 保存期被效果链改写的明细列：`pur-apply-sync` 承接旧过程 `P_PUR_APPLY_After_Save`，
 * 第一步就是「本单明细应购数量清零」，随后由待购表（PUR_APPLY_MORE）分配补回。
 * 提交值被覆盖是设计，不参与读回比对；只对挂了该 SAVE 效果的模块生效，
 * 免得把普通模块的同名列一起放过。
 */
const SAVE_REWRITTEN_DETAIL_KEYS = { 'pur-apply-sync': new Set(['REQUIRE_QTY']) }
const saveRewrittenDetailKeys = definition => {
  const keys = new Set()
  for (const action of definition?.businessActions ?? []) {
    if (!action?.enabled) continue
    if (String(action.eventCode ?? '').toUpperCase() !== 'SAVE') continue
    const mapped = SAVE_REWRITTEN_DETAIL_KEYS[String(action.effectKey ?? '').toLowerCase()]
    for (const key of mapped ?? []) keys.add(key)
  }
  return keys
}

/** 新增态的主表提交集：可写字段 + 主键（新增时主键要用户给，编辑态才由键决定）。 */
function createFields(form) {
  const derived = derivedAmountKeys(form)
  return (form.masterFields ?? []).filter(field => field.isVisible && !field.serverFilled && !field.isVirtual && !field.displayOnly
    && !derived.has(String(field.key ?? '').toUpperCase())
    && (field.isPrimaryKey || !field.isReadonly
      || (!field.isPrimaryKey && (field.isRequired || activeSources(field).length > 0))))
}

/**
 * 折扣/冲抵这类"扣减型合计"必须造 0：随手造个正值等于给单据打满折，
 * 会让服务端的结算公式（实收 = 价税合计 − 折扣 − 冲抵）算出 0 或负数，
 * 于是"提交值 vs 读回值"看起来像丢值，实际是我把业务前提造错了。
 */
const ZERO_AMOUNT_KEYS = new Set(['REBATE_SUM', 'REBATE', 'DISCOUNT_SUM', 'PREPAY_SUM'])

/**
 * 解析模块过滤条件（`MODULES.FILTER`）里的等值项：`BACK_TYPE='SCTW'`、`REWORK_TAG=0`。
 * 这些字段决定"这条记录属于哪个模块"，探针值落在被排除的一侧时保存直接被
 * `RECORD_OUT_OF_MODULE_FILTER` 拒（与 1522/180308 同族）。只认 `KEY=值` 的合取式，
 * 其它形态（函数、比较、子查询）一律跳过——猜错的判别值比不猜更糟。
 */
function parseModuleFilter(text) {
  const map = new Map()
  if (!text) return map
  for (const part of String(text).split(/\s+and\s+/i)) {
    const match = part.trim().match(/^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:'([^']*)'|([0-9.]+))$/)
    if (!match) continue
    map.set(match[1].toUpperCase(), match[2] !== undefined ? match[2] : match[3])
  }
  return map
}

/**
 * 按字段类型造一个合法值。返回 null 表示"造不出来"，由调用方记 UNFILLABLE。
 *
 * 数值列的小数位不能只看列声明的 scale：`numeric-context` 把字段分成 QTY / PRICE / AMOUNT /
 * RATE / PERCENT，其中**数量的可接受小数位由该行的单位精度决定**（PCS/箱/张为 0 位、
 * 米/千克为 3 位），与 `decimal(18,6)` 的物理声明无关。按列 scale 造 1.2500 会撞上
 * 「小数位超出精度（最多 0 位）」——探针值不合法，模块被记成 create-rejected，白丢覆盖。
 * 所以数量造整数（任何单位都成立），金额/单价/汇率/百分比造 2 位；
 * 未登记角色的数值字段才退回按列 scale 造值。
 *
 * `filterValues` 命中时优先：模块判别字段必须取模块过滤条件要求的值，否则整张单都建不进去。
 */
function generateValue(field, runTag, seq, roleByKey, filterValues) {
  const filterHit = filterValues?.get(String(field.key ?? '').toUpperCase())
  if (filterHit !== undefined && filterHit !== '') return filterHit
  if (ZERO_AMOUNT_KEYS.has(String(field.key ?? '').toUpperCase()) && isNumeric(field)) return '0'
  const max = Number(field.maxLength) || 0
  const clamp = text => (max > 0 && text.length > max ? text.slice(0, max) : text)
  if (isBit(field)) return '0'
  if (isNumeric(field)) {
    const role = String(roleByKey?.[String(field.key ?? '').toUpperCase()] ?? '').toUpperCase()
    if (role === 'QTY') return '1'
    if (role === 'PRICE' || role === 'AMOUNT' || role === 'RATE' || role === 'PERCENT') return '1.25'
    const scale = Number.isFinite(field.scale) ? Math.max(0, Math.min(Number(field.scale), 4)) : 2
    return (1.25).toFixed(scale)
  }
  if (isInteger(field)) return '1'
  if (isDateOnly(field)) return new Date().toISOString().slice(0, 10)
  if (isDateTime(field)) return new Date().toISOString().slice(0, 19).replace('T', ' ')
  const text = String(field.dataType ?? '').toLowerCase()
  if (/char|text/.test(text)) return clamp(`${VALUE_PREFIX}${runTag}${seq}`)
  // 类型认不出来时仍给一个字符串：F_TYPE 是元数据、可能与物理列不一致（本仓有 TYPE_MISMATCH 先例），
  // 真要是不兼容，服务端会以明确错误码拒存——那比"扫描器自己放弃"有信息量得多。
  return clamp(`${VALUE_PREFIX}${runTag}${seq}`)
}

/** 主键值：必须唯一，故不用默认值（默认值在多行之间重复会撞主键）。 */
function generateKeyValue(field, runTag, seq, filterValues) {
  // 主键里的模块判别列（单别）必须取模块过滤要求的值，否则整单被
  // RECORD_OUT_OF_MODULE_FILTER 拒——唯一性由复合主键的另一列（单号）承担。
  const filterHit = filterValues?.get(String(field.key ?? '').toUpperCase())
  if (filterHit !== undefined && filterHit !== '') return filterHit
  const max = Number(field.maxLength) || 0
  const raw = `${VALUE_PREFIX}${runTag}${seq}`
  const text = String(field.dataType ?? '').toLowerCase()
  if (isInteger(field)) return String(90000 + (seq % 9000))
  if (isNumeric(field)) return '900'
  if (isDateOnly(field) || isDateTime(field)) return generateValue(field, runTag, seq)
  if (/char|text/.test(text)) return max > 0 && raw.length > max ? raw.slice(0, max) : raw
  return null
}

/**
 * 按选择器取一个真实候选并把 RETURN_ITEMS 回填进 values。
 * 造值必须用真值：假值会被业务校验规则挡住（那会淹没真缺陷），也会污染依赖它的筛选条件
 * （见 LESSONS L100）。
 */
async function fillByChooser(page, moduleId, field, values, detailValues, submittable, targets = values, preferValue = null) {
  const sources = activeSources(field)
  for (let index = 0; index < sources.length; index++) {
    const source = sources[index]
    // 有判别值时多取候选：单别类字段的候选项常排在后面，5 行看不到模块过滤要求的那一个
    const query = new URLSearchParams({ page: '1', pageSize: preferValue != null ? '50' : '5', serialNo: String(source.serialNo ?? 1) })
    query.set('master', JSON.stringify(values))
    if (detailValues) query.set('detail', JSON.stringify(detailValues))
    const path = `/api/v1/document-workbench/${moduleId}/form-chooser/${encodeURIComponent(field.key)}?${query}`
    const response = await api(page, 'GET', path)
    if (response.status !== 200 || !Array.isArray(response.json?.rows) || response.json.rows.length === 0) continue
    const mapping = parseReturnMapping(source.returnMapping)
    const items = mapping.length > 0 ? mapping : [{ target: field.key, column: field.key }]
    const rows = response.json.rows
    const matched = preferValue != null
      ? rows.find(candidate => items.some(item => String(candidate[item.column] ?? '').trim() === preferValue))
      : null
    const row = matched ?? rows[0]
    let applied = 0
    const rejectedTargets = []
    for (const item of items) {
      const raw = row[item.column]
      if (raw === undefined || raw === null || String(raw).trim() === '') continue
      // 回填只落在"可提交的字段"上：虚拟列/只读呈现列在服务端是读不进去的，
      // 写进请求体会被 `READONLY_FIELD`（"该字段由服务端维护，不可提交"）整单拒掉。
      if (!submittable.has(item.target.toUpperCase())) { rejectedTargets.push(item.target); continue }
      // 落到 `targets` 而不是恒定的 `values`：主表字段回填到主表，**明细字段必须回填到明细行**。
      // 一律写 `values` 的话，明细的回填目标（如付款单 `DUE_NO` 带出的 `DUE_TYPE`/`AMOUNT`）
      // 会落到主表字典里被丢掉，明细行的那个键永远是空的。
      targets[item.target] = String(raw).trim()
      applied++
    }
    // rejectedTargets 里既有"表单上根本没有的字段"（元数据不一致），也有"在表单上但不可提交"
    // （虚拟呈现列，设计如此）——两种由调用方按表单字段集分开判。
    if (applied > 0 || rejectedTargets.length > 0) return { serialNo: source.serialNo ?? 1, row, rejectedTargets, preferMatched: Boolean(matched) }
  }
  return null
}

function parseReturnMapping(text) {
  if (!text) return []
  try {
    const items = JSON.parse(text)
    return (Array.isArray(items) ? items : [])
      .map(item => ({ target: String(item?.target ?? ''), column: String(item?.column ?? '') }))
      .filter(item => item.target && item.column)
  } catch { return [] }
}

// ===== 读回比对 =====
/** 按字段类型归一后比较：字符串去空白、数值按数值、日期按时间戳、bit 按真假。 */
function sameValue(field, submitted, actual) {
  const left = String(submitted ?? '').trim()
  const right = String(actual ?? '').trim()
  if (left === '' && right === '') return true
  if (left === right) return true
  if (isBit(field)) return normalizeBit(left) === normalizeBit(right)
  if (isNumeric(field) || isInteger(field)) {
    const a = Number(left)
    const b = Number(right)
    if (Number.isFinite(a) && Number.isFinite(b)) return Math.abs(a - b) < 1e-9
  }
  if (isDateOnly(field) || isDateTime(field)) {
    const a = Date.parse(left)
    const b = Date.parse(right)
    if (Number.isFinite(a) && Number.isFinite(b) && a === b) return true
    // 解析不出来或时间戳不等时继续往下走"日期语义"那一条，不能在这里直接判不等
  }
  // 日期语义相等：`2026-10-17` 与 `2026-10-17T00:00:00` 是同一个时刻，只是序列化形态不同
  // （元数据 F_TYPE 与物理列不一致时会出现，本仓有 TYPE_MISMATCH 先例）。
  // 不能拿 Date.parse 比时间戳：不带时区的 'yyyy-MM-dd' 按 UTC 解析、带时间的按本地解析，两者必然不等。
  const dateOnly = /^\d{4}-\d{2}-\d{2}$/
  const dateTimeMidnight = /^\d{4}-\d{2}-\d{2}[ T]00:00:00(\.0+)?$/
  const sameDay = left.slice(0, 10) === right.slice(0, 10)
  if (sameDay && (dateOnly.test(left) || dateTimeMidnight.test(left)) && (dateOnly.test(right) || dateTimeMidnight.test(right))) {
    return true
  }
  return left === right
}

function normalizeBit(text) {
  const value = text.trim().toLowerCase()
  return ['1', 'true', 'y', 'yes'].includes(value) ? '1' : '0'
}

/**
 * 读回的主表值里，bit 列下发为布尔、数值列可能是数字，而 `values`/`original` 在服务端是
 * `Dictionary<string,string?>`——直接回传布尔会让整个请求在模型绑定阶段就被 400 INVALID_MODEL 拒掉
 * （报 `$.original.X: The JSON value could not be converted to System.String`）。
 * 这里统一转成字符串；null 直接省略（省略比回传空串安全，空串可能被当成"用户清空了它"）。
 */
function toPlainStrings(source) {
  const result = {}
  for (const [key, value] of Object.entries(source ?? {})) {
    if (value === null || value === undefined) continue
    result[key] = typeof value === 'string' ? value : String(value)
  }
  return result
}

/** 逐字段比对"提交值 vs 读回值"，返回 findings。 */
/**
 * 读回值是否只是"按分钟四舍五入"——`smalldatetime` 列只有分钟精度且四舍五入，
 * 提交 06:49:48 读回 06:50:00 是列精度使然，不是值丢了。这类差异记 info，不算缺陷。
 */
function isMinuteRounded(submitted, actual) {
  const left = Date.parse(submitted)
  const right = Date.parse(actual)
  if (!Number.isFinite(left) || !Number.isFinite(right)) return false
  if (!/[:T]\d{2}:\d{2}:00/.test(actual)) return false
  const delta = Math.abs(right - left)
  return delta > 0 && delta <= 60_000
}

/**
 * 服务端会覆盖的列：保存期的效果链或汇总会重写它们，用户提交值不保证原样回来。
 * 这不是"值丢了"，是这些列的值本来就由服务端按**引用单据/明细**算出来。
 *
 * 只列已确证"谁在写它"的键，新增前先找到那个消费点，不要凭列名猜：
 * - `ORDER_NO`/`ORDER_TYPE`/`ORDER_SERIAL_NO`：制令单族（1502/1512/2803/2804）SAVE 期
 *   link-stamp 效果按主表引用把明细订单号回填；
 * - `PRODUCE_NO`：生产单变更（1509）按被改的那张生产单归一；
 * - `QTY_TOTAL`：应收货款单（170101）按引用送货单明细汇总。
 */
const SERVER_OWNED_KEYS = new Set(['ORDER_NO', 'ORDER_TYPE', 'ORDER_SERIAL_NO', 'PRODUCE_NO', 'QTY_TOTAL'])

function compareValues(moduleId, fields, submitted, actual, stage, detail) {
  const findings = []
  for (const field of fields) {
    const sent = String(submitted[field.key] ?? '').trim()
    if (sent === '') continue // 没提交的键不比：服务端维护的列本来就不由用户给
    const got = String(actual?.[field.key] ?? '').trim()
    const serverOwned = SERVER_OWNED_KEYS.has(String(field.key ?? '').toUpperCase())
    if (serverOwned && got !== sent) {
      findings.push({
        moduleId,
        code: 'SERVER_OWNED_COLUMN',
        level: 'info',
        field: field.key,
        stage,
        detail: `${stage}：字段 ${field.key}（${field.label}）提交「${sent}」读回「${got || '空'}」——该列由服务端按引用单据/明细回填或汇总，属设计行为`,
      })
      continue
    }
    if (got === '') {
      findings.push({ moduleId, code: 'VALUE_LOST', field: field.key, stage, detail: `${stage}：字段 ${field.key}（${field.label}）提交「${sent}」读回为空${detail ? `｜${detail}` : ''}` })
    } else if (!sameValue(field, sent, got)) {
      const rounded = isMinuteRounded(sent, got)
      findings.push({
        moduleId,
        code: rounded ? 'DATETIME_MINUTE_ROUNDED' : 'VALUE_MISMATCH',
        level: rounded ? 'info' : 'fail',
        field: field.key,
        stage,
        detail: `${stage}：字段 ${field.key}（${field.label}）提交「${sent}」读回「${got}」${rounded ? '（列精度到分钟，四舍五入）' : ''}${detail ? `｜${detail}` : ''}`,
      })
    }
  }
  return findings
}

// ===== C 层：负面边界（变异因子）=====
/**
 * 不按模块铺面，而是在**已证明能建单成功**的模块上，一次只改坏一个因子，
 * 断言四种结局之一：
 *   ① 4xx 且带 problem.code —— 正常拒绝（用户能看到原因）；
 *   ② 被接受，但那个因子本就允许被接受（如负数、未知字段）—— 记 info；
 *   ③ 4xx 却没有 code —— `BOUNDARY_NO_CODE`（前端拿不到原因，只能显示"请求失败"）；
 *   ④ 5xx —— `BOUNDARY_5XX`（最严重：用户输入不该让服务端崩）。
 *
 * 判据只认"服务端有没有守住"，不判业务语义（负数该不该允许是业务问题，
 * 这里只要求它"要么明确拒绝、要么明确接受"）。
 */
const BOUNDARY_ALLOW_ACCEPT = new Set(['NEGATIVE_NUMERIC', 'UNKNOWN_FIELD', 'SQL_SNIPPET'])

/**
 * 各因子实际执行次数。某些因子依赖"本模块恰好有这类字段"（日期列、数值列……），
 * 没跑到不等于通过——报告里必须能看出"测了没有"，否则空白会被读成绿。
 */
const boundaryFactorRuns = new Map()

async function runBoundaryBattery(page, moduleId, form, fields, values, details, runTag) {
  const findings = []
  const masterFields = form.masterFields ?? []
  const textField = fields.find(field => !isNumeric(field) && !isBit(field) && !isDateOnly(field) && !isDateTime(field))
  const numericField = fields.find(field => isNumeric(field) && Number(field.scale ?? 0) > 0) || fields.find(isNumeric)
  const dateField = fields.find(field => isDateOnly(field) || isDateTime(field))
  const requiredField = masterFields.find(field => field.isRequired && !field.isPrimaryKey && values[field.key] !== undefined)
  // 只读列通常不在 `values` 里（不该提交），这里仍要探：它是"绕过前端直接发"的主场景。
  const readonlyField = masterFields.find(field => field.isReadonly && !field.isPrimaryKey && !field.isVirtual && !field.displayOnly)

  const probes = []
  if (requiredField) {
    probes.push({ factor: 'EMPTY_REQUIRED', field: requiredField.key, mutate: payload => { payload[requiredField.key] = '' } })
  }
  if (textField) {
    const long = 'Z'.repeat(Math.min(4000, Number(textField.maxLength ?? 0) + 20 || 4000))
    probes.push({ factor: 'OVERLONG_TEXT', field: textField.key, mutate: payload => { payload[textField.key] = long } })
    probes.push({ factor: 'SQL_SNIPPET', field: textField.key, mutate: payload => { payload[textField.key] = `'; DROP TABLE ZZ_PROBE--` } })
  }
  if (numericField) {
    probes.push({ factor: 'NON_NUMERIC_TEXT', field: numericField.key, mutate: payload => { payload[numericField.key] = 'abc' } })
    probes.push({ factor: 'NEGATIVE_NUMERIC', field: numericField.key, mutate: payload => { payload[numericField.key] = '-1' } })
    probes.push({ factor: 'HUGE_NUMBER', field: numericField.key, mutate: payload => { payload[numericField.key] = '9'.repeat(40) } })
  }
  if (dateField) {
    probes.push({ factor: 'INVALID_DATE', field: dateField.key, mutate: payload => { payload[dateField.key] = '2026-02-31' } })
  }
  if (readonlyField) {
    probes.push({ factor: 'READONLY_FIELD', field: readonlyField.key, mutate: payload => { payload[readonlyField.key] = 'ZZPROBE' } })
  }
  probes.push({ factor: 'UNKNOWN_FIELD', field: 'ZZ_GHOST_FIELD', mutate: payload => { payload.ZZ_GHOST_FIELD = 'ZZPROBE' } })
  probes.push({ factor: 'MALFORMED_JSON', field: '', rawBody: '{ "values": ' })

  for (const probe of probes) {
    const payload = { ...values }
    if (probe.mutate) probe.mutate(payload)
    const body = probe.rawBody ?? JSON.stringify({
      values: payload,
      ...(details ? { details } : {}),
      idempotencyKey: `rt-${runTag}-${moduleId}-b${Date.now()}-${probe.factor}`,
    })
    const response = await page.evaluate(async ({ path, body }) => {
      const res = await fetch(path, {
        method: 'POST',
        headers: { accept: 'application/json', 'content-type': 'application/json' },
        credentials: 'include',
        body,
      })
      const text = await res.text()
      let json = null
      try { json = text ? JSON.parse(text) : null } catch { /* ProblemDetails 之外的形态 */ }
      return { status: res.status, json, text: text.slice(0, 240) }
    }, { path: `/api/v1/document-workbench/${moduleId}/record`, body })

    boundaryFactorRuns.set(probe.factor, (boundaryFactorRuns.get(probe.factor) ?? 0) + 1)
    const code = String(response.json?.code ?? '')
    const allowAccept = BOUNDARY_ALLOW_ACCEPT.has(probe.factor)
    if (response.status >= 500) {
      findings.push({
        moduleId,
        code: 'BOUNDARY_5XX',
        field: probe.field,
        detail: `负面边界 ${probe.factor}（字段 ${probe.field || '-'}）返回 ${response.status} —— 用户输入不该让服务端崩：${response.text}`,
      })
      continue
    }
    if (response.status >= 200 && response.status < 300) {
      // 被接受了：清掉这条探针记录（主键同样是 ZZ 前缀，可辨认）
      const key = response.json?.key
      if (Array.isArray(key) && key.length > 0) {
        const query = new URLSearchParams({ key: JSON.stringify(key) })
        await api(page, 'DELETE', `/api/v1/document-workbench/${moduleId}/record?${query}`, null, {
          'X-Idempotency-Key': `rt-${runTag}-${moduleId}-bd${Date.now()}`,
        })
      }
      findings.push({
        moduleId,
        code: allowAccept ? 'BOUNDARY_ACCEPTED' : 'BOUNDARY_UNEXPECTED_ACCEPT',
        level: allowAccept ? 'info' : 'fail',
        field: probe.field,
        detail: `负面边界 ${probe.factor}（字段 ${probe.field || '-'}）：${allowAccept ? '被接受（业务允许，已清理探针记录）' : '本应拒绝却被接受，已清理探针记录'}`,
      })
      continue
    }
    if (!code) {
      findings.push({
        moduleId,
        code: 'BOUNDARY_NO_CODE',
        field: probe.field,
        detail: `负面边界 ${probe.factor}（字段 ${probe.field || '-'}）返回 ${response.status} 但没有 problem.code —— 前端拿不到可显示的原因：${response.text}`,
      })
    }
  }
  return findings
}

/**
 * C 层·并发与幂等（第二组因子）：连点保存、空明细提交。
 *
 * 与第一组同一判据——只看"服务端有没有守住"，不判业务语义：
 *   · 同一个 idempotencyKey 连发两次，**只该产生一条记录**（真发生两条就是重复建单）；
 *   · 明细必填的模块交空数组，要么明确拒（带 code）、要么明确接受，绝不 5xx；
 *   · 任何一步 5xx 都是 `BOUNDARY_5XX`。
 *
 * 探针记录用**另一套主键**（`ZZ…I<n>`），不与主流程那条撞车；跑完逐条删除。
 */
async function runConcurrencyBattery(page, moduleId, form, values, details, runTag) {
  const findings = []
  const path = `/api/v1/document-workbench/${moduleId}/record`
  const pkFields = (form.masterFields ?? []).filter(field => field.isPrimaryKey)
  const variant = ordinal => {
    const copy = { ...values }
    for (const [index, field] of pkFields.entries()) {
      const base = `ZZ${runTag}I${ordinal}${index}`
      copy[field.key] = base.slice(0, Number(field.maxLength) || base.length)
    }
    return copy
  }
  const drop = async key => {
    if (!Array.isArray(key) || key.length === 0) return
    const query = new URLSearchParams({ key: JSON.stringify(key) })
    await api(page, 'DELETE', `${path}?${query}`, null, {
      'X-Idempotency-Key': `rt-${runTag}-${moduleId}-cd${Date.now()}`,
    })
  }

  // ① 连点保存：同一个幂等键连发两次
  const replayTag = `rt-${runTag}-${moduleId}-idem-${Date.now()}`
  const replayBody = { values: variant(1), ...(details ? { details } : {}), idempotencyKey: replayTag }
  const first = await api(page, 'POST', path, replayBody)
  const second = await api(page, 'POST', path, replayBody)
  if (first.status >= 500 || second.status >= 500) {
    findings.push({
      moduleId,
      code: 'BOUNDARY_5XX',
      field: '',
      detail: `连点保存返回 ${first.status}/${second.status}：${(first.status >= 500 ? first.text : second.text).slice(0, 200)}`,
    })
  } else if (first.status === 200) {
    // 覆盖度登记：这一因子确实跑到了（结论另看 findings）。
    boundaryFactorRuns.set('IDEMPOTENCY', (boundaryFactorRuns.get('IDEMPOTENCY') ?? 0) + 1)
    const firstKey = JSON.stringify(first.json?.key ?? null)
    const secondKey = JSON.stringify(second.json?.key ?? null)
    if (second.status === 200 && secondKey !== firstKey) {
      findings.push({
        moduleId,
        code: 'IDEMPOTENCY_LEAK',
        field: '',
        detail: `同一幂等键连发两次产生了**两条记录**：${firstKey} / ${secondKey}——连点保存会重复建单`,
      })
    } else {
      findings.push({
        moduleId,
        code: 'IDEMPOTENCY_OK',
        level: 'info',
        detail: `连点保存：第二次返回 ${second.status}，未产生第二条记录（${second.status === 200 ? '幂等重放' : '明确拒绝'}）`,
      })
    }
    await drop(first.json?.key)
    if (second.status === 200 && secondKey !== firstKey) await drop(second.json?.key)
  }

  // ② 空明细提交：明细必填的模块交空数组
  if ((form.detailFields ?? []).length > 0) {
    boundaryFactorRuns.set('EMPTY_DETAILS', (boundaryFactorRuns.get('EMPTY_DETAILS') ?? 0) + 1)
    const empty = await api(page, 'POST', path, {
      values: variant(2),
      details: [],
      idempotencyKey: `rt-${runTag}-${moduleId}-empty-${Date.now()}`,
    })
    if (empty.status >= 500) {
      findings.push({ moduleId, code: 'BOUNDARY_5XX', field: '', detail: `空明细提交返回 ${empty.status}：${empty.text.slice(0, 200)}` })
    } else if (empty.status >= 200 && empty.status < 300) {
      await drop(empty.json?.key)
      findings.push({
        moduleId,
        code: 'EMPTY_DETAILS_ACCEPTED',
        level: 'info',
        detail: '空明细被接受（效果链可生成明细，或本模块允许无明细），探针记录已清理',
      })
    } else if (!String(empty.json?.code ?? '')) {
      findings.push({
        moduleId,
        code: 'BOUNDARY_NO_CODE',
        field: '',
        detail: `空明细提交返回 ${empty.status} 但没有 problem.code：${empty.text.slice(0, 200)}`,
      })
    }
  }
  return findings
}

// ===== 单模块回环 =====
async function roundtrip(page, moduleId, runTag, findings, extra) {
  const formResp = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/form-definition?mode=new`)
  if (formResp.status >= 500) {
    findings.push({ moduleId, code: 'CREATE_SERVER_ERROR', detail: `form-definition ${formResp.status}：${formResp.text.slice(0, 160)}` })
    return { moduleId, capability: 'server-error' }
  }
  if (formResp.status === 403 || formResp.status === 404) return { moduleId, capability: 'not-writable' }
  if (formResp.status !== 200 || !formResp.json?.moduleId) {
    findings.push({ moduleId, code: 'CREATE_REJECTED', detail: `form-definition ${formResp.status}：${formResp.text.slice(0, 160)}` })
    return { moduleId, capability: 'bad-payload' }
  }
  const form = formResp.json
  if (!form.canAddNew) return { moduleId, capability: 'no-add', title: form.title }

  // 自动批核 / 有流程：保存即批核会触发效果链（库存、账务），一律不碰
  const definition = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/definition`)
  const autoApprove = Boolean(definition.json?.autoApprove)
  if (autoApprove || form.hasWorkflow) {
    return { moduleId, capability: 'side-effect', title: form.title, autoApprove, hasWorkflow: Boolean(form.hasWorkflow) }
  }

  // 数值列的业务角色（QTY / PRICE / AMOUNT / RATE / PERCENT）：造值时的小数位按角色定，
  // 不按列声明的 scale（见 generateValue）。取不到就退化为按列 scale 造值。
  const numericContext = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/numeric-context`)
  const roleByKey = numericContext.status === 200 && numericContext.json?.fieldRoles
    ? numericContext.json.fieldRoles
    : null
  // 模块过滤条件里的等值项：判别字段按它造值，选择器字段按它挑候选行（见 parseModuleFilter）。
  const filterValues = parseModuleFilter(definition.json?.moduleFilter)

  const fields = createFields(form)
  const values = {}
  const unsolved = []
  let seq = 0
  // 两套集合要分开：**能提交的**（虚拟列/呈现列/服务端维护列除外）用于过滤请求体；
  // **表单上存在的全部字段**用于判"回填目标到底有没有这个地方"。混用会把"虚拟呈现列"
  // 误报成"字段不存在"——那是设计如此，不是缺陷。
  const submittable = new Set(fields.map(field => field.key.toUpperCase()))
  const formMasterKeys = new Set((form.masterFields ?? []).map(field => field.key.toUpperCase()))
  const reportedMissing = new Set()
  const reportMissingTargets = (picked, knownKeys) => {
    for (const target of picked?.rejectedTargets ?? []) {
      const key = target.toUpperCase()
      if (knownKeys.has(key)) continue // 表单上有它（只是不该提交），不是元数据不一致
      if (reportedMissing.has(key)) continue
      reportedMissing.add(key)
      // 定为 info：这类条目是**配置冗余**——用户真正要点的是主目标字段，同伴目标不在表单上
      // 只是"白配了一条映射"，看不出来、也不影响操作。只有当那个目标同时是必填列时才是缺陷，
      // 而那会以 REQUIRED_FIELD_MISSING 的形式在保存时暴露，不需要这条兜着。
      findings.push({
        moduleId,
        code: 'CHOOSER_TARGET_NOT_IN_FORM',
        level: 'info',
        field: target,
        detail: `数据源把 ${target} 列为回填目标，但该字段不在模块 ${moduleId} 的表单字段集里——这条映射带不出值（配置冗余）`,
      })
    }
  }

  // 第一遍：主键与不需要选择器的字段（它们往往是选择器筛选条件的来源，必须先有值）
  for (const field of fields.filter(item => item.isPrimaryKey || activeSources(item).length === 0)) {
    seq++
    // 模块判别字段的值优先于默认值：默认值往往落在模块过滤范围之外
    const filterHit = filterValues.get(String(field.key ?? '').toUpperCase())
    const raw = field.isPrimaryKey
      ? generateKeyValue(field, runTag, seq, filterValues)
      : (filterHit !== undefined && filterHit !== ''
        ? filterHit
        : (String(form.defaultValues?.[field.key] ?? '').trim() || String(field.defaultValue ?? '').trim() || generateValue(field, runTag, seq, roleByKey, filterValues)))
    if (raw === null || raw === '') { unsolved.push(field.key); continue }
    values[field.key] = raw
  }
  // 第二遍：带选择器的字段——现在 master 里已经有筛选条件要的值了，能查到真实候选
  for (const field of fields.filter(item => !item.isPrimaryKey && activeSources(item).length > 0)) {
    if (String(values[field.key] ?? '').trim() !== '') continue
    const filterKey = String(field.key ?? '').toUpperCase()
    const prefer = filterValues.get(filterKey)
    const picked = await fillByChooser(page, moduleId, field, values, null, submittable, values, prefer ?? null)
    reportMissingTargets(picked, formMasterKeys)
    // 模块过滤要求这个字段取固定值（单别类）：候选里没有那一行时直接提交该值，
    // 否则整张单会被 RECORD_OUT_OF_MODULE_FILTER 拒，连回环都走不到。
    if (prefer !== undefined && prefer !== '' && picked?.preferMatched !== true && submittable.has(filterKey)) {
      values[field.key] = prefer
    }
    if (String(values[field.key] ?? '').trim() === '') unsolved.push(field.key)
  }
  // 第三遍：回头再试那些"因为依赖别的回填值才查不到"的字段（联动常有先后）
  for (const key of [...unsolved]) {
    const field = fields.find(item => item.key === key)
    if (!field || activeSources(field).length === 0) continue
    const picked = await fillByChooser(page, moduleId, field, values, null, submittable)
    reportMissingTargets(picked, formMasterKeys)
    if (picked && String(values[key] ?? '').trim() !== '') unsolved.splice(unsolved.indexOf(key), 1)
  }
  if (unsolved.length > 0) {
    return { moduleId, capability: 'unfillable', title: form.title, unsolved }
  }

  // 明细：只造一行（要的是"这条路通不通"，不是覆盖率）
  // 金额列要**照常提交**（主表的实收/实付汇总依赖它，不提交会让汇总算成空），
  // 但**不参与读回比对**（服务端 RecalculateDetailAmounts 会按 数量×单价×折扣×税率 覆盖它）。
  // "提交"与"断言"在这里必须分开，否则不是漏测就是误报——两个方向都踩过。
  const detailFields = writableFields(form.detailFields)
  const saveRewritten = saveRewrittenDetailKeys(definition.json)
  const detailCompareFields = detailFields
    .filter(field => !DERIVED_AMOUNT_KEYS.has(String(field.key ?? '').toUpperCase()))
    .filter(field => !saveRewritten.has(String(field.key ?? '').toUpperCase()))
  let details = null
  if (detailFields.length > 0) {
    const row = {}
    const detailUnsolved = []
    for (const field of detailFields) {
      // 有数据源的明细字段一律交给选择器取真值（下面那一轮），这里不造。
      // 否则先生成的假值会让"只填空字段"的选择器轮次直接跳过它——付款单明细的
      // `DUE_NO`（应付对账单号）就是这么被造成 `ZZ...` 的：服务端据它带出只读的
      // 明细金额，假号带不出金额，主表"本次实付金额"于是汇总成 0（L100 在明细侧的遗漏）。
      if (activeSources(field).length > 0) continue
      // 明细金额列**不能吃默认值**：这类列的默认值普遍是 0，一旦提交 0，主表按明细汇总出来的
      // 金额也全是 0，而主表那几个汇总列我提交的是正值 —— 于是"读回 ≠ 提交"看起来像缺陷，
      // 实际是我自己造了一份不自洽的单据。照常生成正值，让主表汇总能对上。
      const key = String(field.key ?? '').toUpperCase()
      // 数值列一律不吃默认值：物理默认约束与版式默认值普遍是 0，提交 0 会让服务端的
      // 金额/数量链算出一份"合法但没内容"的单据（汇总对不上，或数量为 0 让分配结果变 0）。
      const preset = DERIVED_AMOUNT_KEYS.has(key) || isNumeric(field) ? '' : String(field.defaultValue ?? '').trim()
      const raw = preset || generateValue(field, runTag, ++seq, roleByKey)
      if (raw === null || raw === '') { detailUnsolved.push(field.key); continue }
      row[field.key] = raw
    }
    const detailSubmittable = new Set(detailFields.map(field => field.key.toUpperCase()))
    const formDetailKeys = new Set((form.detailFields ?? []).map(field => field.key.toUpperCase()))
    for (const field of detailFields.filter(item => activeSources(item).length > 0 && !row[item.key])) {
      const picked = await fillByChooser(page, moduleId, field, values, row, detailSubmittable, row)
      reportMissingTargets(picked, formDetailKeys)
      if (String(row[field.key] ?? '').trim() !== '') continue
      // 明细的"引用单据号"（收款单的客户对账单、付款单的应付对账单……）筛选条件多绑定主表
      // 厂商/客户，本库这组探针值下可能一条候选都没有（`{m.supplier_id}` 指向的厂商没有未结单据）。
      // 这属于**造值边界**，不是缺陷：退回造值继续走完回环，只记一条 info，
      // 否则整个模块会被判 unfillable、彻底失去覆盖（创建/读回/改/删都不再验）。
      const fallback = generateValue(field, runTag, ++seq, roleByKey)
      if (fallback === null || fallback === '') { detailUnsolved.push(field.key); continue }
      row[field.key] = fallback
      findings.push({
        moduleId,
        code: 'DETAIL_REFERENCE_UNRESOLVED',
        level: 'info',
        field: field.key,
        detail: `明细 ${field.key}（${field.label}）取不到候选，已用探针值继续（业务校验可能拒单）`,
      })
    }
    if (detailUnsolved.length > 0) {
      return { moduleId, capability: 'unfillable', title: form.title, unsolved: detailUnsolved.map(key => `明细.${key}`) }
    }
    details = [row]
  }

  if (planOnly) {
    return { moduleId, capability: 'planned', title: form.title, sample: { values, details } }
  }

  // ---- 建单 ----
  let keyValues = null
  try {
    const created = await api(page, 'POST', `/api/v1/document-workbench/${moduleId}/record`, {
      values,
      ...(details ? { details } : {}),
      idempotencyKey: `rt-${runTag}-${moduleId}-${Date.now()}`,
    })
    if (created.status >= 500) {
      findings.push({ moduleId, code: 'CREATE_SERVER_ERROR', detail: `建单 ${created.status}：${created.text.slice(0, 200)}` })
      return { moduleId, capability: 'create-500', title: form.title }
    }
    if (created.status !== 200) {
      const fieldErrors = Array.isArray(created.json?.fieldErrors) ? created.json.fieldErrors : null
      /**
       * 被拒要分清"我的探针值不合业务"与"前后端口径不一致"：
       * 前者（引用不存在、格式不匹配、超精度）是本脚本造值的边界，记 info；
       * 后者（我按表单定义提交的键，服务端说不可提交/不认识）是**真不一致**，记 FAIL。
       */
      const valueQualityCodes = new Set([
        'REFERENCE_CONFLICT', 'REGEX_MISMATCH', 'INVALID_VALUE', 'SCALE_EXCEEDED', 'PRECISION_EXCEEDED',
        'REQUIRED_FIELD_MISSING',
        // 我造的明细行没有对应的真实单据行（订单/送货/制令），服务端带不出单价/金额——
        // 属造值边界；真单据也报同一个码时才会是缺陷，那需要人工拿真实单据复核
        'REFERENCED_ORDER_NOT_FOUND',
      ])
      // 顶层就是"业务规则不认这个值"（引用不存在、客户停交易……）：没有字段级明细时也归为造值边界
      const businessRejectCodes = new Set(['BUSINESS_VALIDATION_FAILED', 'REFERENCE_CONFLICT'])
      const codes = (fieldErrors ?? []).map(item => String(item.code ?? ''))
      const onlyValueQuality = codes.length > 0
        ? codes.every(code => valueQualityCodes.has(code))
        : businessRejectCodes.has(String(created.json?.code ?? ''))
      findings.push({
        moduleId,
        code: onlyValueQuality ? 'CREATE_VALUE_REJECTED' : 'CREATE_REJECTED',
        level: onlyValueQuality ? 'info' : 'fail',
        detail: `建单被拒 ${created.status} ${created.json?.code ?? ''}：${created.json?.message ?? created.text.slice(0, 200)}`
          + (fieldErrors ? `｜${fieldErrors.map(item => `${item.field}:${item.message}(${item.code})`).join('；')}` : ''),
        errorCode: created.json?.code ?? null,
        values,
      })
      return { moduleId, capability: 'create-rejected', title: form.title }
    }
    keyValues = created.json?.key ?? null
    if (!Array.isArray(keyValues) || keyValues.length === 0) {
      findings.push({ moduleId, code: 'CREATE_SERVER_ERROR', detail: `建单返回 200 但没有主键：${created.text.slice(0, 200)}` })
      return { moduleId, capability: 'no-key', title: form.title }
    }
    const keyQuery = new URLSearchParams({ key: JSON.stringify(keyValues) })

    // ---- 读回比对（建单后）----
    const readBack = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`)
    if (readBack.status !== 200) {
      findings.push({ moduleId, code: 'VALUE_LOST', detail: `建单成功但读不回：${readBack.status} ${readBack.text.slice(0, 160)}`, stage: 'create' })
      return { moduleId, capability: 'read-back-failed', title: form.title, key: keyValues }
    }
    const master = readBack.json?.master ?? {}
    findings.push(...compareValues(moduleId, fields, values, master, '建单', `key=${JSON.stringify(keyValues)}`))

    const readDetails = Array.isArray(readBack.json?.details) ? readBack.json.details : []
    if (details) {
      if (readDetails.length < details.length) {
        findings.push({ moduleId, code: 'DETAIL_LOST', detail: `建单：提交 ${details.length} 行明细，读回 ${readDetails.length} 行`, stage: 'create' })
      } else {
        findings.push(...compareValues(moduleId, detailCompareFields, details[0], readDetails[0], '建单明细'))
      }
    }

    // ---- C 层：负面边界 + 并发幂等（只在合法建单已成功时才有意义）----
    if (boundary) {
      findings.push(...await runBoundaryBattery(page, moduleId, form, fields, values, details, runTag))
      findings.push(...await runConcurrencyBattery(page, moduleId, form, values, details, runTag))
    }

    // ---- 改单（只改主表，不提交 details = 不碰明细）----
    // 提交字段集必须取**编辑态**定义：主键在编辑态被标成只读，照新增态那份提交会被
    // `READONLY_FIELD`（"该字段为只读，不可提交"）整单拒掉。
    const editForm = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/form-definition?mode=edit`)
    const editKeys = editForm.status === 200
      ? new Set(writableFields(editForm.json.masterFields).map(field => field.key))
      : new Set(fields.filter(field => !field.isPrimaryKey).map(field => field.key))
    // 改单要回传**读回来的当前值**，不能用建单时那份 `values`：服务端在保存期可能已经把
    // 某些列改写（引用单据归一、明细汇总、效果回填），拿旧值回传会被乐观并发判成
    // `CONCURRENT_MODIFIED`（"字段内容已被他人修改"），而实际上是我自己回传了过期的值。
    const updateValues = {}
    for (const [key, value] of Object.entries(toPlainStrings(master))) if (editKeys.has(key)) updateValues[key] = value
    // 临时取证开关：EOS_DEBUG_MODULE=1418 时打印改单两侧的原值，用来判 CONCURRENT_MODIFIED 的成因
    if (String(process.env.EOS_DEBUG_MODULE ?? '') === String(moduleId)) {
      console.log(`[debug ${moduleId}] editKeys=${JSON.stringify([...editKeys])}`)
      console.log(`[debug ${moduleId}] master=${JSON.stringify(toPlainStrings(master))}`)
      console.log(`[debug ${moduleId}] updateValues=${JSON.stringify(updateValues)}`)
    }

    // 改单只改**非主键**的文本列：主键是记录身份，改它等于"换一条记录"，
    // 服务端会静默保留原值（或按规则归一），读回自然与提交值不同——那是设计，不是缺陷。
    const changeable = writableFields(editForm.status === 200 ? editForm.json.masterFields : fields)
      .find(field => !field.isPrimaryKey && !isBit(field) && !isNumeric(field) && !isDateOnly(field) && !isDateTime(field))
    if (changeable) {
      const submitUpdate = payload => api(page, 'PUT', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`, {
        values: payload,
        original: toPlainStrings(master),
        idempotencyKey: `rt-${runTag}-${moduleId}-u${Date.now()}`,
      })
      const updated = { ...updateValues, [changeable.key]: `${VALUE_PREFIX}E${runTag}`.slice(0, Number(changeable.maxLength) || 32) }
      let effective = updated // 实际被接受的那份载荷：退回原样回传时它不含那个改动
      let response = await submitUpdate(updated)
      // 改一字被业务校验挡下时（典型：元数据把数值列写成字符串类型，TYPE_MISMATCH），
      // 退回**原样回传**再走一遍保存：这一趟仍能验"编辑态保存是否丢字段/误判并发"，
      // 只是不再验证"改一个值能否存住"——比直接报一条 UPDATE_REJECTED 有信息量。
      if (response.status === 400 && ['VALIDATION_FAILED', 'BUSINESS_VALIDATION_FAILED'].includes(String(response.json?.code ?? ''))) {
        response = await submitUpdate(updateValues)
        if (response.status === 200) {
          effective = updateValues
          findings.push({ moduleId, code: 'UPDATE_CHANGE_REJECTED', level: 'info', stage: 'update', detail: `改单：把 ${changeable.key}（${changeable.label}）改成文本被业务校验拒绝，已退回原样回传` })
        }
      }
      if (response.status >= 500) {
        findings.push({ moduleId, code: 'UPDATE_REJECTED', detail: `改单 ${response.status}：${response.text.slice(0, 200)}`, stage: 'update' })
      } else if (response.status !== 200) {
        // 业务规则不认这组探针值（与建单同一口径）：例如 1418 的"客户订单号重复"——
        // 规则会排除"被本单引用的原订单"，而我造的原单号不存在，排除不掉任何行，
        // 于是撞上真实订单。这不是改单逻辑坏了，是我造出了一组不成对的引用。
        // 只有契约类失败（READONLY_FIELD / TABLE_MISMATCH / 5xx）才算缺陷。
        const businessReject = String(response.json?.code ?? '') === 'BUSINESS_VALIDATION_FAILED'
        findings.push({
          moduleId,
          code: businessReject ? 'UPDATE_VALUE_REJECTED' : 'UPDATE_REJECTED',
          level: businessReject ? 'info' : 'fail',
          detail: `改单被拒 ${response.status} ${response.json?.code ?? ''}：${response.json?.message ?? ''}｜${response.text.slice(0, 300)}`,
          stage: 'update',
          errorCode: response.json?.code ?? null,
        })
      } else {
        const second = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`)
        if (second.status === 200) {
          findings.push(...compareValues(moduleId, fields, effective, second.json?.master ?? {}, '改单', `key=${JSON.stringify(keyValues)}`))
          const secondDetails = Array.isArray(second.json?.details) ? second.json.details : []
          if (details && secondDetails.length < details.length) {
            findings.push({ moduleId, code: 'DETAIL_LOST', detail: `改单：明细从 ${details.length} 行变成 ${secondDetails.length} 行（改单未提交明细，本不该变化）`, stage: 'update' })
          }
        }
      }
    }
    // ---- C 层：并发改单（两个 PUT 同时提交，必须一个成功一个冲突）----
    // 服务端持有列（会被效果链/归一改写）不能用来探并发：我写进去的值本来就不作数，
    // "两次都成功"只说明写入被忽略，不说明有竞态——这类模块直接跳过，不冒充结论。
    const serverOwnedChange = changeable && SERVER_OWNED_KEYS.has(String(changeable.key ?? '').toUpperCase())
    if (boundary && changeable && serverOwnedChange) {
      boundaryFactorRuns.set('CONCURRENT_UPDATE_SKIPPED', (boundaryFactorRuns.get('CONCURRENT_UPDATE_SKIPPED') ?? 0) + 1)
      findings.push({
        moduleId,
        code: 'CONCURRENT_UPDATE_SKIPPED',
        level: 'info',
        field: changeable.key,
        detail: `并发改单未验证：可改的文本列 ${changeable.key} 是服务端持有列（值会被改写），探不了并发`,
      })
    }
    if (boundary && changeable && !serverOwnedChange) {
      const fresh = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`)
      const original = toPlainStrings(fresh.status === 200 ? fresh.json?.master ?? {} : master)
      const makeBody = (suffix, nonce) => ({
        values: {
          ...updateValues,
          [changeable.key]: `${VALUE_PREFIX}C${runTag}${suffix}`.slice(0, Number(changeable.maxLength) || 32),
        },
        original,
        idempotencyKey: `rt-${runTag}-${moduleId}-cc${nonce}`,
      })
      const [ra, rb] = await Promise.all([
        api(page, 'PUT', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`, makeBody('A', 1)),
        api(page, 'PUT', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`, makeBody('B', 2)),
      ])
      const codes = [ra, rb].map(r => String(r.json?.code ?? ''))
      if (ra.status >= 500 || rb.status >= 500) {
        findings.push({ moduleId, code: 'BOUNDARY_5XX', field: changeable.key, detail: `并发改单返回 ${ra.status}/${rb.status}：${(ra.status >= 500 ? ra.text : rb.text).slice(0, 200)}` })
      } else if (ra.status === 200 && rb.status === 200) {
        boundaryFactorRuns.set('CONCURRENT_UPDATE', (boundaryFactorRuns.get('CONCURRENT_UPDATE') ?? 0) + 1)
        const after = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`)
        const finalValue = String(after.json?.master?.[changeable.key] ?? '').trim()
        const submitted = [makeBody('A', 1).values[changeable.key], makeBody('B', 2).values[changeable.key]]
        if (!submitted.includes(finalValue)) {
          // 两次都成功，但最终值既不是 A 也不是 B —— 该列被服务端改写了，不适合探并发
          findings.push({
            moduleId,
            code: 'CONCURRENT_UPDATE_IGNORED',
            level: 'info',
            field: changeable.key,
            detail: `并发改单两次都成功，但最终值「${finalValue}」不是提交的两个值之一（该列由服务端改写），不判竞态`,
          })
        } else {
          // 两次都成功且值真落了 = 后写覆盖先写：前一次修改被静默丢弃，界面上完全看不出来
          findings.push({
            moduleId,
            code: 'CONCURRENT_UPDATE_LOST',
            field: changeable.key,
            detail: `并发改单两次都成功（最终值「${finalValue}」）——后写覆盖先写，前一次修改被静默丢弃`,
          })
        }
      } else if (codes.every(code => code === 'BUSINESS_VALIDATION_FAILED' || code === 'VALIDATION_FAILED')) {
        // 两次都被业务/值校验挡下（探针值不合业务，如 1418 的"客户订单号重复"）：
        // 并发校验这一步根本没走到，记 info，不冒充"并发没问题"，也不当成缺陷。
        boundaryFactorRuns.set('CONCURRENT_UPDATE_SKIPPED', (boundaryFactorRuns.get('CONCURRENT_UPDATE_SKIPPED') ?? 0) + 1)
        findings.push({
          moduleId,
          code: 'CONCURRENT_UPDATE_SKIPPED',
          level: 'info',
          field: changeable.key,
          detail: `并发改单未验证：两次都被 ${codes[0]} 挡下（探针值不合业务）`,
        })
      } else if (!codes.includes('CONCURRENT_MODIFIED')) {
        boundaryFactorRuns.set('CONCURRENT_UPDATE', (boundaryFactorRuns.get('CONCURRENT_UPDATE') ?? 0) + 1)
        findings.push({
          moduleId,
          code: 'CONCURRENT_UPDATE_NO_CODE',
          field: changeable.key,
          detail: `并发改单：${ra.status}/${rb.status}，失败那次不是 CONCURRENT_MODIFIED（${codes.join(' / ') || '无 code'}）`,
        })
      } else {
        boundaryFactorRuns.set('CONCURRENT_UPDATE', (boundaryFactorRuns.get('CONCURRENT_UPDATE') ?? 0) + 1)
        findings.push({
          moduleId,
          code: 'CONCURRENT_UPDATE_OK',
          level: 'info',
          field: changeable.key,
          detail: '并发改单：一个成功、另一个 CONCURRENT_MODIFIED，未丢更新',
        })
      }
    }
    return { moduleId, capability: 'swept', title: form.title, key: keyValues }
  } finally {
    // ---- 删除：无论上面怎么退出，都把自己造的那条清掉 ----
    if (keyValues && !keepRecords) {
      const keyQuery = new URLSearchParams({ key: JSON.stringify(keyValues) })
      // DELETE 没有请求体，幂等键只能走请求头——漏了这个头会被 400 IDEMPOTENCY_KEY_REQUIRED 拒掉，
      // 而那正是"清理失败、留下孤儿记录"的直接原因。
      const removed = await api(page, 'DELETE', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`, null, {
        'X-Idempotency-Key': `rt-${runTag}-${moduleId}-d${Date.now()}`,
      })
      if (removed.status !== 200 && removed.status !== 204) {
        findings.push({ moduleId, code: 'DELETE_FAILED', detail: `删除 ${removed.status}：${removed.text.slice(0, 160)}`, key: keyValues })
        extra.orphans.push({ moduleId, key: keyValues })
      } else {
        const after = await api(page, 'GET', `/api/v1/document-workbench/${moduleId}/record?${keyQuery}`)
        if (after.status === 200) {
          findings.push({ moduleId, code: 'DELETE_NOT_EFFECTIVE', detail: `删除返回 ${removed.status}，但记录仍能读到`, key: keyValues })
          extra.orphans.push({ moduleId, key: keyValues })
        }
      }
    } else if (keyValues && keepRecords) {
      extra.orphans.push({ moduleId, key: keyValues, keptByRequest: true })
    }
  }
}

// ===== 主流程 =====
mkdirSync(outDir, { recursive: true })
const runTag = Math.random().toString(36).slice(2, 6).toUpperCase()
const findings = []
const extra = { orphans: [] }
const moduleReports = []
let exitCode = 0

const browser = await chromium.launch({ executablePath: chromePath, headless: true })
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  page.on('pageerror', error => console.log(`pageerror: ${error.message}`))

  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', process.env.EOS_SWEEP_USER || 'admin')
  await page.fill('#login-password', process.env.EOS_SWEEP_PASSWORD || 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 20000 })

  let moduleIds = onlyModules
  if (moduleIds.length === 0) {
    const routes = await page.evaluate(async () => {
      const response = await fetch('/api/v1/app/bootstrap', { headers: { accept: 'application/json' } })
      const json = await response.json()
      const collected = []
      const walk = nodes => {
        for (const node of nodes ?? []) {
          const match = /^\/workbench\/(\d+)$/.exec(String(node.route ?? ''))
          if (match && node.moduleId) collected.push(Number(node.moduleId))
          walk(node.children)
        }
      }
      walk(json.navigation)
      return [...new Set(collected)].sort((a, b) => a - b)
    })
    moduleIds = routes
  }
  if (limit > 0) moduleIds = moduleIds.slice(0, limit)

  let index = 0
  for (const moduleId of moduleIds) {
    index++
    let outcome
    try {
      outcome = await roundtrip(page, moduleId, runTag, findings, extra)
    } catch (error) {
      outcome = { moduleId, capability: 'threw' }
      findings.push({ moduleId, code: 'CREATE_SERVER_ERROR', detail: `扫描抛异常：${error.message}` })
    }
    moduleReports.push(outcome)
    const failed = findings.filter(item => item.moduleId === moduleId && item.level !== 'info')
    const mark = failed.length === 0 ? (outcome.capability === 'swept' || outcome.capability === 'planned' ? 'PASS' : `SKIP(${outcome.capability})`) : `FAIL×${failed.length}`
    console.log(`[${index}/${moduleIds.length}] ${moduleId} ${mark}`)
  }

  // ---- 报告与基线（info 只是观察项，不进棘轮、不阻断）----
  const fails = findings.filter(item => item.level !== 'info')
  const infos = findings.filter(item => item.level === 'info')
  const signature = item => `${item.moduleId}|${item.code}|${item.field ?? ''}`
  let newFindings = fails
  let resolved = []
  if (baselinePath && existsSync(baselinePath)) {
    const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'))
    const known = new Set((baseline.findings ?? []).map(signature))
    newFindings = fails.filter(item => !known.has(signature(item)))
    const currentKeys = new Set(fails.map(signature))
    resolved = (baseline.findings ?? []).filter(item => !currentKeys.has(signature(item)))
  }
  if (updateBaseline && baselinePath) {
    mkdirSync(resolve(baselinePath, '..'), { recursive: true })
    writeFileSync(baselinePath, JSON.stringify({ generatedAt: new Date().toISOString(), findings: fails }, null, 2), 'utf8')
  }

  const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14)
  const byCode = {}
  for (const item of fails) byCode[item.code] = (byCode[item.code] ?? 0) + 1
  const swept = moduleReports.filter(item => item.capability === 'swept' || item.capability === 'planned').length
  const capability = moduleReports.reduce((acc, item) => { acc[item.capability] = (acc[item.capability] ?? 0) + 1; return acc }, {})
  writeFileSync(join(outDir, `record-roundtrip-${stamp}.json`), JSON.stringify({
    generatedAt: new Date().toISOString(), baseUrl, runTag,
    modulesScanned: moduleIds.length, modulesSwept: swept, capability,
    failByCode: byCode,
    newFailByCode: newFindings.reduce((acc, item) => { acc[item.code] = (acc[item.code] ?? 0) + 1; return acc }, {}),
    baseline: baselinePath ?? null, resolvedSinceBaseline: resolved.length,
    findings, modules: moduleReports, orphans: extra.orphans,
  }, null, 2), 'utf8')

  const lines = [
    `# 统一表单提交回环 ${stamp}（runTag=${runTag}）`,
    '',
    `- 扫描模块：${moduleIds.length}（走完回环 ${swept} 个）`,
    `- 能力分类：${Object.entries(capability).map(([key, value]) => `${key}=${value}`).join(' / ') || '—'}`,
    `- FAIL 合计：${fails.length}（按判据：${Object.entries(byCode).map(([key, value]) => `${key}=${value}`).join(' / ') || '—'}）`,
    `- 观察项（info，不计失败）：${infos.length}`,
    boundary
      ? `- 负面边界覆盖：${Object.entries(Object.fromEntries(boundaryFactorRuns)).sort((a, b) => b[1] - a[1]).map(([factor, count]) => `${factor}×${count}`).join(' / ') || '（未执行任何因子）'}`
      : '',
    baselinePath ? `- 相对基线新增：${newFindings.length}；基线中已消失：${resolved.length}` : '- 未提供基线（全部按新增计）',
    extra.orphans.length > 0 ? `- ⚠ 清理未成功的记录：${extra.orphans.length} 条（见下表）` : '',
    '',
    '| 模块 | 判据 | 明细 |',
    '|---|---|---|',
    ...newFindings.slice(0, 500).map(item => `| ${item.moduleId} | ${item.code} | ${String(item.detail).replace(/\|/g, '/')} |`),
  ]
  if (extra.orphans.length > 0) {
    lines.push('', '## 残留记录（需人工清理）', '', '| 模块 | 主键 |', '|---|---|')
    for (const orphan of extra.orphans) lines.push(`| ${orphan.moduleId} | ${JSON.stringify(orphan.key)} |`)
  }
  writeFileSync(join(outDir, `record-roundtrip-${stamp}.md`), lines.filter(Boolean).join('\n'), 'utf8')

  console.log(`\n===== 提交回环汇总 =====`)
  console.log(`扫描 ${moduleIds.length} 个模块；走完回环 ${swept} 个`)
  console.log(`FAIL 合计 ${fails.length}${baselinePath ? `（相对基线新增 ${newFindings.length}）` : ''}；观察项 ${infos.length}`)
  for (const [code, count] of Object.entries(byCode).sort((a, b) => b[1] - a[1])) console.log(`  ${code}: ${count}`)
  if (boundary) {
    // 覆盖度必须打出来：某因子"没跑到"和"跑了没发现问题"是两件事，
    // 报告里不写清就会被读成绿（同 LESSONS L40：假绿比没有断言更糟）。
    const coverage = Object.fromEntries([...boundaryFactorRuns.entries()].sort((a, b) => b[1] - a[1]))
    console.log(`负面边界覆盖：${Object.entries(coverage).map(([factor, count]) => `${factor}×${count}`).join(' / ') || '（未执行任何因子）'}`)
  }
  if (extra.orphans.length > 0) console.log(`⚠ 清理未成功 ${extra.orphans.length} 条，报告里列了主键`)
  console.log(`报告：${join(outDir, `record-roundtrip-${stamp}.md`)}`)
  exitCode = newFindings.length > 0 ? 1 : 0
} finally {
  await browser.close()
}
process.exit(exitCode)
