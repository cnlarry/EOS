/**
 * ADR-006 统一表单一次性重构 UI 链验证（Playwright）：
 * ① 工作台主表行双击进入浏览态（携带导航上下文）；② 浏览态上一条/下一条按列表顺序导航；
 * ③ 明细子表行双击不进入浏览；④ 查询型模块（14999）双击无动作；⑤ 新增保存落浏览态（服务端权威键）+ 浏览态删除回列表。
 * 运行前提：EOS.API/EOS.Web 已启动，dev 库存在 1209 可浏览数据（无则链①②③ SKIP）、14999 存在。
 * 用法：node scripts/form-rework-ui-verify.mjs [baseUrl]
 */
import { createRequire } from 'module'
import { existsSync, mkdirSync } from 'fs'
import { join } from 'path'

const require = createRequire(import.meta.url)
const pwCorePath = process.env.PW_CORE_PATH || join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core')
const { chromium } = require(pwCorePath)

const baseUrl = process.argv[2] || 'http://localhost:5173'
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const shotDir = join(process.cwd(), 'logs', 'form-rework-ui')
mkdirSync(shotDir, { recursive: true })

const browser = await chromium.launch({ executablePath: chromePath, headless: true })
let failures = 0
const pass = (message) => console.log(`PASS ${message}`)
const fail = (message) => { failures++; console.log(`FAIL ${message}`) }
const skip = (message) => console.log(`SKIP ${message}`)

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))
  page.on('dialog', (dialog) => void dialog.accept())

  // 登录
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.fill('#login-username', 'admin')
  await page.fill('#login-password', 'admin')
  await page.click('button[type=submit]')
  await page.waitForURL('**/dashboard', { timeout: 15000 })
  console.log('PASS 登录成功')

  // 链①：主表行双击进浏览态
  await page.goto(`${baseUrl}/workbench/1209`, { waitUntil: 'networkidle' })
  const firstRow = page.locator('.erp-master-table-region tbody tr').first()
  if ((await firstRow.count()) === 0) {
    skip('1209 无列表数据，双击/上下条链跳过')
  } else {
    await firstRow.locator('td').nth(2).dblclick()
    try {
      await page.waitForURL(/\/workbench\/1209\/view\//, { timeout: 10000 })
      pass('主表行双击进入浏览态（/view?key=…）')
    } catch {
      fail(`双击未进入浏览态，当前 URL=${page.url()}`)
    }
    // 浏览态断言：只读文本渲染 + 返回按钮 + 上下条按钮
    if (/\/view\?key=/.test(page.url())) {
      const staticCount = await page.locator('.erp-form-static').count()
      staticCount > 0 ? pass('浏览态为只读文本渲染') : fail('浏览态未出现只读文本')
      ;(await page.getByRole('button', { name: '返回' }).count()) > 0 ? pass('浏览态存在返回按钮') : fail('缺少返回按钮')
      const nextButton = page.getByRole('button', { name: '下一条' })
      if ((await nextButton.count()) === 0) {
        skip('无导航上下文（单行列表），上下条链跳过')
      } else {
        const urlBefore = page.url()
        await nextButton.click()
        await page.waitForTimeout(800)
        page.url() !== urlBefore ? pass('下一条按列表顺序切换单据') : fail('下一条未切换单据')
      }
    }

    // 链③：明细子表行双击不进入浏览
    await page.goto(`${baseUrl}/workbench/1209`, { waitUntil: 'networkidle' })
    const anyRow = page.locator('.erp-master-table-region tbody tr').first()
    if ((await anyRow.count()) > 0) {
      await anyRow.locator('td').nth(2).click()
      await page.waitForTimeout(600)
      const detailRow = page.locator('.erp-detail-card tbody tr').first()
      if ((await detailRow.count()) === 0) {
        skip('该行无明细数据，明细双击链跳过')
      } else {
        await detailRow.locator('td').nth(2).dblclick()
        await page.waitForTimeout(500)
        !/\/view\?key=/.test(page.url()) ? pass('明细子表行双击不进入浏览') : fail('明细双击错误进入浏览态')
      }
    }
  }

  // 链④：查询型模块（14999 今日需交货）双击无动作
  await page.goto(`${baseUrl}/workbench/14999`, { waitUntil: 'networkidle' })
  await page.waitForTimeout(400)
  const queryRow = page.locator('.erp-master-table-region tbody tr').first()
  if ((await queryRow.count()) === 0) {
    skip('14999 无列表数据，查询模块双击链跳过')
  } else {
    await queryRow.locator('td').nth(2).dblclick()
    await page.waitForTimeout(500)
    !/\/view\?key=/.test(page.url()) ? pass('查询型模块双击无动作（无浏览能力判定生效）') : fail('14999 双击不应进入浏览态')
  }

  // 链⑤：新增保存落浏览态 + 浏览态删除回列表
  const stamp = Date.now().toString().slice(-8)
  await page.goto(`${baseUrl}/workbench/1209/new`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.erp-form-grid', { timeout: 15000 })
  const inputs = page.locator('.erp-form-grid input.form-control:not([disabled])')
  await inputs.nth(0).fill(`EOSDEVUI${stamp}`)
  const editionInput = page.locator('.erp-form-grid input.form-control:not([disabled])').nth(1)
  await editionInput.fill('V1')
  await page.getByRole('button', { name: '保存' }).click()
  let savedToView = false
  try {
    await page.waitForURL(/\/workbench\/1209\/view\//, { timeout: 10000 })
    savedToView = true
    pass('新增保存后进入浏览态（服务端权威键）')
  } catch {
    fail(`保存后未进入浏览态，当前 URL=${page.url()}`)
  }
  if (savedToView) {
    await page.getByRole('button', { name: '删除' }).click()
    try {
      await page.waitForURL(/\/workbench\/1209$/, { timeout: 10000 })
      pass('浏览态删除后返回工作台列表')
    } catch {
      fail(`删除后未回列表，当前 URL=${page.url()}`)
    }
  }

  if (errors.length > 0) {
    failures++
    console.log(`FAIL 页面错误：${errors.slice(0, 3).join(' | ')}`)
  }
} catch (cause) {
  failures++
  console.log(`FAIL 脚本异常：${cause.message}`)
  await page?.screenshot?.({ path: join(shotDir, 'failure.png'), fullPage: true }).catch(() => undefined)
} finally {
  await browser.close()
}
console.log(failures === 0 ? '\nform-rework-ui-verify 全部通过。' : `\nform-rework-ui-verify 有 ${failures} 处失败。`)
process.exit(failures === 0 ? 0 : 1)

