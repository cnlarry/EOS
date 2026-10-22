import { IconArrowLeft, IconDeviceFloppy, IconPlus, IconX } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ErrorState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpSelectionList } from '../../components/common/ErpSelectionList'
import { Button } from '../../components/ui/Button'
import { usePageBreadcrumb } from '../../components/layout/PageBreadcrumbContext'
import { useTabDirty } from '../../components/layout/workspaceDirty'
import { apiClient } from '../../services/api'
import {
  FieldEditorForm,
  type ExpressionKind,
  type FieldEditorEndpoints,
  type FieldInput,
  type FieldMeta,
  type SetupLookup,
} from './FieldEditorForm'
import type { ExpressionRegistry, ExpressionStructure, TableColumn, TableRelations } from './expressionBuilder'

interface FieldAdminMetadata {
  tableId: string
  fieldId: string
  field: FieldInput
  isVirtual: boolean
  virtualExpression: string | null
  isAutoIncrement: boolean
  convertFunction: string | null
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
      // 标签名固定为页面名：表与字段由页内工具栏标题承担（同一字段在多标签页打开时靠地址区分）
      title: '字段设置',
    })
  }, [setBreadcrumb, tableId, tableDescription])

  const backTo = moduleId ? `/workbench/${moduleId}` : `/admin/tables/${encodeURIComponent(tableId)}/fields`
  const gotoField = (key: string) => {
    navigate(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(key)}${moduleId ? `?moduleId=${moduleId}` : ''}`)
  }
  // 字段键大小写不敏感：路由里可能是小写，清单里是库内原样的大小写。
  const activeFieldId = useMemo(() => {
    const wanted = fieldId.toLowerCase()
    return (fieldsQuery.data ?? []).find(item => item.fieldId.toLowerCase() === wanted)?.fieldId ?? fieldId
  }, [fieldId, fieldsQuery.data])
  const actionRef = useRef<{ save: () => void; saveAsync: () => Promise<void> } | null>(null)
  const [saveState, setSaveState] = useState({ canSave: false, saving: false, dirty: false })
  const [fieldKeyword, setFieldKeyword] = useState('')

  // 左栏只导同表字段：按字段名与中文名过滤，切换字段时不重置关键字（连续找同前缀字段更顺）。
  const listedFields = useMemo(() => {
    const needle = fieldKeyword.trim().toLowerCase()
    const items = fieldsQuery.data ?? []
    if (needle === '') return items
    return items.filter(item => `${item.fieldId} ${item.description}`.toLowerCase().includes(needle))
  }, [fieldsQuery.data, fieldKeyword])

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
    expressionRegistry: async () => apiClient.get<ExpressionRegistry>('/admin/fields/expressions/registry'),
    parseExpression: async (kind: ExpressionKind, expression: string | null) =>
      (await apiClient.post('/admin/fields/expressions/parse', { kind, expression })) as ExpressionStructure,
    tableRelations: async (targetTable: string) =>
      apiClient.get<TableRelations>(`/admin/tables/${encodeURIComponent(targetTable)}/relations`),
    tableColumns: async (targetTable: string) =>
      apiClient.get<TableColumn[]>(`/admin/tables/${encodeURIComponent(targetTable)}/columns`),
  }), [tableId, fieldId, copyFrom, isNew])

  // 页内标题：人话在前（表描述 · 字段描述），ID 在后（等宽次色）。
  // 描述缺失时回落到 ID——两种信息都不丢，读的人不必去猜某个英文标识是什么。
  const tableLabel = (tableDescription ?? '').trim() || tableId
  const fieldDescriptionOf = (id: string | null) => {
    if (!id) return ''
    return (fieldsQuery.data ?? [])
      .find(item => item.fieldId.toLowerCase() === id.toLowerCase())
      ?.description?.trim() ?? ''
  }
  const currentFieldLabel = fieldDescriptionOf(fieldId) || fieldId
  const copySourceLabel = copyFrom ? (fieldDescriptionOf(copyFrom) || copyFrom) : ''

  return (
    <div className="erp-base-page erp-field-editor-page">
      <ErpListCard
        ariaLabel="字段设置工具栏"
        search={(
          <div className="erp-field-editor-context">
            <span className="erp-field-editor-context-title">
              {isNew
                ? (copyFrom ? `复制字段 · ${copySourceLabel} · ${tableLabel}` : `新增字段 · ${tableLabel}`)
                : `${tableLabel} · ${currentFieldLabel}`}
            </span>
            <span className="erp-field-editor-context-code">
              {isNew ? (copyFrom ? `${copyFrom} → ${tableId}` : tableId) : `${tableId}.${fieldId}`}
            </span>
          </div>
        )}
        actions={(
          <>
            <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={() => navigate(backTo)}>返回</Button>
            <Button size="sm" variant="secondary" icon={<IconPlus size={16} />} onClick={() => gotoField('new')}>新增</Button>
            <Button size="sm" variant="secondary" icon={<IconX size={16} />} onClick={() => navigate(backTo)}>取消</Button>
            <Button size="sm" variant="primary" icon={<IconDeviceFloppy size={16} />} loading={saveState.saving} disabled={!saveState.canSave} onClick={() => actionRef.current?.save()}>保存</Button>
          </>
        )}
      >
          <div className="card-body p-0">
            <div className="row g-0">
              <ErpSelectionList
                ariaLabel="字段列表"
                className="erp-field-nav"
                header={(
                  <ErpSearchBox
                    value={fieldKeyword}
                    onChange={setFieldKeyword}
                    placeholder="搜索字段"
                    ariaLabel="搜索字段"
                    shortcut={false}
                  />
                )}
                loading={fieldsQuery.isPending}
                loadingLabel="加载字段…"
                error={fieldsQuery.isError
                  ? <div className="p-2"><ErrorState message="加载字段失败。" onRetry={() => void fieldsQuery.refetch()} /></div>
                  : null}
                empty={(
                  <div className="erp-selection-list-empty">
                    {fieldKeyword.trim() === '' ? '这张表还没有字段。' : '没有匹配的字段。'}
                  </div>
                )}
                items={listedFields.map(item => ({
                  id: item.fieldId,
                  label: item.fieldId,
                  monospace: true,
                  description: item.description,
                }))}
                activeId={activeFieldId}
                onSelect={gotoField}
              />
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
      </ErpListCard>
    </div>
  )
}
