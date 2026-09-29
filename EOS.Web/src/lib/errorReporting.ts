// Global frontend error reporting: window error / unhandledrejection / React render
// failures are posted best-effort to POST /api/v1/client-errors, where the API logs
// them at Warning (event client_error) into the same JSONL file pipeline as the
// request logs, so a report id correlates both sides. Never throws, never blocks UI.

import { appVersion } from './diagnostics'

/** 前端上报载荷：与服务端 ClientErrorReport 的字段一一对应。 */
export interface ClientErrorPayload {
  kind: string
  message: string
  stack?: string
  url?: string
  correlationId?: string
  appVersion?: string
}

export function reportClientError(payload: ClientErrorPayload): void {
  postError({
    kind: payload.kind,
    message: payload.message,
    stack: payload.stack,
    url: payload.url,
    correlationId: payload.correlationId,
    // 版本号仍按字符串上报（服务端按长度校验）；缺省由 appVersion() 兜底为 unknown
    appVersion: payload.appVersion ?? appVersion(),
  })
}

function postError(payload: Record<string, string | undefined>): void {
  try {
    const body = JSON.stringify(payload);
    if (navigator.sendBeacon) {
      const blob = new Blob([body], { type: 'application/json' });
      navigator.sendBeacon('/api/v1/client-errors', blob);
      return;
    }
    void fetch('/api/v1/client-errors', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      keepalive: true,
    }).catch(() => undefined);
  } catch {
    // reporting must never break the app
  }
}

export function installGlobalErrorHandlers(): void {
  window.addEventListener('error', (event) => {
    reportClientError({
      kind: 'error',
      message: String(event.message ?? 'unknown error').slice(0, 1000),
      stack:
        typeof event.error?.stack === 'string'
          ? String(event.error.stack).slice(0, 4000)
          : undefined,
      url: window.location.pathname.slice(0, 500),
    });
  });
  window.addEventListener('unhandledrejection', (event) => {
    const reason = event.reason as unknown;
    const message =
      typeof reason === 'string'
        ? reason
        : reason instanceof Error
          ? `${reason.name}: ${reason.message}`
          : 'unhandled rejection';
    reportClientError({
      kind: 'unhandledrejection',
      message: String(message).slice(0, 1000),
      stack: reason instanceof Error ? String(reason.stack).slice(0, 4000) : undefined,
      url: window.location.pathname.slice(0, 500),
    });
  });
}
