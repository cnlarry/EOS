import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // 整条用例的上限（默认 5 秒不够）：满量跑（上千条并行）比单跑慢一个数量级，
    // 于是"重流程"用例轮流撞线——每条单跑都是通过的，报出来的却是一句
    // "Test timed out in 5000ms"，看着像死锁，其实只是慢（见手册 06）。
    // 抬高它只是把"慢"与"坏了"分开：真没渲染出来时，先失败的仍是里面那条
    // 会说话的断言（各处 findBy*/waitFor 自己的 1 秒默认值照旧生效）。
    testTimeout: 20_000,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html'],
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/test/**',
        'src/main.tsx',
        'src/vite-env.d.ts',
      ],
    },
  },
})
