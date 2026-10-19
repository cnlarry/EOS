import { useState } from 'react'

export interface JobAction<T> {
  running: boolean
  error: string | null
  result: T | null
  execute: () => Promise<void>
}

/**
 * 作业执行状态：执行中、最后一次结果与错误统一收敛。
 * 页面只提供请求本身；参数校验可以在请求函数里抛错，由这里统一展示。
 */
export function useJobAction<T>(run: () => Promise<T>): JobAction<T> {
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<T | null>(null)

  const execute = async () => {
    setRunning(true)
    setError(null)
    setResult(null)
    try {
      setResult(await run())
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : '执行失败。')
    } finally {
      setRunning(false)
    }
  }

  return { running, error, result, execute }
}
