/**
 * 菜单复刻 UI 冒烟验证：登录 → 三级菜单树 → 菜单搜索直达 → 第 4 级分组组值筛选。
 * 用法：node scripts/menu-ui-verify.mjs [baseUrl]
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
const shotDir = join(process.cwd(), 'logs', 'menu-ui-verify')
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
  const sidebar = page.locator('aside.erp-sidebar')
  await sidebar.getByRole('button', { name: '基本参数' }).waitFor({ state: 'visible', timeout: 10000 })

  // 2. 侧栏搜索框
  const searchBox = sidebar.getByPlaceholder('搜索菜单…')
  record('侧栏菜单搜索框存在', await searchBox.isVisible())

  // 3. 三级菜单树：基本参数 → 系统参数 → 公司基本资料
  const rootToggle = sidebar.getByRole('button', { name: '基本参数', exact: true })
  if ((await rootToggle.getAttribute('aria-expanded')) !== 'true') await rootToggle.click()
  const subToggle = sidebar.getByRole('button', { name: '系统参数', exact: true })
  await subToggle.waitFor({ state: 'visible', timeout: 5000 })
  if ((await subToggle.getAttribute('aria-expanded')) !== 'true') await subToggle.click()
  await sidebar.getByRole('link', { name: /公司基本资料/ }).waitFor({ state: 'visible', timeout: 5000 })
  record('三级菜单树渲染（基本参数 > 系统参数 > 公司基本资料）', true)
  await page.screenshot({ path: join(shotDir, '03-menu-tree.png'), fullPage: false })

  // 4. 菜单搜索：报价单 → 点击直达 1404
  await searchBox.fill('报价单')
  const searchResult = sidebar.getByRole('button', { name: /销售管理 \/ 报价单/ })
  await searchResult.waitFor({ state: 'visible', timeout: 5000 })
  await page.screenshot({ path: join(shotDir, '04-menu-search.png'), fullPage: false })
  await searchResult.click()
  await page.waitForURL('**/workbench/1404', { timeout: 10000 })
  await page.waitForLoadState('networkidle')
  record('菜单搜索直达报价单工作台（1404）', page.url().includes('/workbench/1404'))

  // 5. 工具条分组下拉：170204 分组 → 结案 → 组值 NO（分组已从侧栏第 4 级移到工作台工具条）
  await page.goto(`${baseUrl}/workbench/170204`, { waitUntil: 'networkidle' })
  const commandBar = page.locator('.erp-list-command-bar')
  const groupDropdown = commandBar.getByRole('button', { name: '分组', exact: true })
  await groupDropdown.waitFor({ state: 'visible', timeout: 10000 })
  await groupDropdown.click()
  const caseItem = page.getByRole('menuitem', { name: '结案', exact: true })
  await caseItem.waitFor({ state: 'visible', timeout: 5000 })
  await caseItem.click()
  const groupValue = page.getByRole('menuitem', { name: 'NO', exact: true })
  await groupValue.waitFor({ state: 'visible', timeout: 8000 })
  await page.screenshot({ path: join(shotDir, '05-group-values.png'), fullPage: false })
  await groupValue.click()
  await page.waitForURL(/groupId=2&groupValue=NO/, { timeout: 10000 })
  await page.waitForLoadState('networkidle')
  const chip = page.getByText('分组筛选：NO')
  await chip.waitFor({ state: 'visible', timeout: 8000 })
  record('工具条分组下拉进入组值筛选（170204/结案/NO）', page.url().includes('groupId=2&groupValue=NO'))
  await page.screenshot({ path: join(shotDir, '06-group-filter-chip.png'), fullPage: false })

  // 6. 菜单管理页（2301）：树渲染 + 选中节点加载表单
  await page.goto(`${baseUrl}/admin/menus`, { waitUntil: 'networkidle' })
  await page.getByText('菜单管理（模块 2301）').waitFor({ state: 'visible', timeout: 10000 })
  const menuTreeRow = page.locator('.erp-menu-tree').getByRole('button', { name: /基本参数/ })
  await menuTreeRow.waitFor({ state: 'visible', timeout: 8000 })
  await menuTreeRow.click()
  await page.getByLabel('菜单名称').waitFor({ state: 'visible', timeout: 5000 })
  record('菜单管理页（2301）树与表单加载', true)
  await page.screenshot({ path: join(shotDir, '07-menu-admin.png'), fullPage: false })

  // 7. 控制台错误
  record('无控制台/页面错误', errors.length === 0, errors.slice(0, 3).join(' | '))
} finally {
  await browser.close()
}

const failed = results.filter((item) => !item.ok).length
console.log(`\n菜单 UI 冒烟：${results.length - failed}/${results.length} 通过`)
process.exit(failed > 0 ? 1 : 0)

