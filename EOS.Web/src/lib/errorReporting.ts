// Global frontend error reporting: window error / unhandledrejection /
// failed API responses are posted best-effort to POST /api/v1/client-errors,
// where the API logs them at Warning (event client_error) into the JSONL
// pipeline for log MCP correlation. Never throws, never blocks UI.

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
    postError({
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
    postError({
      kind: 'unhandledrejection',
      message: String(message).slice(0, 1000),
      stack: reason instanceof Error ? String(reason.stack).slice(0, 4000) : undefined,
      url: window.location.pathname.slice(0, 500),
    });
  });
}
