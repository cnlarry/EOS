/**
 * 统一表单「渲染扫描」（广扫体检，非门禁）
 *
 * 逐个可写模块打开 /workbench/{id}/new，**只渲染、不提交、不写库**，把元数据声明的字段
 * 与界面上真正渲染出来的控件做差集，回答一个问题：**该有值的地方，用户到底能不能填？**
 *
 * 判据（FAIL 级，逐条对应一个缺陷类型）：
 *   FIELD_MISSING          元数据标为可见的字段没有渲染出来（版式/装箱吞字段）
 *   FIELD_TAB_UNREACHABLE  字段的 tabNo 不在任何页签里，切遍页签也看不到它
 *   CONTROL_MISSING        字段渲染了，但里面没有输入控件也没有只读文本
 *   WRITABLE_NOT_EDITABLE  可写字段被渲染成不可编辑（用户永远填不上）★核心
 *   REQUIRED_NOT_FILLABLE  必填且可写的字段不可编辑 ★核心
 *   REQUIRED_NO_MARKER     必填可写字段的标签没有「 *」必填标记
 *   READONLY_EDITABLE      只读/服务端填充字段被渲染成可编辑（反向断言）
 *   RENDERING_DEVIATION    服务端填充且无选择器的字段应当只读文本，却给了输入控件
 *   DEFAULT_NOT_APPLIED    元数据有默认值（服务端 defaultValues 或字段 defaultValue）却没落到控件上
 *   CHOOSER_MISSING        字段声明了生效中的数据源，却没有选择按钮
 *   CHOOSER_DISABLED       选择按钮被禁用（有数据源即应当可用）
 *   SELECT_NO_OPTION       下拉没有任何可选候选（必填时才算 FAIL）
 *   DETAIL_HEADER_MISSING  明细可见字段在子表列头里找不到
 *   DETAIL_GATE_NOT_A_FIELD 明细门禁（DETAIL_NO_FIELDS）点名了不在主表录入面上的字段 ★
 *   DETAIL_GATE_UNFILLABLE 明细门禁字段存在但用户无路可填 ★
 *   EXTRA_FIELD            DOM 里有元数据不认识的字段键
 *   FORM_NOT_RENDERED      表单卡整页渲染不出来
 *   SERVER_ERROR           接口 5xx
 *   PAGE_ERROR             页面脚本异常 / 控制台错误
 *
 * 上面两条 DETAIL_GATE_* 的后果是同一条：**该模块的明细行加不出来**——「新增一行」要求门禁字段先有值，
 * 而这个值用户无论如何都给不出。★ = 直接废掉一个界面功能，优先级最高。
 *
 * 另记 info 级观察（不作为失败）：STATIC_EMPTY（只读静态字段进页面为空）、
 * REQUIRED_READONLY_EMPTY（只读必填列进页面为空——联动列的设计行为，先观察再定）、
 * DEFAULT_MISMATCH（默认值落上了但与元数据字面不同，多为展示格式差异）。
 *
 * 用法：
 *   node scripts/render-sweep.mjs [webBaseUrl] [选项]
 *   选项：
 *     --modules=1401,1601     只扫点名模块
 *     --limit=20              只扫前 N 个
 *     --baseline=<路径>       与该基线比对，只报新增（棘轮：只减不增）
 *     --update-baseline       把本次结果写回基线
 *     --inject=<case>         判别性注入（证明判据能失败），见 INJECTIONS
 *     --selftest              对每个注入用例各跑一遍，断言"注入必被抓住"
 *     --headed                显示浏览器（排查用）
 *     --out=<目录>            报告输出目录，默认 logs/render-sweep
 *   环境变量：PW_CORE_PATH（playwright-core 安装目录）、EOS_CHROME_PATH（Chrome 可执行文件）
 *
 * 纪律：本脚本只读。任何"顺手写一笔"的改动都会让它从体检工具变成污染源。
 */
import { createRequire } from 'module'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'fs'
import { join, resolve } from 'path'

const require = createRequire(import.meta.url)
/**
 * playwright-core 在本机没有随仓库安装（避免几 MB 的依赖进版本控制），
 * 按候选顺序探测；都找不到时才回落到裸包名，让 Node 自己去解析。
 * 浏览器一律用系统 Chrome（EOS_CHROME_PATH），不依赖托管浏览器下载。
 */
function loadChromium() {
  const candidates = [
    process.env.PW_CORE_PATH,
    join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core'),
    join(process.env.USERPROFILE || '', '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules', 'playwright-core'),
    'playwright-core',
    '@playwright/test',
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
const baseUrl = args.find(a => !a.startsWith('--')) || process.env.EOS_WEB_URL || 'http://localhost:80'
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const option = (name, fallback = null) => {
  const hit = args.find(a => a.startsWith(`--${name}=`))
  return hit ? hit.slice(name.length + 3) : (args.includes(`--${name}`) ? true : fallback)
}
const onlyModules = String(option('modules', '') || '').split(',').map(s => s.trim()).filter(Boolean).map(Number)
const limit = Number(option('limit', 0)) || 0
const outDir = resolve(String(option('out', join('logs', 'render-sweep'))))
const baselinePath = option('baseline', null)
const updateBaseline = Boolean(option('update-baseline', false))
const injectCase = option('inject', null)
const runSelftest = Boolean(option('selftest', false))
const headed = Boolean(option('headed', false))

/** 判别性注入用例：每条注入一种"该被抓到"的坏法，配上它必须触发的判据码。 */
const INJECTIONS = {
  'hide-field': { expect: 'FIELD_MISSING', note: '删掉第一个可写字段的整个字段块' },
  'drop-control': { expect: 'CONTROL_MISSING', note: '删掉第一个可写字段的输入控件' },
  'disable-required': { expect: 'REQUIRED_NOT_FILLABLE', note: '把第一个必填可写字段的控件置为 disabled' },
  'blank-default': { expect: 'DEFAULT_NOT_APPLIED', note: '清空第一个带默认值的控件的值', needsValue: true },
  'drop-chooser': { expect: 'CHOOSER_MISSING', note: '删掉第一个选择按钮', needsChooser: true },
}

// ===== 元数据期望（与 EOS.Web/src/features/document-workbench/FormFieldRenderer.tsx 的渲染分支一一对应）=====
const activeChoosers = field => (field.choosers ?? []).filter(source => source.active && source.table)
/** 渲染分支同源：下拉变体不渲染选择按钮（选了就往 select 里填），其余变体才看数据源。 */
const hasChooser = field => fieldVariant(field) !== 'select' && activeChoosers(field).length > 0
/** 只读文本（不是输入控件）：服务端填充且没有选择器。 */
const expectStatic = field => field.serverFilled === true && !hasChooser(field)
/**
 * 数据仅可选入（FIELDS.ONLY_CHOOSE）：镜像 FormFieldRenderer 的 chooseOnlyLocked——
 * 只锁"能自由键入"的文本框/数值框，且必须真有可用选择入口（没有来源还锁，字段就永远填不上，
 * 存量里就有这种元数据缺口）；下拉、复选与日期本身即选择控件，不在锁定范围内。
 * 只读/服务端填充的字段本来就 disabled（联动列由选择器带出），不靠这条再去锁。
 */
const chooseOnlyLocked = field => field.onlyChoose === true && field.isReadonly !== true && field.serverFilled !== true
  && hasChooser(field) && (fieldVariant(field) === 'text' || fieldVariant(field) === 'decimal')
/** 控件应当 disabled：只读，或服务端填充但有选择器（只读联动列，选择按钮仍可用），或只能选入。 */
const expectDisabled = field => field.serverFilled === true || field.isReadonly === true || chooseOnlyLocked(field)
/** 用户可写：既非只读也非服务端填充，也没被"只能选入"锁住键入。 */
const expectEditable = field => field.serverFilled !== true && field.isReadonly !== true && !chooseOnlyLocked(field)
/**
 * 主表金额列在「明细表有 AMOUNT 列」时被强制只读——那几列保存后由服务端按明细权威汇总，
 * 不让用户填是设计行为，不是缺陷。镜像 FormEditorPage 的 masterAmountLocked 与
 * amountCalculator 的 AMOUNT_COLUMN_KEYS；这条豁免只覆盖"主表金额列"，不外扩。
 */
const AMOUNT_COLUMN_KEYS = new Set(['AMOUNT', 'TAX_SUM', 'AMOUNT_TAX'])
const isMasterAmountLocked = (form, field) =>
  (form.detailFields ?? []).some(item => String(item.key ?? '').toUpperCase() === 'AMOUNT')
  && AMOUNT_COLUMN_KEYS.has(String(field.key ?? '').toUpperCase())
const fieldVariant = field => {
  const type = String(field.dataType ?? '').toLowerCase()
  if (type.includes('bit')) return 'checkbox'
  if ((field.options ?? []).length > 0) return 'select'
  if (/date|time/.test(type)) return type.includes('date') ? 'date' : 'datetime'
  if (/decimal|numeric|money|float|real|int/.test(type)) return 'decimal'
  return 'text'
}

const findings = []
const moduleReports = []

function report(moduleId, code, detail, level = 'fail', extra = {}) {
  findings.push({ moduleId, code, detail, level, ...extra })
}

// ===== 页面侧：抽取字段渲染事实 =====
/** 从表单卡里把每个字段键的渲染事实读出来（控件类型/是否可编辑/值/选择按钮/标签）。 */
const PROBE_SCOPE = `(() => {
  const scope = document.querySelector('.erp-form-card')
  if (!scope) return null
  const read = (key) => {
    const fieldNode = scope.querySelector('.erp-form-field[data-field-key="' + CSS.escape(key) + '"]')
    const container = fieldNode ?? scope.querySelector('.erp-form-control[data-field-key="' + CSS.escape(key) + '"]')
    if (!container) return { key, present: false }
    const controlBox = container.classList.contains('erp-form-control')
      ? container
      : container.querySelector('.erp-form-control')
    const label = (fieldNode ?? container.closest('.erp-form-cell'))?.querySelector('.erp-form-label')
    // 复合格 [主][从] 只在主字段旁边渲染一个标签：从字段没有自己的标签，
    // 因此"标签上的必填星号"只能对主字段断言，对从字段读到的其实是主字段的标签
    const cell = controlBox?.closest('.erp-form-cell') ?? null
    const cellOwner = cell?.querySelector('.erp-form-cell-controls > .erp-form-control[data-field-key]')
    const cellMain = cellOwner ? cellOwner.dataset.fieldKey === key : true
    const chooser = controlBox?.querySelector('button.erp-chooser-btn') ?? null
    const staticNode = controlBox?.querySelector('.erp-form-static') ?? null
    const input = controlBox?.querySelector('input, select, textarea') ?? null
    let value = '', optionCount = 0, selectableCount = 0
    if (input) {
      if (input.tagName === 'SELECT') {
        const opts = [...input.options]
        optionCount = opts.filter(o => o.value !== '').length
        selectableCount = opts.filter(o => o.value !== '' && !o.disabled).length
        value = input.value
      } else if (input.type === 'checkbox') {
        value = input.checked ? '1' : '0'
      } else {
        value = input.value
      }
    }
    return {
      key,
      present: true,
      bare: !fieldNode,
      cellMain,
      fieldClasses: fieldNode ? [...fieldNode.classList] : [],
      tag: input ? input.tagName.toLowerCase() : (staticNode ? 'static' : 'none'),
      inputType: input?.type ?? null,
      disabled: input ? Boolean(input.disabled) : null,
      readOnly: input ? Boolean(input.readOnly) : null,
      value,
      optionCount,
      selectableCount,
      staticText: staticNode ? String(staticNode.textContent ?? '').trim() : null,
      chooserPresent: Boolean(chooser),
      chooserDisabled: chooser ? Boolean(chooser.disabled) : null,
      chooserTitle: chooser?.getAttribute('title') ?? null,
      labelText: label ? String(label.textContent ?? '').replace(/\\s+/g, ' ').trim() : null,
    }
  }
  void read
  return read
})()`

/**
 * 往「新增明细行」的门禁字段里写探针值。返回值逐键说明它为什么没被写上。
 *
 * 只改浏览器里的表单状态，**永不保存**：这些值没有任何机会落到库里。
 * React 受控输入必须经原生 value setter 再派发事件——直接赋 `input.value` 会被
 * React 的 value tracker 判成"没变"，状态不更新，后面那道门禁照样拦人。
 * 探针值刻意带 ZZ 前缀自成标识，万一日后有人拿它去真保存，脏数据一眼可辨。
 */
function fillGateFields({ keys, allowChooserFields = false }) {
  const outcome = []
  for (const key of keys) {
    const escaped = window.CSS.escape(key)
    const box = document.querySelector(`.erp-form-card .erp-form-control[data-field-key="${escaped}"]`)
      ?? document.querySelector(`.erp-form-card [data-field-key="${escaped}"] .erp-form-control`)
    if (!box) { outcome.push({ key, ok: false, reason: '本页签无此控件', found: false }); continue }
    const chooserButton = box.querySelector('button.erp-chooser-btn')
    // 「选择按钮是否可用」是独立事实：只读联动列的唯一入口就是它
    const chooserUsable = Boolean(chooserButton) && chooserButton.disabled !== true
    const input = box.querySelector('input:not([type=checkbox]), select, textarea')
    const base = { found: true, chooserUsable }
    if (!input) { outcome.push({ key, ...base, ok: false, reason: chooserUsable ? '只读呈现' : '只读呈现且无可用数据源' }); continue }
    if (input.disabled) { outcome.push({ key, ...base, ok: false, reason: chooserUsable ? '不可编辑（可用选择器）' : '不可编辑且无可用数据源' }); continue }
    if (String(input.value ?? '').trim() !== '') { outcome.push({ key, ...base, ok: true }); continue }
    // 有可用选择器的字段优先交给选择器取值：这类字段的值会变成别的选择器的**筛选条件**
    // （如采购单选择器按当前厂商过滤），填个假值会让那边一条候选都查不出来——扫描器自己把路堵死。
    // 只有选择器确实取不到值时才由调用方允许退回写字（`allowChooserFields`），目的仅是把明细表
    // 顶出空表态以完成列头对拍，不是在断言这条值合法。
    if (chooserUsable && !allowChooserFields) { outcome.push({ key, ...base, ok: false, reason: '交由选择器取值' }); continue }
    if (input.tagName === 'SELECT') {
      const option = [...input.options].find(item => item.value !== '' && item.disabled !== true)
      if (!option) { outcome.push({ key, ...base, ok: false, reason: '无候选' }); continue }
      Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set.call(input, option.value)
      input.dispatchEvent(new Event('change', { bubbles: true }))
      outcome.push({ key, ...base, ok: true })
      continue
    }
    const raw = /date/.test(input.type) ? new Date().toISOString().slice(0, 10) : 'ZZ'
    const limit = Number.isFinite(input.maxLength) && input.maxLength > 0 ? input.maxLength : 32
    Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set.call(input, raw.slice(0, limit))
    input.dispatchEvent(new Event('input', { bubbles: true }))
    outcome.push({ key, ...base, ok: true })
  }
  return outcome
}

/**
 * 联动回填来源：`RETURN_ITEMS` 是 `[{target,column}]`，读作"选完这个字段的值，就把 column
 * 回填到 target"。倒过来建索引 `target → [来源字段键]`，能回答两种问题：
 *   · 一个"只读、又没有自己数据源"的字段**有没有路可填**（没有 ⇒ `DETAIL_GATE_UNFILLABLE`）；
 *   · 有路的话，**该去点哪个字段的选择器**才能把它带出来（只读联动列的唯一填法）。
 */
function collectReturnSources(masterFields) {
  const sources = new Map()
  for (const field of masterFields ?? []) {
    for (const source of field.choosers ?? []) {
      if (!source.active || !source.table || !source.returnMapping) continue
      let items = null
      try { items = JSON.parse(source.returnMapping) } catch { continue } // 历史配置里可能是非 JSON 文本
      for (const item of Array.isArray(items) ? items : []) {
        const target = String(item?.target ?? '').toUpperCase()
        if (!target) continue
        if (!sources.has(target)) sources.set(target, new Set())
        sources.get(target).add(field.key)
      }
    }
  }
  return sources
}

/**
 * 用统一选择器给一个字段赋值：点选择按钮 → （多来源时先点来源菜单）→ 弹窗里选第一行 → 确认。
 *
 * 只读联动列（如币别）没有可编辑输入框，唯一入口就是这条交互路径——所以补门禁字段时
 * 必须真的走一遍"点击 + 选择"，光往控件里赋值是够不着的。
 * 取第一行即止：扫描要的是"这条路通不通"，不是"取到哪一行"。
 * 所有交互都只落在浏览器状态上，本脚本从不提交，因而不会因为这里选了什么而改动库。
 */
/** 在已打开的选择器里挑第一行并确认。没有可选行（空来源）时返回 false，弹窗留给调用方处置。 */
async function takeFirstChooserRow(page, dialog) {
  const radios = dialog.locator('tbody input[aria-label="选择此行"]')
  // 行要等服务端分页查询回来；大表（上万行）首次打开可能慢到十几秒
  const ready = await radios.first().waitFor({ state: 'visible', timeout: 20000 }).then(() => true).catch(() => false)
  if (!ready) return false
  await radios.first().check({ timeout: 5000 })
    .catch(async () => { await dialog.locator('tbody tr').first().click({ timeout: 3000 }).catch(() => null) })
  const confirm = dialog.locator('button', { hasText: '确认' }).first()
  if (await confirm.isDisabled().catch(() => true)) return false
  await confirm.click({ timeout: 5000 }).catch(() => null)
  await page.waitForTimeout(250)
  return true
}

async function pickViaChooser(page, key) {
  if (!/^[A-Za-z0-9_]+$/.test(key)) return false
  const button = page.locator(`.erp-form-card .erp-form-control[data-field-key="${key}"] button.erp-chooser-btn`).first()
  if (await button.count() === 0) return false
  if (await button.isDisabled().catch(() => true)) return false

  const dialog = page.locator('.erp-chooser-dialog').first()
  const menuItems = page.locator('.erp-source-menu [role="menuitem"]')
  const cancel = async () => {
    await dialog.locator('button', { hasText: '取消' }).first().click({ timeout: 3000 }).catch(() => null)
    await page.waitForTimeout(250)
  }

  await button.click({ timeout: 5000 }).catch(() => null)
  // 点下去之后有两条路：直接进选择器（单来源），或先弹来源菜单（多来源）。
  // 两条都可能比一次渲染慢（来源菜单要算方向、选择器要发一次分页查询），故轮询等"任一路出现"。
  let stage = null
  const deadline = Date.now() + 8000
  while (Date.now() < deadline && stage === null) {
    if (await dialog.isVisible().catch(() => false)) stage = 'dialog'
    else if (await menuItems.first().isVisible().catch(() => false)) stage = 'menu'
    else await page.waitForTimeout(150)
  }
  if (stage === null) return false

  if (stage === 'dialog') {
    if (await takeFirstChooserRow(page, dialog)) return true
    await cancel()
    return false
  }

  // 多来源：**逐个来源都试一遍**。同一字段的几条来源筛选口径不同（如"未收料采购单 / 已收料采购单"），
  // 当前库里某一口径没有数据是常态——只试第一条会把本来能填的模块误判成"用户无路可填"。
  const count = await menuItems.count()
  for (let index = 0; index < count; index++) {
    if (index > 0) {
      await button.click({ timeout: 5000 }).catch(() => null)
      const shown = await menuItems.nth(index).waitFor({ state: 'visible', timeout: 8000 }).then(() => true).catch(() => false)
      if (!shown) break
    }
    await menuItems.nth(index).click({ timeout: 5000 }).catch(() => null)
    const opened = await dialog.waitFor({ state: 'visible', timeout: 12000 }).then(() => true).catch(() => false)
    if (!opened) continue
    if (await takeFirstChooserRow(page, dialog)) return true
    await cancel() // 这一条来源没有可选数据，换下一条
  }
  return false
}

const injectMap = {
  'hide-field': `(() => {
    const node = [...document.querySelectorAll('.erp-form-card .erp-form-field[data-field-key]')]
      .find(el => !el.classList.contains('is-static') && !el.classList.contains('is-readonly'))
    if (!node) return null
    const key = node.dataset.fieldKey
    node.remove()
    return key
  })()`,
  'drop-control': `(() => {
    const node = [...document.querySelectorAll('.erp-form-card .erp-form-field[data-field-key]')]
      .find(el => !el.classList.contains('is-static') && !el.classList.contains('is-readonly'))
    if (!node) return null
    const key = node.dataset.fieldKey
    node.querySelector('.erp-form-control')?.remove()
    return key
  })()`,
  'disable-required': `(() => {
    // 靶子必须是"本可编辑"的必填字段：只能选入（ONLY_CHOOSE）的字段本来就是 disabled，
    // 拿它当靶子注入等于什么也没改，自检会假通过
    const node = [...document.querySelectorAll('.erp-form-card .erp-form-field')].find(el => {
      if (!/\\*$/.test((el.querySelector('.erp-form-label')?.textContent ?? '').trim())) return false
      const input = el.querySelector('input:not([type=checkbox]), select, textarea')
      return Boolean(input) && !input.disabled
    })
    const key = node?.dataset.fieldKey
    const input = node?.querySelector('input:not([type=checkbox]), select, textarea')
    if (!input) return null
    input.disabled = true
    return key
  })()`,
  'blank-default': `(() => {
    const nodes = [...document.querySelectorAll('.erp-form-card .erp-form-control[data-field-key]')]
    const hit = nodes.find(el => {
      const input = el.querySelector('input:not([type=checkbox]), select, textarea')
      return input && !input.disabled && input.value
    })
    if (!hit) return null
    const input = hit.querySelector('input:not([type=checkbox]), select, textarea')
    const key = hit.dataset.fieldKey
    if (input.tagName === 'SELECT') input.value = ''
    else input.value = ''
    input.dispatchEvent(new Event('input', { bubbles: true }))
    input.dispatchEvent(new Event('change', { bubbles: true }))
    return key
  })()`,
  'drop-chooser': `(() => {
    const node = document.querySelector('.erp-form-card button.erp-chooser-btn')
    if (!node) return null
    const key = node.closest('[data-field-key]')?.dataset.fieldKey ?? null
    node.remove()
    return key
  })()`,
}

// ===== 断言：把元数据与渲染事实逐字段对拍 =====
function checkMasterField(moduleId, field, rendered, defaults, tabNo, locked = false) {
  if (!rendered?.present) {
    report(moduleId, 'FIELD_MISSING', `字段 ${field.key}（${field.label}）可见但未渲染`, 'fail', { field: field.key, tabNo })
    return
  }
  if (rendered.tag === 'none') {
    report(moduleId, 'CONTROL_MISSING', `字段 ${field.key}（${field.label}）渲染了但既无输入控件也无只读文本`, 'fail', { field: field.key, tabNo })
    return
  }
  const variant = fieldVariant(field)
  const staticExpected = expectStatic(field)
  const disabledExpected = expectDisabled(field) || locked
  const editableExpected = expectEditable(field) && !locked

  if (staticExpected && rendered.tag !== 'static') {
    report(moduleId, 'RENDERING_DEVIATION', `字段 ${field.key}（${field.label}）非只读且无数据源，应当只读呈现，实际给了 ${rendered.tag}`, 'fail', { field: field.key, tabNo })
  }
  if (!staticExpected && editableExpected && rendered.disabled) {
    report(moduleId, 'WRITABLE_NOT_EDITABLE', `字段 ${field.key}（${field.label}）用户可填却不可编辑（${rendered.tag} disabled）`, 'fail', { field: field.key, tabNo })
    if (field.isRequired) {
      report(moduleId, 'REQUIRED_NOT_FILLABLE', `必填字段 ${field.key}（${field.label}）不可编辑，必填项永远填不上`, 'fail', { field: field.key, tabNo })
    }
  }
  if (!staticExpected && disabledExpected && !rendered.disabled) {
    report(moduleId, 'READONLY_EDITABLE', `字段 ${field.key}（${field.label}）只读/服务端填充，却渲染成可编辑控件`, 'fail', { field: field.key, tabNo })
  }
  // 必填标记：源码条件 = 必填 且 非只读文本 且 非复选框 且（控件未禁用 或 只能选入）。
  // 只能选入的字段只是"不能手输"，仍然要用户去选择器里选，星号照常显示。
  // 复合格 [主][从] 只有一个标签（属于主字段），从字段旁边本来就没有星号，故只对"标签持有者"断言。
  const expectMarker = field.isRequired && !staticExpected && variant !== 'checkbox'
    && (!rendered.disabled || chooseOnlyLocked(field)) && rendered.cellMain !== false
  if (expectMarker && rendered.labelText && !rendered.labelText.trimEnd().endsWith('*')) {
    report(moduleId, 'REQUIRED_NO_MARKER', `必填字段 ${field.key}（${field.label}）标签缺少必填标记：${rendered.labelText}`, 'fail', { field: field.key, tabNo })
  }
  // 默认值落地：新增态初值 = defaultValues[key] ?? field.defaultValue ?? (bit ? '0' : '')
  const bit = variant === 'checkbox'
  const expected = defaults[field.key] ?? field.defaultValue ?? (bit ? '0' : '')
  if (expected !== '' && !rendered.key.endsWith('__skip')) {
    const shown = rendered.tag === 'static' ? (rendered.staticText ?? '') : rendered.value
    const empty = shown === '' || shown === '—'
    if (empty) {
      report(moduleId, 'DEFAULT_NOT_APPLIED', `字段 ${field.key}（${field.label}）元数据默认值「${expected}」未落到界面（界面为空）`, 'fail', { field: field.key, tabNo })
    } else if (rendered.tag !== 'static' && rendered.inputType !== 'date' && rendered.inputType !== 'datetime-local' && shown !== expected) {
      report(moduleId, 'DEFAULT_MISMATCH', `字段 ${field.key}（${field.label}）默认值 ${expected} 显示为 ${shown}`, 'info', { field: field.key, tabNo })
    }
  }
  // 选择器
  if (hasChooser(field)) {
    if (!rendered.chooserPresent) {
      report(moduleId, 'CHOOSER_MISSING', `字段 ${field.key}（${field.label}）有生效数据源却没有选择按钮`, 'fail', { field: field.key, tabNo })
    } else if (rendered.chooserDisabled) {
      report(moduleId, 'CHOOSER_DISABLED', `字段 ${field.key}（${field.label}）的选择按钮被禁用`, 'fail', { field: field.key, tabNo })
    }
  }
  // 下拉候选
  if (variant === 'select' && rendered.tag === 'select') {
    if (rendered.selectableCount === 0) {
      const code = field.isRequired && editableExpected ? 'SELECT_NO_OPTION' : null
      report(moduleId, code ?? 'SELECT_NO_OPTION', `字段 ${field.key}（${field.label}）下拉没有可选候选（共 ${rendered.optionCount} 项，全部不可选）`, field.isRequired && editableExpected ? 'fail' : 'info', { field: field.key, tabNo })
    }
  }
  // 观察项：只读文本进页面即空
  if (rendered.tag === 'static' && (rendered.staticText === '' || rendered.staticText === '—')) {
    report(moduleId, field.isRequired ? 'REQUIRED_READONLY_EMPTY' : 'STATIC_EMPTY', `字段 ${field.key}（${field.label}）只读静态呈现且进页面为空`, 'info', { field: field.key, tabNo })
  }
}

// ===== 单模块扫描 =====
async function sweepModule(page, moduleId, errors, { inject }) {
  await page.goto(`${baseUrl}/workbench/${moduleId}/new`, { waitUntil: 'domcontentloaded' })
  const formResp = await page.evaluate(async id => {
    const response = await fetch(`/api/v1/document-workbench/${id}/form-definition?mode=new`, { headers: { accept: 'application/json' } })
    const text = await response.text()
    let json = null
    try { json = JSON.parse(text) } catch { /* 非 JSON 响应体（ProblemDetails 之外的形态） */ }
    return { status: response.status, json, text: text.slice(0, 400) }
  }, moduleId)

  if (formResp.status >= 500) {
    report(moduleId, 'SERVER_ERROR', `form-definition 返回 ${formResp.status}：${formResp.text.slice(0, 160)}`, 'fail')
    return { moduleId, capability: 'server-error', status: formResp.status }
  }
  if (formResp.status === 403 || formResp.status === 404) {
    return { moduleId, capability: 'not-writable', status: formResp.status }
  }
  if (formResp.status === 400) {
    report(moduleId, 'SERVER_ERROR', `form-definition 返回 400：${formResp.text.slice(0, 160)}`, 'fail')
    return { moduleId, capability: 'bad-request', status: formResp.status }
  }
  const form = formResp.json
  if (!form?.moduleId) {
    report(moduleId, 'FORM_NOT_RENDERED', `form-definition 响应不含模块定义：${formResp.text.slice(0, 160)}`, 'fail')
    return { moduleId, capability: 'bad-payload', status: formResp.status }
  }
  if (!form.canAddNew) return { moduleId, capability: 'no-add', status: formResp.status, title: form.title }

  // 页面渲染
  const rendered = await page.waitForSelector('.erp-form-card', { timeout: 20000 }).catch(() => null)
  if (!rendered) {
    const body = (await page.locator('body').innerText().catch(() => '')).slice(0, 200).replace(/\s+/g, ' ')
    report(moduleId, 'FORM_NOT_RENDERED', `表单卡未渲染，页面内容：${body}`, 'fail')
    return { moduleId, capability: 'not-rendered', status: formResp.status, title: form.title }
  }
  await page.waitForSelector('.erp-form-grid', { timeout: 15000 }).catch(() => null)

  const masterVisible = (form.masterFields ?? []).filter(f => f.isVisible)
  const tabs = form.tabs ?? []
  const tabNos = tabs.length > 0 ? tabs.map(t => t.no) : [1]
  const defaults = { defaultValues: form.defaultValues ?? {} }
  const seen = new Map()

  for (let index = 0; index < tabNos.length; index++) {
    const tabNo = tabNos[index]
    if (tabs.length > 0) {
      const button = page.locator('.erp-form-tabs button.nav-link').nth(index)
      if (await button.count() > 0) {
        await button.click().catch(() => null)
        await page.waitForTimeout(200)
      }
    }
    if (index === 0 && inject) {
      const injected = await page.evaluate(injectMap[inject]).catch(() => null)
      if (!injected) return { moduleId, capability: 'inject-no-target', status: formResp.status, title: form.title }
    }
    const keys = await page.evaluate(({ probe, list }) => {
      const read = eval(probe)
      return list.map(key => read(key))
    }, { probe: `(${PROBE_SCOPE})`, list: masterVisible.filter(f => !tabs.length || f.tabNo === tabNo).map(f => f.key) })
    for (const item of keys) if (item.present && !seen.has(item.key)) seen.set(item.key, { ...item, tabNo })
  }

  const expectedKeys = new Set(masterVisible.map(f => f.key))
  for (const field of masterVisible) {
    if (tabs.length > 0 && !tabNos.includes(field.tabNo)) {
      report(moduleId, 'FIELD_TAB_UNREACHABLE', `字段 ${field.key}（${field.label}）的页签 ${field.tabNo} 不在页签表里，切遍所有页签也看不到它`, 'fail', { field: field.key, tabNo: field.tabNo })
      continue
    }
    checkMasterField(moduleId, field, seen.get(field.key), defaults.defaultValues, tabs.length > 0 ? field.tabNo : 1, isMasterAmountLocked(form, field))
  }
  const knownKeys = new Set([...expectedKeys, ...(form.detailFields ?? []).map(f => f.key)])
  for (const key of seen.keys()) {
    if (!knownKeys.has(key)) report(moduleId, 'EXTRA_FIELD', `DOM 里出现了元数据不认识的字段键 ${key}`, 'fail', { field: key })
  }

  // 明细列头覆盖。空表时 ErpTable 用 empty 兜底替换整张表（连表头一起不渲染），
  // 所以要点「新增一行」把表格顶出来——那只是客户端加一条空行，不产生任何服务端写入。
  // 该按钮要求 detailNoFields（单别/单号那一组主表字段）先有值，故先往那些控件里写字：
  // 同样只改客户端状态、永不保存；填不进去时如实记为未检查并带上卡住的原因，不伪装成通过。
  const detailVisible = (form.detailFields ?? []).filter(f => f.isVisible)
  if (detailVisible.length > 0) {
    const headerScope = () => [...document.querySelectorAll('.erp-detail-card thead th')]
      .map(th => String(th.textContent ?? '').replace(/\s+/g, ' ').trim())
    const detailCardCount = await page.locator('.erp-detail-card').count()
    if (detailCardCount === 0) {
      report(moduleId, 'DETAIL_HEADER_MISSING', `模块声明了 ${detailVisible.length} 个明细可见字段，但没有渲染明细子表`, 'fail', {})
    } else {
      let headers = await page.evaluate(headerScope)
      let blocked = ''
      if (headers.length === 0) {
        // 「新增一行」要求 detailNoFields（单别/单号/客户/币别那一组主表字段）先有值，而这些字段
        // 散布在多个页签上。逐页签两段补齐：先往普通控件里写探针值，再对"只读联动列"
        // （如币别，只能经选择器按钮赋值）真去点开统一选择器挑一行——这正是用户平时的操作路径。
        // 全部只改浏览器状态、永不保存；补不齐时如实记下卡在哪几个键、为什么。
        const gateKeys = String(form.detailNoFields ?? '').split(';').map(s => s.trim()).filter(Boolean)
        const pending = new Set(gateKeys)
        const reasons = new Map()
        const everFound = new Set()
        const chooserUsable = new Set()
        const returnSources = collectReturnSources(form.masterFields)
        const note = (key, reason) => { if (!reasons.has(key)) reasons.set(key, reason) } // 保留首次的成因，后续页签的"本页签无此控件"不该盖掉它
        const activateTab = async index => {
          if (tabs.length === 0) return
          const button = page.locator('.erp-form-tabs button.nav-link').nth(index)
          if (await button.count() > 0) {
            await button.click().catch(() => null)
            await page.waitForTimeout(200)
          }
        }
        for (let round = 0; round < 3 && pending.size > 0; round++) {
          const before = pending.size
          for (let index = 0; index < tabNos.length && pending.size > 0; index++) {
            await activateTab(index)
            const results = await page.evaluate(fillGateFields, { keys: [...pending] })
            for (const item of results) {
              if (item.found) everFound.add(item.key)
              if (item.chooserUsable) chooserUsable.add(item.key)
              if (item.ok) { pending.delete(item.key); reasons.delete(item.key) }
              else note(item.key, item.reason)
            }
            for (const key of [...pending]) {
              if (await pickViaChooser(page, key)) {
                reasons.delete(key)
                await page.waitForTimeout(200)
              }
            }
            // 只读联动列自己没有数据源，值只能由别的字段带出：顺着 RETURN_ITEMS 找到那个来源字段，
            // 点开它的选择器挑一行（这正是用户平时的路径——先选币别/厂商，汇率自己出来）。
            // 不走这一步，就只能报"不可编辑且无可用数据源"，把可用的模块误记成观察项。
            for (const key of [...pending]) {
              const sourceKeys = returnSources.get(key.toUpperCase())
              if (!sourceKeys) continue
              for (const sourceKey of sourceKeys) {
                if (sourceKey === key) continue
                if (!(await pickViaChooser(page, sourceKey))) continue
                await page.waitForTimeout(250)
                const probe = await page.evaluate(fillGateFields, { keys: [key] })
                if (probe[0]?.ok) { pending.delete(key); reasons.delete(key); break }
              }
            }
          }
          // 联动会带出别的门禁字段（选币别带出汇率），故再判一次是否还有进展
          if (pending.size === before) break
          for (const key of [...pending]) {
            const results = await page.evaluate(fillGateFields, { keys: [key] })
            if (results[0]?.ok) { pending.delete(key); reasons.delete(key) }
          }
        }
        // 兜底：选择器真取不到值（「单别」这类只认主档的字段在本库可能没有可挑项）时退回写字。
        // 目的只是把明细表顶出空表态以完成列头对拍，不是在断言这条值合法；仍然失败就照旧记未检查。
        if (pending.size > 0) {
          for (let index = 0; index < tabNos.length && pending.size > 0; index++) {
            await activateTab(index)
            const forced = await page.evaluate(fillGateFields, { keys: [...pending], allowChooserFields: true })
            for (const item of forced) {
              if (item.found) everFound.add(item.key)
              if (item.ok) { pending.delete(item.key); reasons.delete(item.key) }
              else note(item.key, item.reason)
            }
          }
        }
        const addRow = page.locator('.erp-detail-card button', { hasText: '新增一行' }).first()
        if (await addRow.count() > 0) {
          await addRow.click({ timeout: 5000 }).catch(() => null)
          await page.waitForTimeout(400)
          headers = await page.evaluate(headerScope)
        }
        if (headers.length === 0) {
          /**
           * 门禁没补齐 ⇒ 明细行加不出来。逐键定性：
           * - 任何页签都不渲染它：元数据要求它、界面却不给 ⇒ 硬缺陷；
           * - 只读且没有可用数据源、也不在任何选择器的回填目标里 ⇒ 用户无路可填 ⇒ 硬缺陷
           *   （该模块的明细功能是死的）；
           * - 其余（有回填来源但本次没触发）只记观察，不下结论。
           */
          for (const key of pending) {
            const masterField = (form.masterFields ?? []).find(item => item.key === key)
            if (!masterField) {
              report(moduleId, 'DETAIL_GATE_NOT_A_FIELD', `明细门禁（DETAIL_NO_FIELDS）点名了 ${key}，但该字段不在本模块的主表录入面上，界面既不显示也不校验它 —— 门禁永远过不去，明细行加不出来`, 'fail', { field: key })
            } else if (!everFound.has(key)) {
              report(moduleId, 'DETAIL_GATE_UNFILLABLE', `明细门禁字段 ${key}（${masterField.label}）：所有页签都没有渲染它的控件，而模块要求填了它才能加明细行 —— 明细行加不出来`, 'fail', { field: key })
            } else if (!chooserUsable.has(key) && !returnSources.has(key.toUpperCase())) {
              report(moduleId, 'DETAIL_GATE_UNFILLABLE', `明细门禁字段 ${key}（${masterField.label}）：只读、无可用数据源、也不在任何选择器的回填目标里，用户无路可填 —— 明细行加不出来`, 'fail', { field: key })
            }
          }
          const alert = await page.locator('.erp-form-page .alert-danger').first().innerText().catch(() => '')
          // 卡住的键若有回填来源，把"该由谁带出"一并写进理由：下一次人工排查不用再翻一遍 RETURN_ITEMS
          const reasonText = ([key, reason]) => {
            const sources = returnSources.get(key.toUpperCase())
            return sources ? `${key}:${reason}（期望由 ${[...sources].join('/')} 带出）` : `${key}:${reason}`
          }
          blocked = [String(alert ?? '').replace(/\s+/g, ' ').trim(), ...[...reasons].map(reasonText)].filter(Boolean).join('；')
        }
      }
      if (headers.length === 0) {
        report(moduleId, 'DETAIL_HEADER_UNCHECKED', `明细子表处于空表态（未渲染表头），${detailVisible.length} 个明细字段的列头覆盖本次未检查${blocked ? `；阻塞原因：${blocked}` : ''}`, 'info', {})
      } else {
        // 标签里常有用于对齐的连续空格（如「库    别」），HTML 渲染会折叠成单个空格，比对前两侧都归一
        const flat = text => String(text ?? '').replace(/\s+/g, '')
        const flatHeaders = headers.map(flat)
        for (const field of detailVisible) {
          if (!flatHeaders.some(text => text.includes(flat(field.label)))) {
            report(moduleId, 'DETAIL_HEADER_MISSING', `明细字段 ${field.key}（${field.label}）在子表列头里找不到`, 'fail', { field: field.key })
          }
        }
      }
    }
  }

  if (errors.length > 0) {
    report(moduleId, 'PAGE_ERROR', errors.slice(0, 2).join(' | '), 'fail')
  }
  return {
    moduleId,
    capability: 'swept',
    status: formResp.status,
    title: form.title,
    masterVisible: masterVisible.length,
    detailVisible: detailVisible.length,
    tabs: tabs.length,
  }
}

// ===== 主流程 =====
mkdirSync(outDir, { recursive: true })
const browser = await chromium.launch({ executablePath: chromePath, headless: !headed })
const consoleErrors = []
let exitCode = 0
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  page.on('pageerror', error => consoleErrors.push(`pageerror: ${error.message}`))
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(`console: ${message.text()}`) })
  page.on('response', async response => {
    if (response.status() >= 500 && response.url().includes('/api/')) {
      consoleErrors.push(`http ${response.status()}: ${response.url().replace(baseUrl, '')}`)
    }
  })

  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', process.env.EOS_SWEEP_USER || 'admin')
  await page.fill('#login-password', process.env.EOS_SWEEP_PASSWORD || 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 20000 })
  await page.waitForLoadState('networkidle')
  consoleErrors.length = 0 // 登录前的 bootstrap 401 属预期

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

  if (runSelftest || injectCase) {
    /**
     * 判别性自检：把页面改坏，断言判据确实报红——否则无从知道扫描器是真绿还是假绿。
     * 用例在**多个模块上轮寻靶子**（某个模块可能没有带默认值的控件或没有选择按钮），
     * 全试完仍找不到靶子时明确记「未取得靶子」，不当成通过。
     */
    const target = moduleIds.length > 0 ? moduleIds : [Number(option('module', 0)) || 1401]
    const cases = runSelftest ? Object.keys(INJECTIONS) : [String(injectCase)]
    let selftestFailed = 0
    let selftestSkipped = 0
    for (const name of cases) {
      if (!INJECTIONS[name]) { console.log(`FAIL 未知注入用例 ${name}`); selftestFailed++; continue }
      let verdict = null
      for (const id of target) {
        findings.length = 0
        const errors = []
        page.removeAllListeners('pageerror'); page.removeAllListeners('console')
        page.on('pageerror', error => errors.push(`pageerror: ${error.message}`))
        page.on('console', message => { if (message.type() === 'error') errors.push(`console: ${message.text()}`) })
        const outcome = await sweepModule(page, id, errors, { inject: name })
        if (outcome.capability === 'inject-no-target') continue
        const hit = findings.some(item => item.code === INJECTIONS[name].expect && item.level === 'fail')
        if (hit) { verdict = { ok: true, id }; break }
        verdict = { ok: false, id, outcome }
        break
      }
      if (!verdict) { console.log(`SKIP 注入 ${name} → ${target.length} 个候选模块里都没有可注入的靶子`); selftestSkipped++; continue }
      if (verdict.ok) console.log(`PASS 注入 ${name} → 模块 ${verdict.id} 报出 ${INJECTIONS[name].expect}（${INJECTIONS[name].note}）`)
      else { console.log(`FAIL 注入 ${name} → 模块 ${verdict.id} 未报出 ${INJECTIONS[name].expect}（capability=${verdict.outcome.capability}）`); selftestFailed++ }
    }
    exitCode = selftestFailed > 0 ? 1 : 0
    console.log(`\n判别性自检：${cases.length - selftestFailed - selftestSkipped}/${cases.length - selftestSkipped} 通过，${selftestSkipped} 个未取得靶子`)
  } else {
    const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14)
    const shotsDir = join(outDir, 'shots')
    mkdirSync(shotsDir, { recursive: true })
    let index = 0
    for (const moduleId of moduleIds) {
      index++
      const errors = []
      const collector = text => errors.push(text)
      const onError = error => collector(`pageerror: ${error.message}`)
      const onConsole = message => { if (message.type() === 'error') collector(`console: ${message.text()}`) }
      page.on('pageerror', onError)
      page.on('console', onConsole)
      let outcome
      try {
        outcome = await sweepModule(page, moduleId, errors, { inject: null })
      } catch (error) {
        outcome = { moduleId, capability: 'threw' }
        report(moduleId, 'FORM_NOT_RENDERED', `扫描抛异常：${error.message}`, 'fail')
      } finally {
        page.off('pageerror', onError)
        page.off('console', onConsole)
      }
      moduleReports.push(outcome)
      const failed = findings.filter(item => item.moduleId === moduleId && item.level === 'fail')
      if (failed.length > 0) {
        await page.screenshot({ path: join(shotsDir, `${moduleId}.png`), fullPage: false }).catch(() => null)
      }
      const mark = failed.length === 0 ? (outcome.capability === 'swept' ? 'PASS' : `SKIP(${outcome.capability})`) : `FAIL×${failed.length}`
      console.log(`[${index}/${moduleIds.length}] ${moduleId} ${mark}`)
    }

    // 基线棘轮：只报新增
    const signature = item => `${item.moduleId}|${item.code}|${item.field ?? ''}`
    const current = findings.filter(item => item.level === 'fail')
    let newFindings = current
    let resolved = []
    if (baselinePath && existsSync(baselinePath)) {
      const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'))
      const known = new Set((baseline.findings ?? []).map(signature))
      newFindings = current.filter(item => !known.has(signature(item)))
      const currentKeys = new Set(current.map(signature))
      resolved = (baseline.findings ?? []).filter(item => !currentKeys.has(signature(item)))
    }
    if (updateBaseline && baselinePath) {
      mkdirSync(resolve(baselinePath, '..'), { recursive: true })
      writeFileSync(baselinePath, JSON.stringify({ generatedAt: new Date().toISOString(), findings: current }, null, 2), 'utf8')
    }

    const byCode = {}
    for (const item of current) byCode[item.code] = (byCode[item.code] ?? 0) + 1
    const swept = moduleReports.filter(item => item.capability === 'swept').length
    const skipped = moduleReports.filter(item => item.capability !== 'swept')
    const summary = {
      generatedAt: new Date().toISOString(),
      baseUrl,
      modulesScanned: moduleIds.length,
      modulesSwept: swept,
      capability: skipped.reduce((acc, item) => { acc[item.capability] = (acc[item.capability] ?? 0) + 1; return acc }, {}),
      failByCode: byCode,
      newFailByCode: newFindings.reduce((acc, item) => { acc[item.code] = (acc[item.code] ?? 0) + 1; return acc }, {}),
      baseline: baselinePath ?? null,
      resolvedSinceBaseline: resolved.length,
      findings,
      modules: moduleReports,
    }
    writeFileSync(join(outDir, `render-sweep-${stamp}.json`), JSON.stringify(summary, null, 2), 'utf8')

    const lines = [
      `# 统一表单渲染扫描 ${stamp}`,
      '',
      `- 扫描模块：${moduleIds.length}（成功对拍 ${swept} 个）`,
      `- 能力分类：${Object.entries(summary.capability).map(([k, v]) => `${k}=${v}`).join(' / ') || '—'}`,
      `- FAIL 合计：${current.length}（按判据：${Object.entries(byCode).map(([k, v]) => `${k}=${v}`).join(' / ') || '—'}）`,
      baselinePath ? `- 相对基线新增：${newFindings.length}；基线中已消失：${resolved.length}` : '- 未提供基线（全部按新增计）',
      '',
      '| 模块 | 判据 | 明细 |',
      '|---|---|---|',
      ...newFindings.slice(0, 400).map(item => `| ${item.moduleId} | ${item.code} | ${String(item.detail).replace(/\|/g, '/')} |`),
    ]
    writeFileSync(join(outDir, `render-sweep-${stamp}.md`), lines.join('\n'), 'utf8')

    console.log(`\n===== 渲染扫描汇总 =====`)
    console.log(`扫描 ${moduleIds.length} 个模块；成功对拍 ${swept} 个；能力外 ${skipped.length} 个`)
    console.log(`FAIL 合计 ${current.length}${baselinePath ? `（相对基线新增 ${newFindings.length}）` : ''}`)
    for (const [code, count] of Object.entries(byCode).sort((a, b) => b[1] - a[1])) console.log(`  ${code}: ${count}`)
    console.log(`报告：${join(outDir, `render-sweep-${stamp}.md`)}`)
    const infoCount = findings.filter(item => item.level === 'info').length
    console.log(`观察项（info，不计失败）：${infoCount}`)
    exitCode = newFindings.length > 0 ? 1 : 0
  }
} finally {
  await browser.close()
}
process.exit(exitCode)
