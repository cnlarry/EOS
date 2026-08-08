import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { LegacyModulePage } from './LegacyModulePage'

describe('LegacyModulePage', () => {
  it('渲染模块编号与迁移提示', () => {
    render(
      <MemoryRouter initialEntries={['/legacy/modules/1209']}>
        <Routes>
          <Route path="/legacy/modules/:moduleId" element={<LegacyModulePage />} />
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByText('模块正在迁移')).toBeInTheDocument()
    expect(screen.getByText(/模块 1209/)).toBeInTheDocument()
  })
})
