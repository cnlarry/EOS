import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ConfigDiffCard } from './ConfigDiffCard'
import type { AssistantConfigDiff } from './types'

const previewConfigChange = vi.fn()
const applyConfigChange = vi.fn()

vi.mock('./api', () => ({
  previewConfigChange: (...args: unknown[]) => previewConfigChange(...args),
  applyConfigChange: (...args: unknown[]) => applyConfigChange(...args),
}))

/** 一字段项 + 一不可预演的效果项：两行都要看得见，且预演覆盖情况逐项标出。 */
function diff(overrides: Partial<AssistantConfigDiff> = {}): AssistantConfigDiff {
  return {
    kind: 'config-diff',
    surface: 'fields',
    sourceLabel: 'ZZ_SRC',
    targetLabel: 'ZZ_DST',
    blocked: false,
    blockedCode: null,
    blockedMessage: null,
    items: [
      {
        id: 'fields:ZZ_DST.F_DESC',
        surface: 'fields',
        target: 'ZZ_DST.F_DESC',
        label: 'F_DESC（显示名）',
        changes: [{ field: 'F_DESC', label: '显示名', oldValue: '旧名', newValue: '新名' }],
        impacts: ['字段元数据：列表列与表单呈现、必填与正则校验；不经过效果链。'],
        previewable: false,
        previewNote: '无法预演：字段元数据改动影响的是列表与表单的呈现和校验，不经过效果链，没有可预演的路径。',
        previewSummary: null,
      },
      {
        id: 'effect:APPROVE_EFFECT@set-state#1',
        surface: 'effect',
        target: 'APPROVE_EFFECT@set-state',
        label: '状态置位（批核生效）',
        changes: [{ field: 'ENABLED', label: '启用', oldValue: '否', newValue: '是' }],
        impacts: ['MODULES.M_DESC [SET]'],
        previewable: false,
        previewNote: '可预演：需要一张真实单据的主键才能跑预演；未提供单据时按无法预演处理。',
        previewSummary: null,
      },
    ],
    notes: ['克隆只搬运显示与校验属性。'],
    request: { surface: 'fields', source_table: 'ZZ_SRC', target_table: 'ZZ_DST' },
    ...overrides,
  }
}

describe('ConfigDiffCard', () => {
  beforeEach(() => {
    previewConfigChange.mockReset()
    applyConfigChange.mockReset()
  })

  it('逐项列出旧值与新值', () => {
    render(<ConfigDiffCard draft={diff()} />)

    expect(screen.getByText('旧名')).toBeTruthy()
    expect(screen.getByText('新名')).toBeTruthy()
    expect(screen.getByText('否')).toBeTruthy()
    expect(screen.getByText(/影响面：字段元数据/)).toBeTruthy()
  })

  it('不可预演的改动单独标注且汇总如实计数', () => {
    render(<ConfigDiffCard draft={diff()} />)

    // 每一项的预演覆盖情况都在卡上（不可预演的显式写出"无法预演"）。
    expect(screen.getByText(/无法预演：字段元数据改动/)).toBeTruthy()
    // 没有提供单据时，可预演的那类也必须说清"这次没预演"。
    expect(screen.getByText(/需要一张真实单据的主键/)).toBeTruthy()
    expect(screen.getAllByText(/2 项改动中有 2 项无法预演/).length).toBeGreaterThan(0)
    expect(screen.getByText(/不能当成已校验/)).toBeTruthy()
  })

  it('可逐项取消勾选，应用只提交被勾选的项', async () => {
    applyConfigChange.mockResolvedValue({
      kind: 'config-apply-result',
      surface: 'fields',
      targetLabel: 'ZZ_DST',
      blockedCode: null,
      blockedMessage: null,
      items: [{ id: 'fields:ZZ_DST.F_DESC', target: 'ZZ_DST.F_DESC', applied: true, code: null, message: null }],
      notes: [],
    })
    render(<ConfigDiffCard draft={diff()} />)

    expect(screen.getByText('应用所选 2 项')).toBeTruthy()
    fireEvent.click(screen.getByLabelText('应用 状态置位（批核生效）'))
    expect(screen.getByText('应用所选 1 项')).toBeTruthy()

    fireEvent.click(screen.getByText('应用所选 1 项'))

    await waitFor(() => expect(applyConfigChange).toHaveBeenCalledTimes(1))
    const [request, itemIds] = applyConfigChange.mock.calls[0] as [unknown, string[], string]
    expect(request).toEqual({ surface: 'fields', source_table: 'ZZ_SRC', target_table: 'ZZ_DST' })
    expect(itemIds).toEqual(['fields:ZZ_DST.F_DESC'])
    // 勾选项是唯一的选择依据：值本身不进请求（写入值由服务端重新规划）。
    expect((request as Record<string, unknown>).items).toBeUndefined()
    await waitFor(() => expect(screen.getByText('已写入')).toBeTruthy())
  })

  it('重新检查会把服务端最新结论换到卡上', async () => {
    previewConfigChange.mockResolvedValue(diff({
      items: [{
        id: 'fields:ZZ_DST.F_DESC',
        surface: 'fields',
        target: 'ZZ_DST.F_DESC',
        label: 'F_DESC（显示名）',
        changes: [],
        impacts: [],
        previewable: false,
        previewNote: '无法预演：字段元数据改动影响的是列表与表单的呈现和校验，不经过效果链，没有可预演的路径。',
        previewSummary: null,
      }],
      notes: ['重新检查完成：结论已刷新。'],
    }))
    render(<ConfigDiffCard draft={diff()} />)

    fireEvent.click(screen.getByText('重新检查（预演 / 自检）'))

    await waitFor(() => expect(previewConfigChange).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(screen.getByText(/本次 1 项改动中有 1 项无法预演/)).toBeTruthy())
    expect(screen.getByText('重新检查完成：结论已刷新。')).toBeTruthy()
  })
})
