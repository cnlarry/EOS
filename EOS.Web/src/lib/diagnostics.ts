import { writeClipboard } from '../components/common/tableClipboard'
import { createId } from './uuid'
import { ApiError } from '../types/api'

/**
 * 前端诊断信息：产品版本、页面地址、出错时间与报障编号（correlationId）。
 * 用户报障时只需复制一段文本，维护者据此直接定位到日志与审计记录
 * （服务端保证响应体里的 correlationId 与文件日志中的 correlation 同值）。
 */
export interface AppInfo {
  version: string
  path: string
  at: string
}

/** 构建时由 vite.config.ts 注入（见 src/vite-env.d.ts）；测试环境未注入时如实标 unknown。 */
export function appVersion(): string {
  return typeof __APP_VERSION__ === 'string' && __APP_VERSION__ ? __APP_VERSION__ : 'unknown'
}

export function currentAppInfo(now: Date = new Date()): AppInfo {
  return {
    version: appVersion(),
    path: typeof window === 'undefined' ? '' : `${window.location.pathname}${window.location.search}`,
    at: now.toLocaleString('zh-CN', { hour12: false }),
  }
}

/**
 * 从错误里取报障编号：接口错误的响应体带 correlationId；
 * 渲染期错误没有请求可言，编号由调用方（错误边界）本地生成后再上报，
 * 服务端 `client-errors` 会原样采用它——这样"用户复制的编号"与"服务端日志里的编号"是同一个。
 */
export function reportIdOf(error: unknown): string | undefined {
  if (error instanceof ApiError) return error.body.correlationId || undefined
  return undefined
}

/** 为一个不经过请求的错误现场生成报障编号。 */
export function newReportId(): string {
  return createId()
}

/** 已有编号则沿用，否则新生成——避免同一个错误现场出现两个编号。 */
export function ensureReportId(existing?: string | null): string {
  return existing && existing.trim().length > 0 ? existing : newReportId()
}

/** 一行式报障编号，供提示条展示（编号是排障关联键，必须完整给出、可复制）。 */
export function describeReportId(error: unknown): string | undefined {
  const id = reportIdOf(error)
  return id ? `报障编号：${id}` : undefined
}

export function formatDiagnostics(info: AppInfo, reportId?: string, detail?: string): string {
  const lines = [
    '【EOS 诊断信息】',
    `版本：${info.version}`,
    `页面：${info.path}`,
    `时间：${info.at}`,
  ]
  if (reportId) lines.push(`报障编号：${reportId}`)
  if (detail) lines.push(`说明：${detail}`)
  return lines.join('\n')
}

export function copyDiagnostics(info: AppInfo, reportId?: string, detail?: string): void {
  writeClipboard(formatDiagnostics(info, reportId, detail))
}
