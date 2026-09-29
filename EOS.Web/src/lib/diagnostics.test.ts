import { describe, expect, it } from 'vitest'
import { ensureReportId, formatDiagnostics, newReportId, reportIdOf, type AppInfo } from './diagnostics'
import { ApiError } from '../types/api'

const info: AppInfo = { version: '0.1.0', path: '/workbench/1406', at: '2026/9/29 21:30:00' }

describe('diagnostics', () => {
  it('报障编号取自接口错误的响应体', () => {
    const error = new ApiError(500, { code: 'INTERNAL_ERROR', message: '服务器内部错误', correlationId: 'corr-9' })
    expect(reportIdOf(error)).toBe('corr-9')
  })

  it('非接口错误没有编号（渲染期错误由调用方本地生成）', () => {
    expect(reportIdOf(new Error('boom'))).toBeUndefined()
  })

  it('本地生成的编号非空且唯一', () => {
    const first = newReportId()
    const second = newReportId()
    expect(first.length).toBeGreaterThan(0)
    expect(first).not.toBe(second)
  })

  it('已有编号时沿用，避免同一现场出现两个编号', () => {
    expect(ensureReportId('corr-1')).toBe('corr-1')
    expect(ensureReportId('  ')).not.toBe('  ')
    expect(ensureReportId(undefined)).toBeTruthy()
  })

  it('诊断文本含版本、页面、时间与报障编号（用户复制即可给维护者）', () => {
    const text = formatDiagnostics(info, 'corr-7', 'TypeError: boom')
    expect(text).toContain('版本：0.1.0')
    expect(text).toContain('页面：/workbench/1406')
    expect(text).toContain('报障编号：corr-7')
    expect(text).toContain('说明：TypeError: boom')
  })
})
