import { IconArrowBackUp, IconDeviceFloppy } from '@tabler/icons-react'
import { useMutation } from '@tanstack/react-query'
import { useMemo, useState, type CSSProperties } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { apiClient } from '../../services/api'
import { newIdempotencyKey, type RecordBundle, type RecordSaveResponse } from '../document-workbench/formEditorUtils'
import { EDITION_PATTERN, importBomLines } from './bomApi'

const MODULE_ID = 1204

/**
 * 新建产品 BOM（模块 1204 定制页）。
 *
 * 只让用户填**料号**：版次是记录身份，新建固定落到 `01`（DB 默认值），换版走
 * 列表行的「新建版本」——那条路会复制明细，这条路不会。
 *
 * 「复制自」是建 BOM 的最高频方式（同族产品结构相近）：先建空单，再把源版次的
 * 明细导入过来。导入复用取试算表那条端点，所以子件存在性、底数、损耗率、项次分配
 * 全部走同一套校验，不因为"是复制来的"就放宽。
 */
export function BomProductNewPage() {
  const navigate = useNavigate()
  const toast = useToast()
  const [proNo, setProNo] = useState('')
  const [copySource, setCopySource] = useState('')
  const [copyEdition, setCopyEdition] = useState('01')
  const [chooser, setChooser] = useState<'target' | 'source' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: async () => {
      const target = proNo.trim()
      // `EDITION` 必须显式提交：它是 `BOM_STRU_M` 的主键列之一，
      // 服务端的主键校验在 DB 默认值之前执行——不传就报「主键字段 EDITION 不能为空」。
      // 版次固定 01，换版走列表行的「新建版本」（那条路会复制明细）
      const saved = await apiClient.post<RecordSaveResponse>(`/document-workbench/${MODULE_ID}/record`, {
        values: { PRO_NO: target, EDITION: '01' },
        idempotencyKey: newIdempotencyKey(),
      })
      const key = saved?.key?.length === 2 ? saved.key : [target, '01']

      const source = copySource.trim()
      if (source) {
        const bundle = await apiClient.get<RecordBundle>(`/document-workbench/${MODULE_ID}/record`, {
          query: { key: JSON.stringify([source, copyEdition.trim() || '01']) },
        })
        const rows = bundle.details
          .map(row => ({
            elementProNo: String(row.ELEMENT_PRO_NO ?? '').trim(),
            // 复制必须带上子件版本，否则「复制自」产出的半成品行为空，
            // 一保存就被 require-when-referenced 拦下
            elementEdition: row.ELEMENT_EDITION == null || String(row.ELEMENT_EDITION).trim() === ''
              ? null
              : String(row.ELEMENT_EDITION).trim(),
            qty: Number(row.ELEMENT_QTY ?? 0),
            baseQty: Number(row.BASE_QTY ?? 1) || 1,
            lostRate: Number(row.LOST_RATE ?? 0),
            remark: row.REMARK == null ? null : String(row.REMARK),
          }))
          .filter(row => row.elementProNo !== '')
        if (rows.length > 0) await importBomLines(key[0], key[1], false, rows)
      }
      return key
    },
    onSuccess: key => {
      toast.notify({ message: `已创建 ${key.join(' / ')}`, variant: 'success' })
      // 落盘后进**浏览态**：结构树是 1204 唯一的浏览入口，独立的浏览页已删除
      navigate(`/bom/product/${key.map(encodeURIComponent).join('/')}/tree`)
    },
    onError: cause => setError(describeApiError(cause, '创建失败。')),
  })

  const commands = useMemo<ErpCommandItem[]>(() => [
    {
      action: 'save',
      icon: <IconDeviceFloppy size={16} />,
      label: '创建',
      variant: 'primary',
      disabled: proNo.trim() === '',
      loading: create.isPending,
      onClick: () => create.mutate(),
    },
    {
      action: 'back',
      icon: <IconArrowBackUp size={16} />,
      label: '返回列表',
      // 回定制列表，不是统一工作台（1204 已不归工作台管）
      onClick: () => navigate('/bom/product'),
    },
  ], [create, navigate, proNo])

  const copyInvalid = copySource.trim() !== '' && !EDITION_PATTERN.test(copyEdition.trim())

  return (
    <div className="erp-form-page erp-bom-form-page">
      {error ? <div className="alert alert-danger mb-0">{error}</div> : null}
      <section className="card erp-form-card">
        <div className="card-body">
          <ErpCommandBar items={commands} ariaLabel="新建产品 BOM" />
          {/* 布局用统一表单的 `erp-form-grid`（两列、间距由同一套 CSS 决定），
              不自己写 `row g-3` + `col-md-*`——那会让字段间距与统一表单差一档 */}
          <div className="erp-form-grid">
            {/* `erp-form-grid` 只是外层容器（display:block，负责边框与内边距），
                真正的列布局在 `erp-form-row` 上——漏了这一层，字段会纵向堆叠 */}
            <div className="erp-form-row" style={{ '--erp-form-cols': 2 } as CSSProperties}>
            <div>
              <label className="form-label" htmlFor="bom-new-pro-no">料号</label>
              <div className="d-flex gap-2">
                <input
                  id="bom-new-pro-no"
                  className="form-control"
                  value={proNo}
                  placeholder="选择或输入料号"
                  onChange={event => { setProNo(event.target.value); setError(null) }}
                />
                <Button onClick={() => setChooser('target')}>选择</Button>
              </div>
              <div className="form-text">版次固定为 01；换版请在列表行用「新建版本」。</div>
            </div>
            <div>
              <label className="form-label" htmlFor="bom-new-copy-source">复制自（可选）</label>
              <div className="d-flex gap-2">
                <input
                  id="bom-new-copy-source"
                  className="form-control"
                  value={copySource}
                  placeholder="源料号"
                  onChange={event => setCopySource(event.target.value)}
                />
                <Button onClick={() => setChooser('source')}>选择</Button>
              </div>
              <div className="form-text">
                填了源料号就把它的明细复制过来；子件存在性、底数、损耗率与手工录入同一套校验。
              </div>
            </div>
            <div>
              <label className="form-label" htmlFor="bom-new-copy-edition">源版次</label>
              <input
                id="bom-new-copy-edition"
                className={`form-control${copyInvalid ? ' is-invalid' : ''}`}
                value={copyEdition}
                maxLength={2}
                placeholder="如 01"
                onChange={event => setCopyEdition(event.target.value.trim())}
              />
              {copyInvalid ? <div className="invalid-feedback">版次必须是两位数字。</div> : null}
            </div>
            </div>
          </div>
        </div>
      </section>

      {chooser ? (
        <UnifiedChooser
          open
          title={chooser === 'target' ? '选择料号' : '选择源料号'}
          source={{ kind: 'formField', moduleId: String(MODULE_ID), fieldKey: 'PRO_NO', serialNo: null }}
          mode="single"
          masterValues={{}}
          onPick={(rows: UnifiedChooserRow[]) => {
            const picked = rows.length > 0 ? String(rows[0].PRO_NO ?? '').trim() : ''
            if (chooser === 'target') setProNo(picked)
            else setCopySource(picked)
            setChooser(null)
          }}
          onClose={() => setChooser(null)}
          resizable
          storageKey="chooser-bom-new-pro-no"
          emptyText="没有可选数据。"
        />
      ) : null}
    </div>
  )
}
