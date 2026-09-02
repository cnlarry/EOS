/**
 * 统一 apiClient mock 工厂（代码质量批 3 C9 收编）：
 * 原 19 个测试文件各自重复同一份 `vi.hoisted(() => ({ get, post, put, delete, postFile }))`，
 * 收敛到本模块；测试文件用 async 工厂动态 import 本模块返回同一实例：
 * `vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))`。
 */
import { vi } from 'vitest'

export const apiClientMock = {
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}
