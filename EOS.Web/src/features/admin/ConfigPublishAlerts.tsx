/**
 * 配置面「保存即发布」的结果展示（ADR-033 §7.3 方案 B），两个配置面（230103 按钮 / 230104 动作+规则）共用。
 *
 * 语义要点：保存**已经提交**，发布可能没通过——那时的正确说法是"**已保存，但未生效**"，
 * 并把发布校验的逐条原因列出来（`checks[]`）。不要把它显示成"保存失败"：那会让人以为改动丢了。
 */

/** 发布校验项（与后端 `WorkbenchDefinitionValidationCheck` 对应）。 */
export interface PublishValidationCheck {
  code: string
  passed: boolean
  message: string
  severity: string
}

/** 发布结果（与后端 `WorkbenchPublishResult` 对应）。 */
export interface WorkbenchPublishResult {
  moduleId: number
  title: string
  published: boolean
  version: number | null
  definitionVersion: string | null
  passed: boolean
  checks: PublishValidationCheck[]
  error?: string | null
}

/** 配置面保存的响应：保存本身已提交，发布结果随行。 */
export interface ConfigSaveOutcome {
  saved: boolean
  publish: WorkbenchPublishResult | null
}

export function ConfigPublishAlerts({ outcome }: { outcome: ConfigSaveOutcome | null }) {
  const publish = outcome?.publish
  if (!publish) return null

  if (publish.published) {
    return (
      <div className="alert alert-success py-2 mb-0" role="alert">
        已保存并发布：{publish.definitionVersion ?? `module-${publish.moduleId}-v${publish.version}`}。
      </div>
    )
  }

  const failed = publish.checks.filter((check) => !check.passed)
  return (
    <div className="alert alert-warning py-2 mb-0" role="alert">
      已保存，但<span className="fw-semibold">未生效</span>
      {failed.length > 0 ? '：发布未通过校验，未写入新快照。逐条原因：' : `：${publish.error ?? '发布未完成。'}`}
      {failed.length > 0 ? (
        <ul className="mb-0 mt-1">
          {failed.map((check) => (
            <li key={check.code}>{check.message}</li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}
