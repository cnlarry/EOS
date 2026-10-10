import { apiClient } from '../../services/api'
import type {
  ImportDefinitionInfo,
  ImportFileData,
  ImportMappingEntry,
  ImportMappingSnapshot,
  ImportReadinessItem,
  ImportRunRequest,
  ImportRunResult,
  ImportTarget,
} from './types'

/** 可导入的目标模块（写名单 ∩ 统一工作台模块）。 */
export function fetchImportTargets() {
  return apiClient.get<ImportTarget[]>('/import/targets')
}

export function fetchImportDefinition(moduleId: number) {
  return apiClient.get<ImportDefinitionInfo>(`/import/targets/${moduleId}/definition`)
}

/** 空模板：列名即字段标签，填好直接上传即可自动匹配。 */
export function fetchImportTemplate(moduleId: number) {
  return apiClient.getFile(`/import/targets/${moduleId}/template`)
}

/** 上传文件并解析成表格：服务端只读成格子，不碰业务判定。 */
export function parseImportFile(file: File) {
  const form = new FormData()
  form.append('file', file)
  return apiClient.postForm<ImportFileData>('/import/parse', form)
}

/** 预演（dryRun）与执行共用同一端点形状：预演在真实事务里跑完再回滚。 */
export function runImport(moduleId: number, request: ImportRunRequest, dryRun: boolean) {
  return apiClient.post<ImportRunResult>(`/import/targets/${moduleId}/${dryRun ? 'preview' : 'execute'}`, request)
}

/** 服务端记住的映射（按 用户 + 模块）；没记住过返回 null。 */
export function fetchImportMapping(moduleId: number) {
  return apiClient.get<ImportMappingSnapshot | null>(`/import/targets/${moduleId}/mapping`)
}

/** 记住这份映射：补导、换文件、换机器回来都能直接套用。 */
export function saveImportMapping(moduleId: number, sourceName: string | null, entries: ImportMappingEntry[]) {
  return apiClient.put<void>(`/import/targets/${moduleId}/mapping`, { sourceName, entries })
}

/** 前置资料就绪度：必填字段引用的主档还是空表时提前说出来。 */
export function fetchImportReadiness(moduleId: number) {
  return apiClient.get<ImportReadinessItem[]>(`/import/targets/${moduleId}/readiness`)
}

/** 触发浏览器下载：`URL.revokeObjectURL` 延后执行，否则部分浏览器来不及取走数据。 */
export function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.rel = 'noopener'
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
