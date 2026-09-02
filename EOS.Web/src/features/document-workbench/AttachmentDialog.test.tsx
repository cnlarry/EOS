import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AttachmentDialog } from './AttachmentDialog'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const items = [
  {
    id: 1, moduleId: 1209, masterTable: 'PRODUCT_EDITION', keyValues: '["P1","A"]', serialNo: 1,
    fileName: '1.pdf', clientFileName: '客户订单.pdf', contentType: 'application/pdf', sizeBytes: 2048,
    sha256: 'abc', remark: '原始订单', uploadedBy: 'u1', uploadedByDisplay: '张三', uploadedAt: '2026-08-01T00:00:00Z',
  },
  {
    id: 2, moduleId: 1209, masterTable: 'PRODUCT_EDITION', keyValues: '["P1","A"]', serialNo: 2,
    fileName: '2.xlsx', clientFileName: '合同.xlsx', contentType: 'application/vnd.ms-excel', sizeBytes: 4096,
    sha256: 'def', remark: null, uploadedBy: 'u2', uploadedByDisplay: null, uploadedAt: '2026-08-02T00:00:00Z',
  },
]

function renderDialog(overrides: Partial<Parameters<typeof AttachmentDialog>[0]> = {}) {
  return renderWithProviders(
      <AttachmentDialog
        moduleId={1209}
        masterTable="PRODUCT_EDITION"
        recordKey={['P1', 'A']}
        title="产品版次"
        canUpload
        canEdit
        canDelete
        onClose={vi.fn()}
        {...overrides}
      />
)
}

describe('AttachmentDialog', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(items)
    apiClientMock.put.mockResolvedValue({ ...items[0], remark: '新备注' })
    apiClientMock.delete.mockResolvedValue(items[0])
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出附件元数据（文件名/大小/上传人/时间/备注）', async () => {
    renderDialog()
    expect(await screen.findByText('客户订单.pdf')).toBeInTheDocument()
    expect(screen.getByText('合同.xlsx')).toBeInTheDocument()
    expect(screen.getByText('2.0 KB')).toBeInTheDocument()
    expect(screen.getByText(/张三/)).toBeInTheDocument()
    expect(screen.getByText('原始订单')).toBeInTheDocument()
  })

  it('无附件时显示空状态', async () => {
    apiClientMock.get.mockResolvedValue([])
    renderDialog()
    expect(await screen.findByText('暂无附件。')).toBeInTheDocument()
  })

  it('有上传权限时显示上传入口', async () => {
    renderDialog()
    expect(await screen.findByLabelText('上传文件')).toBeInTheDocument()
  })

  it('无上传权限时隐藏上传入口', async () => {
    apiClientMock.get.mockResolvedValue(items)
    renderDialog({ canUpload: false })
    expect(await screen.findByText('客户订单.pdf')).toBeInTheDocument()
    expect(screen.queryByLabelText('上传文件')).not.toBeInTheDocument()
  })

  it('改备注失焦后提交 remark', async () => {
    renderDialog()
    const remarkInput = (await screen.findAllByPlaceholderText('备注'))[0] as HTMLInputElement
    fireEvent.blur(remarkInput, { target: { value: '新备注' } })
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/document-workbench/1209/attachments/1/remark',
      { remark: '新备注' },
    ))
  })

  it('无编辑权限时不显示备注输入', async () => {
    renderDialog({ canEdit: false })
    await screen.findByText('客户订单.pdf')
    expect(screen.queryAllByPlaceholderText('备注')).toHaveLength(0)
  })
})