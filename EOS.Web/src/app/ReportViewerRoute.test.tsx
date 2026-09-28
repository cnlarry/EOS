import { renderWithProviders } from '../test/renderWithProviders'
import { apiClientMock } from '../test/apiMock'
import { createMemoryRouter, RouterProvider, useLocation, useParams } from 'react-router-dom'
import { screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportViewerRoute } from './routeElements'

vi.mock('../services/api', async () => ({ apiClient: (await import('../test/apiMock')).apiClientMock }))

/** 落点探针：把"最后停在哪个报表身份地址"打出来，供断言。 */
function IdentityProbe() {
  const { reportId = '' } = useParams()
  const location = useLocation()
  return <div data-testid="landed">{`${reportId}|${location.search}`}</div>
}

function renderLegacyModuleRoute(entry: string) {
  const router = createMemoryRouter(
    [
      { path: '/reports/:moduleId', element: <ReportViewerRoute /> },
      { path: '/report/:reportId', element: <IdentityProbe /> },
    ],
    { initialEntries: [entry] },
  )
  return renderWithProviders(<RouterProvider router={router} />)
}

describe('ReportViewerRoute（老模块地址的跳转段）', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('没有最近使用记录时跳到模块的默认报表', async () => {
    apiClientMock.get.mockImplementation(async () => ({
      reports: [
        { reportId: 'RPT_B', reportName: '乙表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: false },
        { reportId: 'RPT_A', reportName: '甲表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: true },
      ],
      userSettings: null,
    }))

    renderLegacyModuleRoute('/reports/1405')

    expect(await screen.findByTestId('landed')).toHaveTextContent('RPT_A|')
  })

  it('有最近使用记录时认记录，且条件深链一并带过去', async () => {
    apiClientMock.get.mockImplementation(async () => ({
      reports: [
        { reportId: 'RPT_B', reportName: '乙表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: true },
        { reportId: 'RPT_A', reportName: '甲表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: false },
      ],
      userSettings: { reportId: 'RPT_A' },
    }))

    renderLegacyModuleRoute('/reports/1405?f1=W1')

    await waitFor(() => expect(screen.getByTestId('landed')).toHaveTextContent('RPT_A|?f1=W1'))
  })

  it('模块下一张报表都没有时给出说明，既不落到别的模块也不一直转圈', async () => {
    apiClientMock.get.mockImplementation(async () => ({ reports: [], userSettings: null }))

    renderLegacyModuleRoute('/reports/1405')

    expect(await screen.findByText('本模块没有报表')).toBeInTheDocument()
    expect(screen.queryByTestId('landed')).not.toBeInTheDocument()
  })
})
