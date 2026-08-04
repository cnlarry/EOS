import { ApiClient } from './client'
import { HttpTransport } from './httpTransport'

export const apiClient = new ApiClient(new HttpTransport())
