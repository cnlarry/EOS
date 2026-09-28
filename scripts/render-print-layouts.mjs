/** 打印版式视觉精校工具：把单据 PDF 渲染成 PNG 供人工抽样验收。
 *  用法：node scripts/render-print-layouts.mjs [pdf目录] [png输出目录]
 *  默认：logs/m80-e2e/layouts -> %TEMP%/layout-review
 *  依赖 Playwright core 与 Chrome（与 scripts/ui-verify.mjs 相同的环境变量约定）。
 */
import { createRequire } from 'module'
import { join, basename, resolve } from 'path'
import { mkdirSync, readdirSync } from 'fs'

const require = createRequire(import.meta.url)
const pwCorePath = process.env.PW_CORE_PATH || join(process.env.TEMP || '.', 'pw-core', 'node_modules', 'playwright-core')
const { chromium } = require(pwCorePath)
const chromePath = process.env.EOS_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'

const pdfDir = resolve(process.argv[2] || 'logs/m80-e2e/layouts')
const outDir = resolve(process.argv[3] || join(process.env.TEMP || '.', 'layout-review'))
mkdirSync(outDir, { recursive: true })

const pdfs = readdirSync(pdfDir).filter((name) => name.toLowerCase().endsWith('.pdf')).sort()
if (pdfs.length === 0) {
  console.error('no pdf found in', pdfDir)
  process.exit(1)
}

const browser = await chromium.launch({ executablePath: chromePath, headless: true })
for (const name of pdfs) {
  const pdfPath = join(pdfDir, name)
  const outPng = join(outDir, basename(name, '.pdf') + '.png')
  const page = await browser.newPage({ viewport: { width: 1000, height: 1400 } })
  await page.goto('file://' + pdfPath.replace(/\\/g, '/'), { waitUntil: 'networkidle' })
  await page.waitForTimeout(2500)
  await page.screenshot({ path: outPng, fullPage: true })
  await page.close()
  console.log('rendered', outPng)
}
await browser.close()
