/** 1405 新增页子表编辑冒烟：加行预填、主键只读、全选/删除所选。 */
import { createRequire } from 'module'
import { mkdirSync } from 'fs'
import { join } from 'path'

const require = createRequire(import.meta.url)
const pwCorePath = process.env.PW_CORE_PATH || join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core')
const { chromium } = require(pwCorePath)

const baseUrl = process.argv[2] || 'http://localhost:5173'
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const shotDir = join(process.cwd(), 'logs', 'form-layout-verify')
mkdirSync(shotDir, { recursive: true })

const browser = await chromium.launch({ executablePath: chromePath, headless: true })
try {
  const page = await browser.newPage({ viewport: { width: 1600, height: 950 } })
  const errors = []
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))

  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', 'admin')
  await page.fill('#login-password', 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 15000 })
  await page.waitForLoadState('networkidle')

  await page.goto(`${baseUrl}/workbench/1405/new`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.erp-form-grid', { timeout: 20000 })
  await page.waitForTimeout(500)

  // 先选客户（1405 DETAIL_NO_FIELDS=CLIENT_ID，对齐旧系统 checkPriNotNull）
  await page.locator('.erp-form-cell').filter({ hasText: '客户' }).locator('input').first().fill('668ZS')

  // 明细主键列只读（订单别/订单号）
  await page.getByRole('button', { name: '新增一行' }).click()
  await page.waitForTimeout(400)
  const detailRows = page.locator('.erp-detail-grid tbody tr:not(.erp-detail-filler)')
  const rowCount = await detailRows.count()
  console.log(`detailRows=${rowCount}`)
  const headerTexts = await page.locator('.erp-detail-grid thead th').allTextContents()
  console.log(`headers=[${headerTexts.join('|')}]`)
  const autoNoHint = await page.locator('.erp-detail-grid .form-hint').allTextContents()
  console.log(`autoNoHint=[${autoNoHint.join('|')}]`)
  const sortButtons = await page.locator('.erp-detail-sort').count()
  console.log(`sortButtons=${sortButtons}`)
  const resizerHandles = await page.locator('.erp-detail-grid .erp-col-resizer').count()
  console.log(`resizerHandles=${resizerHandles}`)
  // 新行预填：订单别/订单号来自主表（只读灰显）
  const cells = await detailRows.first().locator('input').evaluateAll(inputs => inputs.map(i => ({ value: i.value, disabled: i.disabled })))
  console.log(`newRowCells=${JSON.stringify(cells)}`)

  // 全选 + 删除所选
  await page.getByRole('button', { name: '新增一行' }).click()
  await page.waitForTimeout(300)
  const rowsAfterAdd = await detailRows.count()
  console.log(`rowsAfterAdd=${rowsAfterAdd}`)
  await page.getByRole('checkbox', { name: '全选' }).check()
  const delBtn = page.getByRole('button', { name: /删除所选/ })
  console.log(`deleteSelectedEnabled=${await delBtn.isEnabled()}`)
  await delBtn.click()
  await page.waitForTimeout(300)
  console.log(`rowsAfterDelete=${await detailRows.count()}`)
  // 排序：点击"数量"类表头切换（这里用第一个可排序列验证指示器出现）
  const firstSortBtn = page.locator('.erp-detail-sort').first()
  await firstSortBtn.click()
  await page.waitForTimeout(200)
  const sortIndicator = await page.locator('.erp-detail-sort').first().textContent()
  console.log(`sortIndicator=${sortIndicator}`)
  await page.screenshot({ path: join(shotDir, '1405-detail.png'), fullPage: true })

  console.log(errors.length ? `页面错误：\n${errors.join('\n')}` : 'PASS 无页面错误')
} finally {
  await browser.close()
}
