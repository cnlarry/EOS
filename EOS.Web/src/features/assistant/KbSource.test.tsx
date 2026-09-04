import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { KbDocDialog } from './KbSource'
import { parseKbLinks } from './kbSources'

describe('parseKbLinks', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('无引用时原样返回纯文本', () => {
    expect(parseKbLinks('你好')).toEqual(['你好'])
  })

  it('切出 kb 来源引用并保留前后文本', () => {
    const parts = parseKbLinks('结论。source: kb://doc/9#c2 完毕')
    expect(parts).toEqual(['结论。source: ', { docId: '9', chunk: 2 }, ' 完毕'])
  })

  it('来源对话框加载原文，不可见时显示不存在', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({
        docId: '9', title: '业务规则', sourceUri: 'docs/07',
        chunks: [{ serialNo: 2, content: '送审后不得重复送审。' }],
      }))
    vi.stubGlobal('fetch', fetchMock)

    render(<KbDocDialog docId="9" onClose={() => undefined} />)
    await waitFor(() => expect(screen.getByText(/送审后不得重复送审/)).toBeInTheDocument())
    expect(screen.getByText(/出处：docs\/07/)).toBeInTheDocument()
  })

  it('来源对话框关闭按钮回调调用方', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({
      docId: '9', title: '业务规则', sourceUri: null, chunks: [],
    })))
    const onClose = vi.fn()
    render(<KbDocDialog docId="9" onClose={onClose} />)
    await waitFor(() => expect(screen.getByRole('dialog')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '关闭' }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
