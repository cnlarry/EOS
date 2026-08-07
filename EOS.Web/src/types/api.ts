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
}

export interface ApiErrorBody {
  code: string
  message: string
  fieldErrors?: ApiFieldError[]
  requestId?: string
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
