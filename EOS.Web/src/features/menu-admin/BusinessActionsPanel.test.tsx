import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { BusinessActionsPanel } from './BusinessActionsPanel'
import type { MenuAdminModule } from './MenuAdminPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleWithTables = (id: number, desc: string): MenuAdminModule => ({
  M_IDX: id,
  M_ALIAS: null,
  M_DESC: desc,
  M_URL: null,
  NEW_URL: null,
  MODI_URL: null,
  HELP_URL: null,
  DETAIL_NO_FIELDS: null,
  DETAIL_NO_SAVE: false,
  SEARCH_1: false,
  SEARCH_2: false,
  M_P_IDX: null,
  SORT_IDX: 0,
  M_TAG: true,
  AUTO_APPROVE: false,
  IF_COPY: false,
  ERROR_NO_SAVE: false,
  SORT_FIELDS: null,
  MASTER_TABLE: 'PUR_RECEIVE_M',
  FILTER: null,
  DETAIL_TABLE: 'PUR_RECEIVE_D',
  UPDATE_SP: null,
  AFTERSAVE_SP: null,
  NOT_BACK_FIELDS_M: null,
  NOT_BACK_FIELDS: null,
  GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
  GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
  GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
  GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
  GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
  FORM_TABS: null,
  FORM_COLUMNS: null,
  FORM_BUTTONS: null,
  LAST_UPDATE_BY: null,
  LAST_UPDATE_DATE: null,
  M_ICON: null,
  Icon: null,
  EFFECT_ENGINE_TAG: false,
})

const catalog = {
  events: ['SAVE', 'APPROVE_EFFECT', 'DEAPPROVE', 'ENDCASE', 'UNENDCASE'],
  failModes: ['BLOCK', 'WARN'],
  effectKeys: ['field-accumulate', 'inventory-move'],
  opCodes: ['ACCUM', 'ASSIGN'],
  sourceScopes: ['MASTER', 'DETAIL', 'TABLE', 'CONSTANT'],
  sourceAggregates: ['SUM', 'MAX', 'MIN', 'DISTINCT'],
  validationStages: ['SAVE', 'APPROVE', 'DEAPPROVE'],
  validationKeys: ['qty-not-exceed', 'reference-exists', 'duplicate-check'],
}

const config = {
  moduleId: 1607,
  actions: [
    {
      seq: 1,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'field-accumulate',
      effectName: '收料量回写采购单',
      enabled: true,
      failMode: 'BLOCK',
      ops: [
        {
          opSeq: 1,
          targetTable: 'PUR_PURCHASE_D',
          targetField: 'RECEIVE_QTY',
          opCode: 'ACCUM',
          sourceScope: 'DETAIL',
          sourceField: 'QTY',
          sourceAgg: 'SUM',
        },
      ],
    },
  ],
  validationRules: [],
}

describe('BusinessActionsPanel', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return catalog
      if (url.endsWith('/module-business-config/1607')) return config
      if (url.endsWith('/module-business-config/schemas')) {
        return {
          effects: catalog.effectKeys.map((effectKey) => ({ effectKey, rootKeys: ['direction', 'fieldMap', 'mrp'] })),
          reverseKinds: ['auto-reverse', 'no-reverse', 'recompute', 'reverse-flow', 'snapshot'],
        }
      }
      return {}
    })
    apiClientMock.put.mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('加载并展示业务动作与公式行', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)

    expect(await screen.findByText('业务动作（1）')).toBeInTheDocument()
    expect(screen.getByText('批核生效（APPROVE_EFFECT）')).toBeInTheDocument()
    fireEvent.click(screen.getByText('收料量回写采购单'))
    expect(await screen.findByText('选中动作的公式行（1）')).toBeInTheDocument()
    expect(screen.getByText('PUR_PURCHASE_D')).toBeInTheDocument()
    expect(screen.getByText('RECEIVE_QTY')).toBeInTheDocument()
  })

  it('新增动作打开编辑弹窗并可取消', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getAllByRole('button', { name: /新增/ })[0])
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('新增业务动作')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('保存配置调用 PUT 并提示成功', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByRole('button', { name: /保存配置/ }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/module-business-config/1607',
      expect.objectContaining({ actions: expect.any(Array), validationRules: expect.any(Array) }),
    ))
    expect(await screen.findByText('配置已保存并重新加载。')).toBeInTheDocument()
  })

  it('发布配置先保存当前内容再发布并提示版本', async () => {
    apiClientMock.post.mockResolvedValue([
      {
        moduleId: 1607,
        title: '收料单',
        published: true,
        version: 5,
        definitionVersion: 'module-1607-v5',
        passed: true,
        checks: [],
      },
    ])
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByRole('button', { name: /发布配置/ }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/module-business-config/1607',
      expect.objectContaining({ actions: expect.any(Array), validationRules: expect.any(Array) }),
    ))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/module-business-config/1607/publish',
    ))
    expect(await screen.findByText(/发布成功：module-1607-v5/)).toBeInTheDocument()
  })

  it('发布校验失败时展示失败原因且不提示成功', async () => {
    apiClientMock.post.mockResolvedValue([
      {
        moduleId: 1607,
        title: '收料单',
        published: false,
        version: null,
        definitionVersion: null,
        passed: false,
        checks: [{ code: 'chooser_sources', passed: false, message: '选择器来源配置非法', severity: 'error' }],
      },
    ])
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByRole('button', { name: /发布配置/ }))

    expect(await screen.findByText(/发布未通过校验，未写入新快照/)).toBeInTheDocument()
    expect(screen.getByText('选择器来源配置非法')).toBeInTheDocument()
    expect(screen.queryByText(/发布成功/)).not.toBeInTheDocument()
  })
})
