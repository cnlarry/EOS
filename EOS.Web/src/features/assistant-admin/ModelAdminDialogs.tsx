import { useMutation, useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { createProvider, listPresets, setProviderKey, updateModel, updateProvider } from './api'
import type {
  AssistantModelItem, AssistantModelPreset, AssistantModelWriteInput,
  AssistantProviderItem, AssistantProviderPreset, AssistantProviderWriteInput,
} from './api'

/**
 * 空字符串代表"不传该参数"，所以这里区分"空"与"0"。
 *
 * <p>刻意不导出：这个文件只导出组件，否则 React Fast Refresh 会失效（改了组件不热更新）。</p>
 */
function parseOptionalNumber(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed.length === 0) return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
}

function optionalText(value: number | null | undefined): string {
  return value == null ? '' : String(value)
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="mb-2">
      <label className="form-label mb-1">{label}</label>
      {children}
      {hint && <div className="form-hint">{hint}</div>}
    </div>
  )
}

/**
 * 从**预设目录**添加供应商：选一家 → 自动带出端点与它的可用模型 → 勾选要启用的 → 一次提交。
 *
 * <p>
 * 价格来自预设（多数为"留空 = 用全局兜底价"，因为各家单价随账号折扣与时段变），
 * 落库后可以在模型上逐条改——预设给的是初值，不是权威。
 * </p>
 */
export function PresetPickerDialog({ usedCodes, onClose, onSaved }: {
  usedCodes: string[]
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const presets = useQuery({ queryKey: ['assistant-admin-presets'], queryFn: listPresets })

  // 已经存在同 CODE 的供应商就不再列出来：CODE 上有唯一约束，重复添加只会在服务端撞一个
  // 看不懂的错（也让界面无法解释"为什么它不让加"）
  const available = useMemo(
    () => (presets.data ?? []).filter(item => !usedCodes.includes(item.code)),
    [presets.data, usedCodes],
  )

  const [code, setCode] = useState('')
  const preset: AssistantProviderPreset | undefined = useMemo(
    () => available.find(item => item.code === code) ?? available[0],
    [available, code],
  )

  const [form, setForm] = useState<AssistantProviderWriteInput>({
    code: '', displayName: '', baseUrl: '', apiKeyEnvVar: '', timeoutSeconds: 300,
    enabled: true, sortIdx: 0, remark: null,
  })
  const [selected, setSelected] = useState<string[]>([])

  // 选中的预设换了就把表单带出来——这是"预设"的意义所在
  useEffect(() => {
    if (!preset) return
    setCode(preset.code)
    setForm({
      code: preset.code,
      displayName: preset.displayName,
      baseUrl: preset.baseUrl,
      apiKeyEnvVar: preset.suggestedApiKeyEnvVar,
      timeoutSeconds: preset.timeoutSeconds,
      enabled: true,
      sortIdx: 0,
      remark: preset.remark ?? null,
    })
    setSelected(preset.models.map(item => item.modelCode))
  }, [preset])

  const save = useMutation({
    mutationFn: () => createProvider({
      ...form,
      models: (preset?.models ?? [])
        .filter(item => selected.includes(item.modelCode))
        .map(item => toPresetModelWrite(item)),
    }),
    onSuccess: (result) => {
      toast.notify({
        message: `已添加供应商，并创建 ${result.modelsCreated} 个模型。接下来用「设置密钥」填一次密钥，再「设为当前」。`,
        variant: 'success',
      })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '添加失败。'), variant: 'danger' }),
  })

  const custom = preset?.code === 'custom'
  const canSave = form.displayName.trim().length > 0
    && form.baseUrl.trim().length > 0
    && form.apiKeyEnvVar.trim().length > 0

  return (
    <Modal title="添加供应商" ariaLabel="添加供应商" size="lg" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} disabled={!canSave}
          onClick={() => save.mutate()}>添加</Button>
      </>}>
      {presets.isPending ? <LoadingState label="正在加载预设目录…" /> : presets.isError ? (
        <ErrorState message={describeApiError(presets.error, '加载预设目录失败。')} onRetry={() => void presets.refetch()} />
      ) : (
        <>
          <Field label="供应商" hint="选一家就会带出它的端点、建议的环境变量名与可用模型（都可以改）。">
            <select className="form-select" value={preset?.code ?? ''} aria-label="预设供应商"
              onChange={(event) => setCode(event.target.value)}>
              {available.map(item => (
                <option key={item.code} value={item.code}>
                  {item.displayName}{item.models.length > 0 ? `（${item.models.length} 个模型）` : ''}
                </option>
              ))}
            </select>
          </Field>

          {/* 目录里的 CODE 都被占完时必须解释一句：否则这里是一个空下拉 + 一个永远点不动的按钮，
              看起来像页面坏了。这确实是个限制（CODE 决定客户端实现），说明白比藏起来好。 */}
          {available.length === 0 && (
            <div className="alert alert-warning py-2">
              <div className="small">
                预设目录里的供应商都已经添加过了。同一个 CODE 只能有一个接入点（CODE 决定用哪个客户端实现），
                所以要再加一个同类型的接入点，请先删掉已有的那一个。
              </div>
            </div>
          )}

          <div className="row">
            <div className="col-md-6">
              <Field label="显示名">
                <input className="form-control" value={form.displayName} aria-label="显示名"
                  onChange={(event) => setForm({ ...form, displayName: event.target.value })} />
              </Field>
            </div>
            <div className="col-md-6">
              <Field label="端点" hint="不含 /chat/completions 的根地址。" >
                <input className="form-control font-monospace" value={form.baseUrl} aria-label="端点"
                  placeholder="https://"
                  onChange={(event) => setForm({ ...form, baseUrl: event.target.value })} />
              </Field>
            </div>
          </div>

          <div className="row">
            <div className="col-md-6">
              <Field label="密钥环境变量名"
                hint={custom
                  ? '自定义供应商必须自己填一个变量名；密钥稍后由「设置密钥」写入。'
                  : '库里只存这个变量名；密钥稍后由「设置密钥」写入，之后不再回显。'}>
                <input className="form-control font-monospace" value={form.apiKeyEnvVar} aria-label="密钥环境变量名"
                  onChange={(event) => setForm({ ...form, apiKeyEnvVar: event.target.value })} />
              </Field>
            </div>
            <div className="col-md-6">
              <Field label="默认超时（秒）">
                <input type="number" className="form-control" value={form.timeoutSeconds} aria-label="默认超时"
                  onChange={(event) => setForm({ ...form, timeoutSeconds: Number(event.target.value) })} />
              </Field>
            </div>
          </div>

          {!custom && (
            <Field label="要启用的模型"
              hint="勾选的会一起落库。预设里的价格多数留空（= 用全局兜底价），落库后可以逐条改。">
              {(preset?.models.length ?? 0) === 0 ? (
                <div className="text-secondary small">这家预设没有内置模型清单，添加后自行新增。</div>
              ) : (
                <div className="d-flex flex-column gap-1">
                  {preset!.models.map(item => (
                    <label key={item.modelCode} className="form-check mb-0">
                      <input type="checkbox" className="form-check-input"
                        checked={selected.includes(item.modelCode)}
                        aria-label={item.modelCode}
                        onChange={(event) => setSelected(event.target.checked
                          ? [...selected, item.modelCode]
                          : selected.filter(code2 => code2 !== item.modelCode))} />
                      <span className="form-check-label">
                        <span className="font-monospace">{item.modelCode}</span>
                        <span className="text-secondary small ms-2">
                          {item.displayName}
                          {item.contextWindow ? ` · 窗口 ${item.contextWindow.toLocaleString('zh-CN')}` : ''}
                          {item.supportsTools ? ' · 支持工具' : ' · 不支持工具'}
                        </span>
                      </span>
                    </label>
                  ))}
                </div>
              )}
            </Field>
          )}

          <div className="alert alert-info py-2 mb-0">
            <div className="small">
              添加后还差两步才能用：①「设置密钥」填一次密钥（只写不读，写进环境变量）；
              ② 在某个模型上点「设为当前」。两步都不需要重启服务。
            </div>
          </div>
        </>
      )}
    </Modal>
  )
}

function toPresetModelWrite(preset: AssistantModelPreset): AssistantModelWriteInput {
  return {
    modelCode: preset.modelCode,
    displayName: preset.displayName,
    contextWindow: preset.contextWindow,
    maxOutputTokens: preset.maxOutputTokens,
    defaultTemperature: preset.defaultTemperature,
    timeoutSeconds: null,
    inputPerMillionYuan: preset.inputPerMillionYuan,
    outputPerMillionYuan: preset.outputPerMillionYuan,
    supportsTools: preset.supportsTools,
    enabled: true,
    sortIdx: 0,
    remark: preset.remark,
  }
}

/** 供应商编辑：端点 / 密钥变量名 / 默认超时 / 启用 / 排序。CODE 只能从目录里选。 */
export function ProviderEditorDialog({ provider, usedCodes, onClose, onSaved }: {
  provider: AssistantProviderItem
  usedCodes: string[]
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const presets = useQuery({ queryKey: ['assistant-admin-presets'], queryFn: listPresets })
  const [form, setForm] = useState<AssistantProviderWriteInput>({
    code: provider.code,
    displayName: provider.displayName,
    baseUrl: provider.baseUrl,
    apiKeyEnvVar: provider.apiKeyEnvVar,
    timeoutSeconds: provider.timeoutSeconds,
    enabled: provider.enabled,
    sortIdx: provider.sortIdx,
    remark: provider.remark,
  })

  const save = useMutation({
    mutationFn: () => updateProvider(provider.providerId, form),
    onSuccess: () => {
      toast.notify({ message: '已保存，立即生效。', variant: 'success' })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  // CODE 决定用哪个客户端实现，所以只能取目录里有的；已占用的其它 CODE 不再列出
  const codes = (presets.data ?? [])
    .filter(item => item.code === provider.code || !usedCodes.includes(item.code))
    .map(item => item.code)

  return (
    <Modal title={`编辑供应商：${provider.displayName}`} ariaLabel="编辑供应商" size="lg" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>保存</Button>
      </>}>
      {/* 与"编辑模型"同一口径：一律两列等宽、按语义两两配对 */}
      <div className="row">
        <div className="col-md-6">
          <Field label="供应商类型" hint="它决定用哪个客户端实现，所以只能从目录里选。">
            <select className="form-select" value={form.code} aria-label="供应商类型"
              onChange={(event) => setForm({ ...form, code: event.target.value })}>
              {codes.map(item => <option key={item} value={item}>{item}</option>)}
            </select>
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="显示名">
            <input className="form-control" value={form.displayName} aria-label="显示名"
              onChange={(event) => setForm({ ...form, displayName: event.target.value })} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="端点" hint="不含 /chat/completions 的根地址。改它会让正在用的模型换一个发请求的地方。">
            <input className="form-control font-monospace" value={form.baseUrl} aria-label="端点"
              onChange={(event) => setForm({ ...form, baseUrl: event.target.value })} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="密钥环境变量名" hint="改这个只是换一个变量名，值要用「设置密钥」重新写一次。">
            <input className="form-control font-monospace" value={form.apiKeyEnvVar} aria-label="密钥环境变量名"
              onChange={(event) => setForm({ ...form, apiKeyEnvVar: event.target.value })} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="默认超时（秒）" hint="模型可以单独覆盖。">
            <input type="number" className="form-control" value={form.timeoutSeconds} aria-label="默认超时"
              onChange={(event) => setForm({ ...form, timeoutSeconds: Number(event.target.value) })} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="排序号">
            <input type="number" className="form-control" value={form.sortIdx} aria-label="排序号"
              onChange={(event) => setForm({ ...form, sortIdx: Number(event.target.value) })} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6 d-flex align-items-end pb-2">
          <label className="form-check">
            <input type="checkbox" className="form-check-input" checked={form.enabled} aria-label="启用"
              onChange={(event) => setForm({ ...form, enabled: event.target.checked })} />
            <span className="form-check-label">启用（停用 = 名下所有模型一起下线）</span>
          </label>
        </div>
      </div>
      <Field label="备注">
        <input className="form-control" value={form.remark ?? ''} aria-label="备注"
          onChange={(event) => setForm({ ...form, remark: event.target.value || null })} />
      </Field>
    </Modal>
  )
}

/** 设置供应商密钥：只写不读。 */
export function ProviderKeyDialog({ provider, onClose, onSaved }: {
  provider: AssistantProviderItem
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const [apiKey, setApiKey] = useState('')

  const save = useMutation({
    mutationFn: () => setProviderKey(provider.providerId, apiKey.trim()),
    onSuccess: (result) => {
      // persisted 为假说明只有本次进程生效：不能只说"成功"，否则重启后助手会突然不可用
      toast.notify({
        message: result.persisted
          ? `密钥已写入环境变量 ${result.envVar}（${result.maskedTail}），当前进程已生效。`
          : `密钥已写入环境变量 ${result.envVar}（${result.maskedTail}），但只对当前进程生效——`
            + '该环境不允许写用户级变量，重启后需要重新设置。',
        variant: result.persisted ? 'success' : 'warning',
      })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '写入密钥失败。'), variant: 'danger' }),
  })

  return (
    <Modal title={`设置密钥：${provider.displayName}`} ariaLabel="设置密钥" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} disabled={apiKey.trim().length === 0}
          onClick={() => save.mutate()}>写入</Button>
      </>}>
      <div className="alert alert-warning py-2">
        <div className="fw-semibold">密钥不入库、也不会再显示出来</div>
        <div className="small">
          它会被写进环境变量 <code>{provider.apiKeyEnvVar}</code>（当前进程立即生效，并尽量持久化到当前用户），
          数据库里只保留这个变量名。这一把密钥由该供应商名下所有模型共用。提交后无法回看，只能重新设置。
        </div>
      </div>
      <label className="form-label mb-1">密钥</label>
      <input type="password" className="form-control font-monospace" value={apiKey} autoFocus
        aria-label="密钥" autoComplete="new-password"
        onChange={(event) => setApiKey(event.target.value)} />
      {provider.apiKeyConfigured && (
        <div className="form-hint">当前已配置 {provider.apiKeyMaskedTail}，写入会覆盖它。</div>
      )}
    </Modal>
  )
}

/** 模型编辑：标识 / 窗口 / 输出 / 温度 / 超时覆盖 / 单价 / 工具能力。 */
export function ModelEditorDialog({ model, providerId, providers, onClose, onSaved }: {
  model: AssistantModelItem
  providerId: number
  providers: AssistantProviderItem[]
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const [form, setForm] = useState<AssistantModelWriteInput>({
    providerId,
    modelCode: model.modelCode,
    displayName: model.displayName,
    contextWindow: model.contextWindow,
    maxOutputTokens: model.maxOutputTokens,
    defaultTemperature: model.defaultTemperature,
    timeoutSeconds: model.timeoutSeconds,
    inputPerMillionYuan: model.inputPerMillionYuan,
    outputPerMillionYuan: model.outputPerMillionYuan,
    supportsTools: model.supportsTools,
    enabled: model.enabled,
    sortIdx: model.sortIdx,
    remark: model.remark,
  })
  // 可空的数字用文本框承载：留空 = 不传该参数，与"填 0"是两回事
  const [texts, setTexts] = useState({
    contextWindow: optionalText(model.contextWindow),
    maxOutputTokens: optionalText(model.maxOutputTokens),
    defaultTemperature: optionalText(model.defaultTemperature),
    timeoutSeconds: optionalText(model.timeoutSeconds),
    inputPerMillionYuan: optionalText(model.inputPerMillionYuan),
    outputPerMillionYuan: optionalText(model.outputPerMillionYuan),
  })

  const set = useCallback(<K extends keyof typeof texts>(key: K, value: string) => {
    setTexts(previous => ({ ...previous, [key]: value }))
    setForm(previous => ({ ...previous, [key]: parseOptionalNumber(value) }))
  }, [])

  const save = useMutation({
    mutationFn: () => updateModel(model.modelId, form),
    onSuccess: () => {
      toast.notify({ message: '已保存，立即生效。', variant: 'success' })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  return (
    <Modal title={`编辑模型：${model.displayName}`} ariaLabel="编辑模型" size="lg" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>保存</Button>
      </>}>
      {/* 一律两列等宽：字段按语义两两配对（身份 / 展示 / 量与行为 / 价钱 / 开关），
          不再出现"三个一组"（那会让第三项独占一行的左半边，看起来像没对齐）。 */}
      <div className="row">
        <div className="col-md-6">
          <Field label="所属供应商">
            <select className="form-select" value={form.providerId} aria-label="所属供应商"
              onChange={(event) => setForm({ ...form, providerId: Number(event.target.value) })}>
              {providers.map(item => <option key={item.providerId} value={item.providerId}>{item.displayName}</option>)}
            </select>
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="模型标识" hint="真正发给厂商的那个字符串，例如 deepseek-reasoner。">
            <input className="form-control font-monospace" value={form.modelCode} aria-label="模型标识"
              onChange={(event) => setForm({ ...form, modelCode: event.target.value })} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="显示名">
            <input className="form-control" value={form.displayName} aria-label="显示名"
              onChange={(event) => setForm({ ...form, displayName: event.target.value })} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="排序号" hint="列表里的先后顺序。">
            <input type="number" className="form-control" value={form.sortIdx} aria-label="排序号"
              onChange={(event) => setForm({ ...form, sortIdx: Number(event.target.value) })} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="上下文窗口（token）" hint="会被用来裁剪历史；留空按保守默认 16384 处理。">
            <input className="form-control" inputMode="numeric" value={texts.contextWindow} aria-label="上下文窗口"
              placeholder="留空 = 未知" onChange={(event) => set('contextWindow', event.target.value)} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="最大输出（token）" hint="留空 = 不传该参数。">
            <input className="form-control" inputMode="numeric" value={texts.maxOutputTokens} aria-label="最大输出"
              placeholder="留空 = 厂商默认" onChange={(event) => set('maxOutputTokens', event.target.value)} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="默认温度" hint="0–2；留空 = 不传该参数。">
            <input className="form-control" inputMode="decimal" value={texts.defaultTemperature} aria-label="默认温度"
              placeholder="留空 = 厂商默认" onChange={(event) => set('defaultTemperature', event.target.value)} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="超时覆盖（秒）" hint="留空 = 用供应商的默认超时。">
            <input className="form-control" inputMode="numeric" value={texts.timeoutSeconds} aria-label="超时覆盖"
              placeholder="留空 = 用供应商默认" onChange={(event) => set('timeoutSeconds', event.target.value)} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6">
          <Field label="输入单价（元/百万 token）" hint="留空 = 用全局兜底价。">
            <input className="form-control" inputMode="decimal" value={texts.inputPerMillionYuan} aria-label="输入单价"
              placeholder="留空 = 全局兜底" onChange={(event) => set('inputPerMillionYuan', event.target.value)} />
          </Field>
        </div>
        <div className="col-md-6">
          <Field label="输出单价（元/百万 token）" hint="留空 = 用全局兜底价。">
            <input className="form-control" inputMode="decimal" value={texts.outputPerMillionYuan} aria-label="输出单价"
              placeholder="留空 = 全局兜底" onChange={(event) => set('outputPerMillionYuan', event.target.value)} />
          </Field>
        </div>
      </div>
      <div className="row">
        <div className="col-md-6 d-flex align-items-end pb-2">
          <label className="form-check">
            <input type="checkbox" className="form-check-input" checked={form.supportsTools} aria-label="支持工具调用"
              onChange={(event) => setForm({ ...form, supportsTools: event.target.checked })} />
            <span className="form-check-label">支持工具调用（不支持时不会带 tools 去请求）</span>
          </label>
        </div>
        <div className="col-md-6 d-flex align-items-end pb-2">
          <label className="form-check">
            <input type="checkbox" className="form-check-input" checked={form.enabled} aria-label="启用"
              onChange={(event) => setForm({ ...form, enabled: event.target.checked })} />
            <span className="form-check-label">启用（停用后不能设为当前）</span>
          </label>
        </div>
      </div>
      <Field label="备注">
        <input className="form-control" value={form.remark ?? ''} aria-label="备注"
          onChange={(event) => setForm({ ...form, remark: event.target.value || null })} />
      </Field>
    </Modal>
  )
}
