export interface PageRequest {
  page: number
  pageSize: number
}

export interface PageResponse<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

export interface ApiFieldError {
  field: string
  message: string
  code: string
  /** 明细行错误行号（0 起）；主表/整单级错误为 null（ADR-006 决策 2.2） */
  rowIndex?: number | null
}

/** 保存成功但存在后续异常时的非阻断告警（ADR-006 决策 2.7，如自动批核失败） */
export interface ApiSaveWarning {
  code: string
  message: string
}

export interface ApiErrorBody {
  code: string
  message: string
  fieldErrors?: ApiFieldError[]
  requestId?: string
  correlationId?: string
  definitionVersion?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly body: ApiErrorBody

  constructor(status: number, body: ApiErrorBody) {
    super(body.message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}
