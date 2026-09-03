import { Button } from '../../components/ui/Button'
import {
  BASIC_RIGHTS,
  DENY_FIELDS,
  FILE_RIGHTS,
  type DenyFieldKey,
  type ModuleRightsInput,
  type ModuleRightsRow,
} from './types'

const EXEC_TAG_OPTIONS = Array.from({ length: 26 }, (_, index) => String.fromCharCode(65 + index))

/**
 * 单模块权限编辑面板：
 * RightsMatrix 的 modal 变体（formPanel）与页面变体原先各持一份约 100+ 行近似重复
 * （标题行 + 生效值预览 + EXEC_TAG + 基本/文件权限 + 字段级拒绝/DATA_FILTER），
 * 仅预览区样式与 EXEC_TAG 布局不同，统一到本组件（variant 控制差异）。
 */
export interface PermissionEditPanelProps {
  variant: 'modal' | 'page'
  mode: 'user' | 'group'
  row: ModuleRightsRow
  draft: ModuleRightsInput
  dirty: boolean
  onChange: (key: string, value: string | boolean) => void
  onOpenFieldPicker: (denyKey: DenyFieldKey) => void
}

export function SourceBadge({ row, mode }: { row: ModuleRightsRow; mode: 'user' | 'group' }) {
  if (row.hasPersonal) {
    return <span className="badge bg-primary-subtle text-primary">{mode === 'user' ? '个人' : '已配置'}</span>
  }
  if (row.effective.source === 'group') return <span className="badge bg-secondary-subtle text-secondary">组</span>
  return <span className="badge bg-light text-secondary">无</span>
}

export function PermissionEditPanel({ variant, mode, row, draft, dirty, onChange, onOpenFieldPicker }: PermissionEditPanelProps) {
  const sourceBadge = <SourceBadge row={row} mode={mode} />
  return (
    <div className="d-flex flex-column gap-3">
      <div className="d-flex align-items-center gap-2">
        <h3 className="mb-0">{row.title}</h3>
        {sourceBadge}
        <span className="text-secondary small">{row.groupPath || '顶层模块'}</span>
        <span className="badge bg-secondary-subtle text-secondary">{row.moduleId}</span>
      </div>
      {variant === 'modal' ? (
        <div className="alert alert-light border py-2 mb-0">
          <div className="fw-semibold small mb-1">生效值预览</div>
          <div className="d-flex flex-wrap gap-2 align-items-center">
            {row.effective.source === 'none' ? (
              <span className="badge bg-light text-secondary">无权限</span>
            ) : (
              <>
                <span className={`badge ${row.effective.source === 'personal' ? 'bg-primary-subtle text-primary' : 'bg-secondary-subtle text-secondary'}`}>
                  来源：{row.effective.source === 'personal' ? '个人' : '组'}
                </span>
                <span className="badge bg-secondary-subtle text-secondary">EXEC_TAG={row.effective.execTag}</span>
                <span className="badge bg-success-subtle text-success">浏览 {row.effective.canBrowse ? '允许' : '禁止'}</span>
                <span className="badge bg-info-subtle text-info">
                  新增/编辑/删除 {[row.effective.addNew, row.effective.edit, row.effective.delete].filter(Boolean).length} 项
                </span>
                <span className="badge bg-warning-subtle text-warning">禁止查看主表字段 {row.effective.denyViewMaster.length} 个</span>
              </>
            )}
            {dirty && <span className="badge bg-warning-subtle text-warning">已修改（保存后刷新生效值）</span>}
          </div>
        </div>
      ) : (
        <div className="alert alert-light border py-2 mb-0 small">
          <strong>生效值预览：</strong>
          {row.effective.source === 'none' ? '无权限' : (
            <>
              来源 {row.effective.source === 'personal' ? '个人' : '组'}，EXEC_TAG={row.effective.execTag}，
              浏览 {row.effective.canBrowse ? '允许' : '禁止'}，新增/编辑/删除{' '}
              {[row.effective.addNew, row.effective.edit, row.effective.delete].filter(Boolean).length} 项，
              禁止查看主表字段 {row.effective.denyViewMaster.length} 个
            </>
          )}
          {dirty && <span className="badge bg-warning-subtle text-warning ms-2">已修改（保存后刷新生效值）</span>}
        </div>
      )}
      {variant === 'modal' ? (
        <div className="d-flex align-items-center gap-2 flex-wrap">
          <label className="form-label mb-0 text-nowrap" htmlFor="exec-tag">执行级别 EXEC_TAG（A=禁止执行）</label>
          <select
            id="exec-tag"
            className="form-select form-select-sm w-auto"
            value={draft.execTag ?? 'A'}
            onChange={(event) => onChange('execTag', event.target.value)}
          >
            {EXEC_TAG_OPTIONS.map((tag) => <option key={tag} value={tag}>{tag}</option>)}
          </select>
        </div>
      ) : (
        <div className="row g-2">
          <div className="col-md-4">
            <label className="form-label" htmlFor="exec-tag">执行级别 EXEC_TAG（A=禁止执行）</label>
            <select
              id="exec-tag"
              className="form-select"
              value={draft.execTag ?? 'A'}
              onChange={(event) => onChange('execTag', event.target.value)}
            >
              {EXEC_TAG_OPTIONS.map((tag) => <option key={tag} value={tag}>{tag}</option>)}
            </select>
          </div>
        </div>
      )}
      <div>
        <div className="form-label mb-1">基本操作</div>
        <div className="d-flex flex-wrap gap-3">
          {BASIC_RIGHTS.map((item) => (
            <label key={item.key} className="d-flex align-items-center gap-2 form-check-label small">
              <input
                type="checkbox"
                className="form-check-input m-0"
                checked={Boolean(draft[item.key])}
                onChange={(event) => onChange(item.key, event.target.checked)}
              />
              {item.label}
            </label>
          ))}
          {FILE_RIGHTS.map((item) => (
            <label key={item.key} className="d-flex align-items-center gap-2 form-check-label small">
              <input
                type="checkbox"
                className="form-check-input m-0"
                checked={Boolean(draft[item.key])}
                onChange={(event) => onChange(item.key, event.target.checked)}
              />
              {item.label}
            </label>
          ))}
        </div>
      </div>
      <details>
        <summary className="form-label mb-1 cursor-pointer">高级权限（字段级拒绝 / DATA_FILTER）</summary>
        <div className="d-grid gap-2 mt-2">
          {DENY_FIELDS.map((deny) => (
            <div className="d-flex align-items-center gap-2" key={deny.key}>
              <label className="form-label mb-0 text-nowrap small" style={{ width: 110 }}>{deny.label}</label>
              <input
                className="form-control form-control-sm font-monospace"
                value={String(draft[deny.key] ?? '')}
                onChange={(event) => onChange(deny.key, event.target.value)}
                placeholder="字段ID，逗号或分号分隔"
              />
              <Button size="sm" variant="secondary" onClick={() => onOpenFieldPicker(deny.key)}>选择字段</Button>
            </div>
          ))}
          <div className="d-flex align-items-start gap-2">
            <label className="form-label mb-0 text-nowrap small" style={{ width: 110, paddingTop: 6 }}>DATA_FILTER</label>
            <textarea
              className="form-control form-control-sm font-monospace"
              rows={2}
              value={draft.dataFilter ?? ''}
              onChange={(event) => onChange('dataFilter', event.target.value)}
              placeholder="行级过滤，受控表达式（如 STATE=1 AND PRO_TYPE='A'）；非法表达式保存时拒绝"
            />
          </div>
          <div className="alert alert-warning py-1 px-2 mb-0 small">DATA_FILTER / 字段级拒绝保存前会做受控校验，非法输入返回 400。</div>
        </div>
      </details>
    </div>
  )
}
