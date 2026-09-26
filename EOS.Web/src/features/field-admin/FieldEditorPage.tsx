import { IconArrowLeft, IconDeviceFloppy, IconPlus, IconX } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { usePageBreadcrumb } from '../../components/layout/PageBreadcrumbContext'
import { useTabDirty } from '../../components/layout/workspaceDirty'
import { apiClient } from '../../services/api'
import {
  FieldEditorForm,
  type ExpressionKind,
  type ExpressionPreview,
  type ExpressionValidation,
  type FieldEditorEndpoints,
  type FieldInput,
  type FieldMeta,
  type SetupLookup,
} from './FieldEditorForm'

interface FieldAdminMetadata {
  tableId: string
  fieldId: string
  field: FieldInput
  isVirtual: boolean
  virtualExpression: string | null
  isAutoIncrement: boolean
  convertFunction: string | null
  dataSourceSql: string | null
  lastUpdatedBy: string | null
  lastUpdatedAt: string | null
  isSystemColumn?: boolean
}

interface FieldSummaryItem {
  fieldId: string
  description: string
  dataType: string
  isVirtual: boolean
  isVisible: boolean
  isDefault: boolean
  isQueryable: boolean
  isReadonly: boolean
  isCost: boolean
  isSecrecy: boolean
  isPrimaryKey: boolean
  physicalExists: boolean
}

interface FieldPageResult {
  items: FieldSummaryItem[]
  total: number
}

function adminMetaToFieldMeta(meta: FieldAdminMetadata): FieldMeta {
  return {
    ...meta.field,
    key: meta.fieldId,
    tableId: meta.tableId,
    isVirtual: meta.isVirtual,
    virtualExpression: meta.virtualExpression,
    isAutoIncrement: meta.isAutoIncrement,
    convertFunction: meta.convertFunction,
    dataSourceSql: meta.dataSourceSql,
    lastUpdatedBy: meta.lastUpdatedBy,
    lastUpdatedAt: meta.lastUpdatedAt,
    isSystemColumn: meta.isSystemColumn,
  }
}

/**
 * 字段设置全尺寸页面：左栏同表字段导航 + 右栏选项卡编辑（5 分组 + 变更历史）。
 * 入口：2302 字段维护列表、工作台表头「字段设置」（?moduleId= 返回上下文）。
 */
export function FieldEditorRoute() {
  const { tableId = '', fieldId = '' } = useParams()
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const moduleId = searchParams.get('moduleId')
  const copyFrom = searchParams.get('copyFrom')
  const isNew = fieldId.toLowerCase() === 'new'
  const loadKey = isNew ? (copyFrom ?? undefined) : fieldId

  const tablesQuery = useQuery({
    queryKey: ['field-admin', 'tables'],
    queryFn: async () => apiClient.get<{ tableId: string; description: string }[]>('/admin/tables'),
    enabled: Boolean(tableId),
  })
  const tableDescription = tablesQuery.data?.find(item => item.tableId.toLowerCase() === tableId.toLowerCase())?.description

  const fieldsQuery = useQuery({
    queryKey: ['field-admin', 'fields', tableId],
    queryFn: async () => {
      const result = await apiClient.get<FieldPageResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields`, { query: { page: '1', pageSize: '500' } })
      return result.items
    },
    enabled: Boolean(tableId),
  })

  const { setBreadcrumb } = usePageBreadcrumb()
  useEffect(() => {
    setBreadcrumb({
      leads: [
        { label: '系统管理' },
        { label: '数据表维护', to: '/admin/tables' },
        { label: tableDescription || tableId, to: `/admin/tables/${encodeURIComponent(tableId)}/fields` },
      ],
      title: isNew ? (copyFrom ? `复制字段：${copyFrom}` : '新增字段') : `字段设置：${fieldId}`,
    })
  }, [setBreadcrumb, tableId, fieldId, isNew, copyFrom, tableDescription])

  const backTo = moduleId ? `/workbench/${moduleId}` : `/admin/tables/${encodeURIComponent(tableId)}/fields`
  const gotoField = (key: string) => {
    navigate(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(key)}${moduleId ? `?moduleId=${moduleId}` : ''}`)
  }
  const actionRef = useRef<{ save: () => void; saveAsync: () => Promise<void> } | null>(null)
  const [saveState, setSaveState] = useState({ canSave: false, saving: false, dirty: false })

  // 未保存改动登记给外壳：关闭标签、离开当前标签时的确认与保存由外壳统一处理
  useTabDirty(saveState.dirty, {
    save: () => actionRef.current?.saveAsync(),
    discard: () => {},
  })

  const endpoints: FieldEditorEndpoints = useMemo(() => ({
    load: async () => {
      if (isNew && !copyFrom) return null
      const key = copyFrom ?? fieldId
      const meta = await apiClient.get<FieldAdminMetadata>(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(key)}`)
      return adminMetaToFieldMeta(meta)
    },
    save: async (input: FieldInput, targetTable: string, targetFieldId: string, original: FieldInput | null) => {
      if (isNew) {
        await apiClient.post('/admin/fields', { tableId: targetTable, fieldId: targetFieldId, field: input })
      } else {
        await apiClient.put(`/admin/fields/${encodeURIComponent(targetTable)}/${encodeURIComponent(targetFieldId)}`, {
          tableId: targetTable,
          fieldId: targetFieldId,
          field: input,
          original,
        })
      }
    },
    tables: async () => (await apiClient.get<{ tableId: string; description: string }[]>('/admin/tables'))
      .map(item => ({ value: item.tableId, label: `${item.description} (${item.tableId})` }) as SetupLookup),
    modules: async () => (await apiClient.get<{ id: number; label: string }[]>('/admin/lookups/modules'))
      .map(item => ({ value: String(item.id), label: item.label }) as SetupLookup),
    validateExpression: async (kind: ExpressionKind, targetTable: string, targetFieldId: string, expression: string | null) =>
      (await apiClient.post('/admin/fields/expressions/validate', { kind, table: targetTable, field: targetFieldId, expression })) as ExpressionValidation,
    previewExpression: async (kind: ExpressionKind, targetTable: string, targetFieldId: string, expression: string | null) =>
      (await apiClient.post('/admin/fields/expressions/preview', { kind, table: targetTable, field: targetFieldId, expression })) as ExpressionPreview,
    publishExpression: async (kind: ExpressionKind, targetTable: string, targetFieldId: string, expression: string | null, original: string | null) => {
      await apiClient.post('/admin/fields/expressions/publish', { kind, table: targetTable, field: targetFieldId, expression, original })
    },
  }), [tableId, fieldId, copyFrom, isNew])

  return (
    <div className="erp-field-editor-page d-flex flex-column">
      <section className="card erp-list-card">
        <section className="erp-list-command-bar" aria-label="字段设置工具栏">
          <span className="fw-semibold small">
            {isNew ? (copyFrom ? `复制字段：${copyFrom}` : `新增字段（${tableDescription || tableId}）`) : `字段设置：${fieldId}`}
          </span>
          <div className="erp-list-actions d-flex gap-2 align-items-center">
            <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={() => navigate(backTo)}>返回</Button>
            <Button size="sm" variant="secondary" icon={<IconX size={16} />} onClick={() => navigate(backTo)}>取消</Button>
            <Button size="sm" variant="primary" icon={<IconDeviceFloppy size={16} />} loading={saveState.saving} disabled={!saveState.canSave} onClick={() => actionRef.current?.save()}>保存</Button>
          </div>
        </section>
          <div className="card-body p-0">
            <div className="row g-0">
              <div className="col-auto border-end d-flex flex-column erp-field-nav">
                <div className="erp-field-nav-header border-bottom d-flex justify-content-between align-items-center">
                  <strong className="small">字段列表（{fieldsQuery.data?.length ?? '…'}）</strong>
                  <Button size="sm" variant="primary" icon={<IconPlus size={16} />} onClick={() => gotoField('new')}>新增</Button>
                </div>
                <div className="flex-grow-1 overflow-auto">
                  {fieldsQuery.isPending && <LoadingState label="加载字段…" />}
                  {fieldsQuery.data?.map(item => (
                    <button
                      key={item.fieldId}
                      type="button"
                      className={`erp-field-nav-item d-block w-100 text-start py-1 border-0 ${item.fieldId.toLowerCase() === fieldId.toLowerCase() ? 'bg-primary-lt' : ''}`}
                      onClick={() => gotoField(item.fieldId)}
                    >
                      <div className="small fw-semibold font-monospace text-truncate">{item.fieldId}</div>
                      <div className="text-secondary small text-truncate">{item.description}</div>
                    </button>
                  ))}
                </div>
              </div>
              <div className="col">
                <div className="erp-field-editor-body">
                  <FieldEditorForm
                    mode={isNew ? 'new' : 'edit'}
                    tableId={tableId}
                    fieldKey={loadKey}
                    endpoints={endpoints}
                    onCancel={() => navigate(backTo)}
                    onSaved={() => navigate(backTo)}
                    historyTab={!isNew}
                    actionRef={actionRef}
                    onStateChange={setSaveState}
                  />
                </div>
              </div>
            </div>
          </div>
      </section>
    </div>
  )
}
