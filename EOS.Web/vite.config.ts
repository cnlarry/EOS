import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * 产品版本的唯一真源是仓库根 version.json（后端程序集、发布标签、更新日志同源）。
 * 前端只把它注入产物，供「日志管理」页展示；构建提交号由后端 /health/version 提供，
 * 不在前端重复一份。
 */
function readAppVersion(): string {
  try {
    const path = fileURLToPath(new URL('../version.json', import.meta.url))
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as { version?: string }
    return parsed.version?.trim() || 'unknown'
  } catch {
    // 读不到就如实标记，不要编一个看起来正常的版本号
    return 'unknown'
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(readAppVersion()),
  },
  server: {
    host: true,
    port: 80,
    allowedHosts: ['localhost', '127.0.0.1'],
    proxy: {
      '/api': {
        target: 'http://localhost:5261',
        changeOrigin: true,
      },
    },
  },
})
