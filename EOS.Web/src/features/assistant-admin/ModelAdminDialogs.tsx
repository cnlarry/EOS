import { useMutation, useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import {
  createModel, createProvider, discoverModels, listPresets, setProviderKey, updateModel, updateProvider,
} from './api'
import type {
  AssistantModelItem, AssistantModelKind, AssistantModelPreset,
  AssistantModelWriteInput, AssistantProviderItem, AssistantProviderPreset, AssistantProviderWriteInput,
} from './api'

/**
 * 现有知识库集合的维度。**只作提示的默认值，不是权威**——权威是集合行上的 `DIMENSION`，
 * 服务端会按它拒绝不一致的写入。放在这里是为了少填一次最常见的那个数（1024），
 * 而不是替人决定：改过集合维度的人要自己看一眼这句话。
 */
const KNOWN_COLLECTION_DIMENSION = 1024

const KIND_LABEL: Record<AssistantModelKind, string> = {
  CHAT: '对话',
  EMBEDDING: '嵌入',
}

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
  // 密钥环境变量名**可以为空**（无凭据端点，如本机嵌入服务）：库里那一列在迁移 307 放宽了，
  // 界面还要求必填的话就永远配不出这类端点
  const canSave = form.displayName.trim().length > 0 && form.baseUrl.trim().length > 0

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
                  ? '库里只存这个变量名；密钥稍后由「设置密钥」写入。留空表示这个端点不需要凭据（本机/内网服务）。'
                  : '库里只存这个变量名；密钥稍后由「设置密钥」写入，之后不再回显。留空 = 该端点不需要凭据。'}>
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
                          {/* 用途必须标出来：目录里同时有对话与嵌入模型时，光看型号名分不出
                              "这个能不能当对话模型用" */}
                          {' · '}{KIND_LABEL[item.kind]}
                          {item.dimension ? `（${item.dimension} 维）` : ''}
                          {item.kind === 'CHAT' && item.contextWindow
                            ? ` · 窗口 ${item.contextWindow.toLocaleString('zh-CN')}`
                            : ''}
                          {item.kind === 'CHAT' ? (item.supportsTools ? ' · 支持工具' : ' · 不支持工具') : ''}
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
    // 用途与维度**照目录带过来**：目录就是为"不用你猜"而存在的，落到库里再由服务端按用途校验
    kind: preset.kind,
    dimension: preset.dimension,
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

/**
 * 模型编辑 / 新增：用途与维度（嵌入）/ 标识 / 窗口 / 输出 / 温度 / 超时覆盖 / 单价 / 工具能力。
 *
 * <p>`model` 为 <c>null</c> = **新增**。一条路共用同一个表单：新增与编辑的字段集完全一样，
 * 分成两个对话框只会让"新增时漏掉某个字段"这种错在两边不同步。</p>
 */
export function ModelEditorDialog({ model, providerId, providers, onClose, onSaved }: {
  model: AssistantModelItem | null
  providerId: number
  providers: AssistantProviderItem[]
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const [form, setForm] = useState<AssistantModelWriteInput>({
    providerId,
    modelCode: model?.modelCode ?? '',
    displayName: model?.displayName ?? '',
    contextWindow: model?.contextWindow ?? null,
    maxOutputTokens: model?.maxOutputTokens ?? null,
    defaultTemperature: model?.defaultTemperature ?? null,
    timeoutSeconds: model?.timeoutSeconds ?? null,
    inputPerMillionYuan: model?.inputPerMillionYuan ?? null,
    outputPerMillionYuan: model?.outputPerMillionYuan ?? null,
    supportsTools: model?.supportsTools ?? true,
    enabled: model?.enabled ?? true,
    sortIdx: model?.sortIdx ?? 0,
    remark: model?.remark ?? null,
    // 用途**始终带上当前值**，不依赖服务端缺省：缺省成对话会让一次"只改单价"的编辑
    // 把嵌入模型变成对话模型，而这件事不会有任何报错
    kind: model?.kind ?? 'CHAT',
    dimension: model?.dimension ?? null,
  })
  // 可空的数字用文本框承载：留空 = 不传该参数，与"填 0"是两回事
  const [texts, setTexts] = useState({
    contextWindow: optionalText(model?.contextWindow),
    maxOutputTokens: optionalText(model?.maxOutputTokens),
    defaultTemperature: optionalText(model?.defaultTemperature),
    timeoutSeconds: optionalText(model?.timeoutSeconds),
    inputPerMillionYuan: optionalText(model?.inputPerMillionYuan),
    outputPerMillionYuan: optionalText(model?.outputPerMillionYuan),
    dimension: optionalText(model?.dimension),
  })
  const set = useCallback(<K extends keyof typeof texts>(key: K, value: string) => {
    setTexts(previous => ({ ...previous, [key]: value }))
    setForm(previous => ({ ...previous, [key]: parseOptionalNumber(value) }))
  }, [])

  const setKind = useCallback((kind: AssistantModelKind) => {
    setForm(previous => ({
      ...previous,
      kind,
      // 切换用途时把**另一用途才有意义的字段清空**：留着它们会在保存时被一并写进库里，
      // 表现为一条嵌入模型带着"窗口 1048576、温度 0.3、支持工具"这类没人会去看的垃圾
      contextWindow: kind === 'CHAT' ? previous.contextWindow : null,
      maxOutputTokens: kind === 'CHAT' ? previous.maxOutputTokens : null,
      defaultTemperature: kind === 'CHAT' ? previous.defaultTemperature : null,
      outputPerMillionYuan: kind === 'CHAT' ? previous.outputPerMillionYuan : null,
      supportsTools: kind === 'CHAT' ? previous.supportsTools : false,
      // 维度留空时给个可用初值（现有集合就是 1024），但**仍然摆在屏幕上让人确认**：
      // 换成别的维度要先改集合，那一步不可能被这个默认值替人决定
      dimension: kind === 'EMBEDDING' ? (previous.dimension ?? KNOWN_COLLECTION_DIMENSION) : null,
    }))
    if (kind === 'EMBEDDING') {
      setTexts(previous => ({
        ...previous,
        contextWindow: '', maxOutputTokens: '', defaultTemperature: '', outputPerMillionYuan: '',
        dimension: previous.dimension.trim().length === 0
          ? String(KNOWN_COLLECTION_DIMENSION)
          : previous.dimension,
      }))
    }
  }, [])

  /**
   * 模型标识与预设对上时，**窗口与最大输出由厂商公开值决定，不给改**——这两个数改错了不会报错，
   * 只会让助手要么被厂商拒（算大了）、要么白丢历史（算小了），属于"改了没有意义、改坏看不出来"的参数。
   * 只有预设里没有的型号（自建端点、厂商刚出的新型号）才需要管理员自己填。
   */
  const presets = useQuery({ queryKey: ['assistant-admin-presets'], queryFn: listPresets })
  const providerCode = providers.find(item => item.providerId === form.providerId)?.code
  const presetModel = useMemo(
    () => presets.data
      ?.find(item => item.code === providerCode)
      ?.models.find(item => item.modelCode === form.modelCode),
    [presets.data, providerCode, form.modelCode])
  const lockedByPreset = presetModel !== undefined
  const isEmbedding = form.kind === 'EMBEDDING'
  const canSave = form.modelCode.trim().length > 0
    && form.displayName.trim().length > 0
    // 嵌入模型的维度是**必填**：没有它，服务端会拒（这里先挡住，省一次往返）
    && (!isEmbedding || (form.dimension != null && form.dimension > 0))

  const save = useMutation({
    // 新增那条把返回的 modelId 丢掉：这个对话框的调用方只关心"成了没有"，
    // 保留它会让两条分支的返回类型不一致（void 对 { modelId }）
    mutationFn: () => model
      ? updateModel(model.modelId, form)
      : createModel(form).then(() => undefined),
    onSuccess: () => {
      toast.notify({
        message: model ? '已保存，立即生效。' : '已新增该模型。接下来用「设为当前」让它生效。',
        variant: 'success',
      })
      onSaved()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  return (
    <Modal title={model ? `编辑模型：${model.displayName}` : '新增模型'} ariaLabel={model ? '编辑模型' : '新增模型'}
      size="lg" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={save.isPending} disabled={!canSave}
          onClick={() => save.mutate()}>保存</Button>
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
      {/* 用途与维度紧挨着身份：模型标识的含义取决于用途（同一个字符串不会既是对话模型又是嵌入模型） */}
      <div className="row">
        <div className="col-md-6">
          <Field label="用途" hint="对话模型用于聊天；嵌入模型用于知识库检索。两者各有一条“当前”。">
            <select className="form-select" value={form.kind} aria-label="用途"
              onChange={(event) => setKind(event.target.value as AssistantModelKind)}>
              <option value="CHAT">对话</option>
              <option value="EMBEDDING">嵌入</option>
            </select>
          </Field>
        </div>
        <div className="col-md-6">
          {isEmbedding && (
            <Field label="维度"
              hint={`必须与知识库集合登记的维度一致（现有集合登记为 ${KNOWN_COLLECTION_DIMENSION}），否则入库会被拒。`}>
              <input className="form-control" inputMode="numeric" value={texts.dimension} aria-label="维度"
                placeholder="例如 1024"
                onChange={(event) => set('dimension', event.target.value)} />
            </Field>
          )}
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
      {isEmbedding ? (
        <>
          <div className="row">
            <div className="col-md-6">
              <Field label="超时覆盖（秒）" hint="留空 = 用供应商的默认超时。">
                <input className="form-control" inputMode="numeric" value={texts.timeoutSeconds} aria-label="超时覆盖"
                  placeholder="留空 = 用供应商默认" onChange={(event) => set('timeoutSeconds', event.target.value)} />
              </Field>
            </div>
            <div className="col-md-6">
              <Field label="输入单价（元/百万 token）" hint="留空 = 用全局兜底价。">
                <input className="form-control" inputMode="decimal" value={texts.inputPerMillionYuan} aria-label="输入单价"
                  placeholder="留空 = 全局兜底" onChange={(event) => set('inputPerMillionYuan', event.target.value)} />
              </Field>
            </div>
          </div>
          <div className="alert alert-info py-2">
            <div className="small">
              嵌入模型不摆上下文窗口、最大输出、温度、输出单价与工具调用这几项：它们不会出现在向量请求里，
              存了也无人读。需要的只有维度（决定向量能不能存）与输入单价（计费用）。
            </div>
          </div>
        </>
      ) : (
        <>
          <div className="row">
            <div className="col-md-6">
              <Field label="上下文窗口（token）"
                hint={lockedByPreset
                  ? '来自预设（厂商公开值），不需要改。'
                  : '会被用来裁剪历史；留空按保守默认 16384 处理。'}>
                <input className="form-control" inputMode="numeric" value={texts.contextWindow} aria-label="上下文窗口"
                  readOnly={lockedByPreset} placeholder="留空 = 未知"
                  onChange={(event) => set('contextWindow', event.target.value)} />
              </Field>
            </div>
            <div className="col-md-6">
              <Field label="最大输出（token）"
                hint={lockedByPreset ? '来自预设（厂商公开值），不需要改。' : '留空 = 不传该参数。'}>
                <input className="form-control" inputMode="numeric" value={texts.maxOutputTokens} aria-label="最大输出"
                  readOnly={lockedByPreset} placeholder="留空 = 厂商默认"
                  onChange={(event) => set('maxOutputTokens', event.target.value)} />
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
                  disabled={lockedByPreset}
                  onChange={(event) => setForm({ ...form, supportsTools: event.target.checked })} />
                <span className="form-check-label">
                  支持工具调用（不支持时不会带 tools 去请求）{lockedByPreset ? '　来自预设，不需要改' : ''}
                </span>
              </label>
            </div>
          </div>
        </>
      )}
      <div className="row">
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

/** 一条待落库的型号及其用途/维度。 */
interface DiscoverPick {
  kind: AssistantModelKind
  dimension: string
}

/**
 * 从厂商**拉取**可用型号，勾选后落库（ADR-030 §12.3）。
 *
 * <p>
 * **拉失败不退回预设清单**：那会把一次失败伪装成一次成功的列表，而管理员会拿着过期的型号去落库。
 * 失败按原因如实显示（密钥没配 / 被拒 / 这家没有该端点 / 网络超时各有各的说明），并指出下一步。
 * 所以这里 <c>retry: false</c>——自动重试会把"密钥不对"刷成"网络不稳定"，恰好把最有用的那条信息抹掉。
 * </p>
 *
 * <p>
 * **用途与维度逐条指定**：厂商的 <c>/models</c> 多数把对话与嵌入混在一起给，靠型号名猜用途
 * 会得到一个永远打不通的行（嵌入模型还要给维度，它决定向量能不能存）。
 * </p>
 */
export function DiscoverModelsDialog({ provider, onClose, onSaved }: {
  provider: AssistantProviderItem
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()
  const presets = useQuery({ queryKey: ['assistant-admin-presets'], queryFn: listPresets })
  const preset = presets.data?.find(item => item.code === provider.code)

  const discovered = useQuery({
    queryKey: ['assistant-admin-discover', provider.providerId],
    queryFn: () => discoverModels(provider.providerId),
    retry: false,
  })

  const [keyword, setKeyword] = useState('')
  const [picks, setPicks] = useState<Record<string, DiscoverPick>>({})

  /** 库里已有的型号不再列出：勾了也会被唯一约束拒，而拒在批量提交的中途会留下半截结果。 */
  const existing = useMemo(
    () => new Set(provider.models.map(item => item.modelCode)),
    [provider.models])

  const rows = useMemo(() => {
    const all = discovered.data?.models ?? []
    const text = keyword.trim().toLowerCase()
    return all.filter(item => !existing.has(item.modelCode)).filter(item =>
      text.length === 0
      || item.modelCode.toLowerCase().includes(text)
      || item.displayName.toLowerCase().includes(text))
  }, [discovered.data, existing, keyword])

  const toggle = (modelCode: string) => {
    setPicks(previous => {
      if (previous[modelCode]) {
        const next = { ...previous }
        delete next[modelCode]
        return next
      }
      return { ...previous, [modelCode]: { kind: 'CHAT', dimension: '' } }
    })
  }

  const setPick = (modelCode: string, patch: Partial<DiscoverPick>) => {
    setPicks(previous => ({ ...previous, [modelCode]: { ...previous[modelCode], ...patch } }))
  }

  const count = Object.keys(picks).length
  const missingDimension = Object.entries(picks)
    .some(([, pick]) => pick.kind === 'EMBEDDING' && (Number(pick.dimension) || 0) <= 0)

  const save = useMutation({
    // 逐条顺序创建：没有"批量加到已有供应商"的端点。中途失败时**如实说清已建成几个**——
    // 报一句"保存失败"会让管理员重来一次，然后撞在唯一约束上
    mutationFn: async () => {
      let created = 0
      for (const [modelCode, pick] of Object.entries(picks)) {
        const found = discovered.data?.models.find(item => item.modelCode === modelCode)
        const isEmbedding = pick.kind === 'EMBEDDING'
        await createModel({
          providerId: provider.providerId,
          modelCode,
          displayName: found?.displayName || modelCode,
          // 厂商给了窗口与最大输出就带上（DeepSeek 给，多数厂商不给），其余留空由人补
          contextWindow: isEmbedding ? null : found?.contextWindow ?? null,
          maxOutputTokens: isEmbedding ? null : found?.maxOutputTokens ?? null,
          defaultTemperature: null,
          timeoutSeconds: null,
          inputPerMillionYuan: null,
          outputPerMillionYuan: null,
          supportsTools: !isEmbedding,
          enabled: true,
          sortIdx: 0,
          remark: null,
          kind: pick.kind,
          dimension: isEmbedding ? Number(pick.dimension) : null,
        })
        created++
      }
      return created
    },
    onSuccess: (created) => {
      toast.notify({ message: `已新增 ${created} 个模型。接下来在需要的那一条上点「设为当前」。`, variant: 'success' })
      onSaved()
    },
    onError: (error) => toast.notify({
      message: `新增中断：${describeApiError(error, '新增模型失败。')}——已成功落库的那些会保留，可关闭后刷新查看。`,
      variant: 'danger',
    }),
  })

  return (
    <Modal title={`从厂商拉取型号：${provider.displayName}`} ariaLabel="从厂商拉取型号" size="lg" onClose={onClose}
      footer={<>
        <Button variant="secondary" onClick={onClose}>关闭</Button>
        <Button variant="primary" loading={save.isPending} disabled={count === 0 || missingDimension}
          onClick={() => save.mutate()}>
          落库{count > 0 ? `（${count} 个）` : ''}
        </Button>
      </>}>
      {/* 能力三态：明确说"这家没有嵌入端点"或"没核过"，而不是让人在一个注定失败的端点上反复试 */}
      {preset?.embedding === 'Unsupported' && (
        <div className="alert alert-warning py-2">
          <div className="small">
            这家已核实<strong>没有嵌入端点</strong>：勾选对话模型没问题，但把它当嵌入模型用会发不出去。
          </div>
        </div>
      )}
      {preset?.embedding === 'Unknown' && (
        <div className="alert alert-info py-2">
          <div className="small">
            这家<strong>有没有嵌入端点还没核实</strong>：要用嵌入的话，落库后建议先试一次知识库入库再定。
          </div>
        </div>
      )}
      {preset?.modelListing === 'Unsupported' && (
        <div className="alert alert-warning py-2">
          <div className="small">
            这家<strong>没有列出型号的端点</strong>：这一页拿不到清单。请关掉它，用「新增模型」手工填型号标识。
          </div>
        </div>
      )}

      {discovered.isPending ? <LoadingState label="正在向厂商拉取型号…" /> : discovered.isError ? (
        <ErrorState
          message={describeApiError(discovered.error, '拉取型号失败。请检查端点与密钥，或稍后重试。')}
          onRetry={() => void discovered.refetch()} />
      ) : (
        <>
          <div className="row g-2 align-items-end mb-2">
            <div className="col-md-6">
              <label className="form-label small mb-1" htmlFor="discover-keyword">筛选</label>
              <input id="discover-keyword" className="form-control form-control-sm font-monospace"
                value={keyword} placeholder="按型号名筛选（厂商可能给几百条）"
                onChange={(event) => setKeyword(event.target.value)} />
            </div>
            <div className="col-md-6 text-secondary small">
              厂商这次给了 {discovered.data?.models.length ?? 0} 条，其中 {rows.length} 条还没落库。
            </div>
          </div>

          {rows.length === 0 ? (
            <div className="text-secondary small p-2">
              {existing.size > 0 && (discovered.data?.models.length ?? 0) > 0
                ? '厂商给的型号都已经在库里了。'
                : '这家这次没有给出可落库的型号。'}
            </div>
          ) : (
            <div className="d-flex flex-column gap-1" style={{ maxHeight: 360, overflowY: 'auto' }}>
              {rows.map(item => {
                const pick = picks[item.modelCode]
                return (
                  <div key={item.modelCode} className="border rounded p-2">
                    <div className="d-flex align-items-center gap-2 flex-wrap">
                      <label className="form-check mb-0">
                        <input type="checkbox" className="form-check-input" checked={pick !== undefined}
                          aria-label={item.modelCode}
                          onChange={() => toggle(item.modelCode)} />
                        <span className="form-check-label">
                          <span className="font-monospace">{item.modelCode}</span>
                          <span className="text-secondary small ms-2">
                            {item.displayName}
                            {item.contextWindow ? ` · 窗口 ${item.contextWindow.toLocaleString('zh-CN')}` : ''}
                            {item.maxOutputTokens ? ` · 最大输出 ${item.maxOutputTokens.toLocaleString('zh-CN')}` : ''}
                          </span>
                        </span>
                      </label>

                      {pick && (
                        <span className="d-flex align-items-center gap-1 ms-auto">
                          <select className="form-select form-select-sm" style={{ width: 110 }}
                            aria-label={`${item.modelCode} 的用途`} value={pick.kind}
                            onChange={(event) => setPick(item.modelCode, {
                              kind: event.target.value as AssistantModelKind,
                            })}>
                            <option value="CHAT">对话</option>
                            <option value="EMBEDDING">嵌入</option>
                          </select>
                          {pick.kind === 'EMBEDDING' && (
                            <input className="form-control form-control-sm" style={{ width: 110 }}
                              inputMode="numeric" placeholder="维度" aria-label={`${item.modelCode} 的维度`}
                              value={pick.dimension}
                              onChange={(event) => setPick(item.modelCode, { dimension: event.target.value })} />
                          )}
                        </span>
                      )}
                    </div>
                    {pick?.kind === 'EMBEDDING' && (
                      <div className="form-hint">
                        维度必须与知识库集合登记的一致（现有集合登记为 {KNOWN_COLLECTION_DIMENSION}），否则入库会被拒。
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          )}
        </>
      )}
    </Modal>
  )
}
