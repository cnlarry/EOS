/** 统一表单布局冒烟：登录 → 单表模块新增/编辑 → 主子表模块编辑，输出截图。 */
import { createRequire } from 'module'
import { existsSync, mkdirSync } from 'fs'
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
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))
  page.on('console', (message) => { if (message.type() === 'error') errors.push(`console: ${message.text()}`) })

  // 1. 登录
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', 'admin')
  await page.fill('#login-password', 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 15000 })
  await page.waitForLoadState('networkidle')
  errors.length = 0
  console.log('PASS 登录成功')

  // 2. 单表模块（1209 产品版次）新增页
  await page.goto(`${baseUrl}/workbench/1209/new`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.erp-form-grid', { timeout: 15000 })
  await page.waitForTimeout(500)
  const cells = page.locator('.erp-form-field')
  const first = await cells.nth(0).boundingBox()
  const second = await cells.nth(1).boundingBox()
  const full = await page.locator('.erp-form-field.is-full').count()
  const gridBox = await page.locator('.erp-form-grid').boundingBox()
  const sameRow = first && second && Math.abs(first.y - second.y) < 4 && first.x < second.x
  const paired = first && second && Math.abs(first.height - second.height) < 4
  console.log(`LAYOUT 同排两列=${sameRow} 等高=${paired} 整行字段=${full} 网格宽=${gridBox?.width}`)
  const labelBox = await page.locator('.erp-form-field .erp-form-label').first().boundingBox()
  const inputBox = await page.locator('.erp-form-field .form-control').first().boundingBox()
  console.log(`LAYOUT 标签左于控件=${labelBox && inputBox && labelBox.x < inputBox.x && labelBox.x + labelBox.width <= inputBox.x + 8}`)
  await page.screenshot({ path: join(shotDir, '1209-new.png'), fullPage: true })
  console.log('PASS 1209 新增页截图')

  // 3. 单表模块编辑页：回列表选第一行点编辑
  await page.goto(`${baseUrl}/workbench/1209`, { waitUntil: 'networkidle' })
  await page.waitForSelector('tbody tr', { timeout: 15000 })
  await page.locator('tbody tr').first().click()
  await page.getByRole('button', { name: '编辑' }).click()
  await page.waitForSelector('.erp-form-grid', { timeout: 15000 })
  await page.waitForTimeout(500)
  await page.screenshot({ path: join(shotDir, '1209-edit.png'), fullPage: true })
  console.log('PASS 1209 编辑页截图')

  // 4. 主子表模块（1404 客户报价）编辑页
  await page.goto(`${baseUrl}/workbench/1404`, { waitUntil: 'networkidle' })
  await page.waitForSelector('tbody tr', { timeout: 15000 })
  await page.locator('tbody tr').first().click()
  await page.getByRole('button', { name: '编辑' }).click()
  await page.waitForSelector('.erp-form-grid', { timeout: 15000 })
  await page.waitForTimeout(500)
  const detailHeader = await page.locator('.erp-detail-grid thead th').allTextContents()
  const full1404 = await page.locator('.erp-form-field.is-full').count()
  console.log(`LAYOUT 1404 明细表头=[${detailHeader.join('|')}] 整行字段=${full1404}`)
  await page.screenshot({ path: join(shotDir, '1404-edit.png'), fullPage: true })
  console.log('PASS 1404 编辑页截图')

  console.log(errors.length ? `页面错误：\n${errors.join('\n')}` : 'PASS 无页面错误')
} finally {
  await browser.close()
}

