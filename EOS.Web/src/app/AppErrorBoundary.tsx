import { Component, type ErrorInfo, type ReactNode } from 'react'
import { IconAlertTriangle, IconCopy, IconHome, IconRefresh } from '@tabler/icons-react'
import { Button } from '../components/ui/Button'
import { copyDiagnostics, currentAppInfo, ensureReportId, formatDiagnostics, type AppInfo } from '../lib/diagnostics'
import { reportClientError } from '../lib/errorReporting'

interface Props {
  children: ReactNode
}

interface State {
  error: Error | null
  info: AppInfo | null
  /**
   * 本次崩溃现场的报障编号：渲染期错误没有请求，编号由这里本地生成，
   * 同时用于「复制诊断信息」与上报——两者必须是同一个值，否则用户给的号在服务端日志里搜不到。
   */
  reportId: string | null
}

/**
 * 应用根错误边界：渲染期抛错时不再整树白屏，而是给出可重试的错误页与可复制的诊断信息。
 * 与 window.onerror 兜底的分工：本组件接管"界面还在但 React 树崩了"，
 * window 兜底负责"连根都没挂上"的情形。两者都上报到同一入口（client-errors）。
 */
export class AppErrorBoundary extends Component<Props, State> {
  state: State = { error: null, info: null, reportId: null }

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error, info: currentAppInfo(), reportId: ensureReportId() }
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    // 上报是强制的：只显示错误页而不上报，等于把崩溃静默掉
    reportClientError({
      kind: 'render',
      message: `${error.name}: ${error.message}`.slice(0, 1000),
      stack: (errorInfo.componentStack ?? error.stack ?? '').slice(0, 4000),
      url: `${window.location.pathname}${window.location.search}`.slice(0, 500),
      correlationId: this.state.reportId ?? undefined,
      appVersion: currentAppInfo().version,
    })
  }

  private readonly reload = () => window.location.reload()

  private readonly goHome = () => {
    window.location.assign('/dashboard')
  }

  private readonly copy = () => {
    const { error, info, reportId } = this.state
    copyDiagnostics(info ?? currentAppInfo(), reportId ?? ensureReportId(), error?.message)
  }

  render(): ReactNode {
    const { error, info, reportId } = this.state
    if (!error) return this.props.children

    return (
      <main className="erp-error-page">
        <div className="text-center">
          <div className="display-5 fw-bold">出错了</div>
          <h1 className="h3 mt-2">页面发生异常，已记录</h1>
          <p className="text-secondary">
            可以重试一次；若反复出现，请把下面的诊断信息复制给维护者。
          </p>
          <pre className="erp-error-diagnostics text-start">{formatDiagnostics(info ?? currentAppInfo(), reportId ?? undefined, error.message)}</pre>
          <div className="d-flex gap-2 justify-content-center mt-3">
            <Button onClick={this.reload}>
              <IconRefresh size={16} /> 重试
            </Button>
            <Button onClick={this.goHome}>
              <IconHome size={16} /> 返回首页
            </Button>
            <Button onClick={this.copy}>
              <IconCopy size={16} /> 复制诊断信息
            </Button>
          </div>
          <p className="text-secondary small mt-3">
            <IconAlertTriangle size={14} /> 该异常已自动上报，包含报障编号与页面地址。
          </p>
        </div>
      </main>
    )
  }
}
