/** 批量配置冒烟：1401（3 页签/复合格/下拉）与 1606（采购单 2 页签）。 */
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

  for (const moduleId of [1401, 1606]) {
    await page.goto(`${baseUrl}/workbench/${moduleId}/new`, { waitUntil: 'networkidle' })
    await page.waitForSelector('.erp-form-grid', { timeout: 20000 })
    await page.waitForTimeout(500)
    const tabs = await page.locator('.erp-form-tabs .nav-link').allTextContents()
    const cells = await page.locator('.erp-form-cell').count()
    const selects = await page.locator('.erp-form-field select, .erp-form-cell select').count()
    const rows = await page.locator('.erp-form-row').count()
    console.log(`${moduleId}: tabs=[${tabs.join('|')}] cells=${cells} selects=${selects} rows=${rows}`)
    await page.screenshot({ path: join(shotDir, `${moduleId}-new.png`), fullPage: true })
  }
  console.log(errors.length ? `页面错误：\n${errors.join('\n')}` : 'PASS 无页面错误')
} finally {
  await browser.close()
}
