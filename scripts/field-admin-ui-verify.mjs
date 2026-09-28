/**
 * 数据表/字段维护（模块 2302）UI 冒烟验证：
 * 登录 → 数据表列表 → 「编辑」进入字段列表页 → 表信息编辑（高风险只读）
 * → 返回 → 未管理字段弹窗 → 字段新增弹窗（受控类型下拉）。
 * 只读冒烟：不执行新增/删除/批量生成等写操作。
 * 用法：node scripts/field-admin-ui-verify.mjs [baseUrl]
 * 环境变量：PW_CORE_PATH（playwright-core 安装目录）、EOS_CHROME_PATH（Chrome 可执行文件）
 */
import { createRequire } from 'module'
import { existsSync, mkdirSync } from 'fs'
import { join } from 'path'

const require = createRequire(import.meta.url)
const pwCorePath = process.env.PW_CORE_PATH || join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core')
const { chromium } = require(pwCorePath)

const baseUrl = process.argv[2] || 'http://localhost:5173'
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const shotDir = join(process.cwd(), 'logs', 'field-admin-ui-verify')
mkdirSync(shotDir, { recursive: true })

const results = []
const record = (name, ok, detail = '') => {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ` — ${detail}` : ''}`)
}

if (!existsSync(chromePath)) {
  console.error(`找不到 Chrome：${chromePath}（可用 EOS_CHROME_PATH 指定）`)
  process.exit(1)
}

const browser = await chromium.launch({ executablePath: chromePath, headless: true })
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(`console: ${message.text()}`)
  })

  // 1. 登录
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', 'admin')
  await page.fill('#login-password', 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 15000 })
  await page.waitForLoadState('networkidle')
  errors.length = 0 // 登录前的 bootstrap 401 属预期
  record('登录成功并进入仪表盘', page.url().includes('/dashboard'))

  // 2. 数据表列表
  await page.goto(`${baseUrl}/admin/tables`, { waitUntil: 'networkidle' })
  const kindFilter = page.getByRole('combobox', { name: '按性质筛选' })
  await kindFilter.waitFor({ state: 'visible', timeout: 10000 })
  record('数据表列表加载且性质筛选存在', await kindFilter.isVisible())
  record('幽灵字段 0 显示淡绿底深绿字徽标', (await page.locator('.badge.bg-green-lt').count()) > 0)

  // 表头排序：描述列降序后首行变化（clientSideSorting 本地排序；在全量列表上验证）
  const firstTableBefore = await page.locator('.erp-full-list-page tbody tr').first().locator('td').nth(1).innerText()
  await page.getByRole('button', { name: '表头操作描述' }).click()
  await page.getByRole('button', { name: '降序', exact: true }).click()
  await page.waitForTimeout(400)
  const firstTableAfter = await page.locator('.erp-full-list-page tbody tr').first().locator('td').nth(1).innerText()
  record('表头排序可用（描述列降序后首行变化）', firstTableBefore !== firstTableAfter)

  await page.getByRole('searchbox', { name: '搜索数据表' }).fill('COMPANY')
  const companyRow = page.getByRole('cell', { name: 'COMPANY', exact: true }).first().locator('..')
  await companyRow.waitFor({ state: 'visible', timeout: 10000 })
  record('数据表列表包含 COMPANY 且行操作可用', await companyRow.getByRole('button', { name: /^编辑/ }).isVisible())
  const masterRadios = await page.getByRole('radio', { name: '选择此行' }).count()
  record('表列表首列渲染单选列（选择器单选模式同款）', masterRadios > 0)
  const masterCard = await page.locator('.erp-full-list-page > .erp-list-card').first().boundingBox()
  const bodyBox = await page.locator('.page-body > .container-fluid').boundingBox()
  record('表列表铺满页面高度、溢出由表格滚动条承载', Math.abs(masterCard.height - bodyBox.height) < 80)
  const kindWidth = (await page.getByRole('combobox', { name: '按性质筛选' }).boundingBox()).width
  record('性质筛选下拉宽度 165px', Math.abs(kindWidth - 165) < 5)
  await page.screenshot({ path: join(shotDir, '01-tables.png'), fullPage: false })

  // 3. 点击「编辑」打开表信息弹窗（修改表基本资料）
  await companyRow.getByRole('button', { name: /^编辑/ }).click()
  await page.getByText('数据表信息（COMPANY）').waitFor({ state: 'visible', timeout: 5000 })
  const saveBox = await page.getByRole('button', { name: '保存', exact: true }).boundingBox()
  record('「编辑」打开表信息弹窗且保存按钮无需滚动可见', saveBox !== null && saveBox.y + saveBox.height <= 900)
  await page.screenshot({ path: join(shotDir, '02-table-editor.png'), fullPage: false })
  await page.locator('.modal-content .card-footer').getByRole('button', { name: '取消' }).click()

  // 4. 「管理字段」进入字段列表页
  await companyRow.getByRole('button', { name: '管理字段', exact: true }).click()
  await page.waitForURL('**/admin/tables/COMPANY/fields', { timeout: 10000 })
  await page.getByText(/共 \d+ 个字段/).waitFor({ state: 'visible', timeout: 10000 })
  await page.locator('th', { hasText: '物理列' }).first().waitFor({ state: 'visible', timeout: 5000 })
  record('「管理字段」进入字段列表页（主键/物理列状态列渲染）', page.url().includes('/admin/tables/COMPANY/fields'))
  record('字段列表页首列渲染单选列', (await page.getByRole('radio', { name: '选择此行' }).count()) > 0)
  record('布尔列用只读复选框表达', (await page.locator('input[type="checkbox"]:disabled').count()) > 0)
  const crumb = await page.getByRole('navigation', { name: '当前位置' }).innerText()
  record('字段页面包屑：系统管理 > 数据表维护 > 数据表维护 > COMPANY > 字段',
    ['系统管理', '数据表维护', '数据表维护', 'COMPANY', '字段'].every((part) => crumb.includes(part)))
  await page.screenshot({ path: join(shotDir, '03-fields.png'), fullPage: false })

  // 5. 返回数据表列表，再进入 MODULES（有未管理物理列）的字段页
  await page.getByRole('button', { name: '返回', exact: true }).click()
  await page.waitForURL('**/admin/tables', { timeout: 10000 })
  await page.getByRole('searchbox', { name: '搜索数据表' }).fill('MODULES')
  const modulesRow = page.getByRole('cell', { name: 'MODULES', exact: true }).first().locator('..')
  await modulesRow.waitFor({ state: 'visible', timeout: 10000 })
  await modulesRow.getByRole('button', { name: '管理字段', exact: true }).click()
  await page.waitForURL('**/admin/tables/MODULES/fields', { timeout: 10000 })
  await page.getByText(/共 \d+ 个字段/).waitFor({ state: 'visible', timeout: 10000 })
  record('MODULES 字段列表页加载', page.url().includes('/admin/tables/MODULES/fields'))

  // 6. 未管理字段弹窗（只读查看，不生成）
  await page.getByRole('button', { name: '未管理字段', exact: true }).click()
  await page.getByText('未管理字段批量生成（', { exact: false }).waitFor({ state: 'visible', timeout: 5000 })
  await page.locator('.erp-unmanaged-table tbody tr').first().waitFor({ state: 'visible', timeout: 5000 })
  record('未管理字段弹窗以电子表格列出物理列差集', true)
  await page.locator('.erp-unmanaged-table tbody input[type="checkbox"]').first().check()
  record('未管理字段可勾选且「生成」按钮就绪', await page.getByRole('button', { name: '生成', exact: true }).isEnabled())
  await page.screenshot({ path: join(shotDir, '04-unmanaged.png'), fullPage: false })
  await page.locator('.modal-content .card-footer').getByRole('button', { name: '关闭' }).click()

  // 7. 字段新增弹窗：受控类型下拉 + 必填门禁
  await page.getByRole('button', { name: '新增', exact: true }).click()
  await page.getByText('新增字段（MODULES）').waitFor({ state: 'visible', timeout: 5000 })
  const typeOptions = await page.locator('.erp-field-settings-dialog select.form-select').first().locator('option').count()
  record('字段新增弹窗加载且数据库类型为受控下拉', typeOptions >= 20)
  await page.screenshot({ path: join(shotDir, '05-field-editor.png'), fullPage: false })
  await page.locator('.modal-header .btn-close').first().click()

  // 8. 控制台错误
  record('无控制台/页面错误', errors.length === 0, errors.slice(0, 3).join(' | '))
} finally {
  await browser.close()
}

const failed = results.filter((item) => !item.ok).length
console.log(`\n数据表/字段维护 UI 冒烟：${results.length - failed}/${results.length} 通过`)
process.exit(failed > 0 ? 1 : 0)
