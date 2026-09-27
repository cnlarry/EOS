import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { FallbackModulePage } from './FallbackModulePage'

describe('FallbackModulePage', () => {
  it('渲染模块编号与迁移提示', () => {
    render(
      <MemoryRouter initialEntries={['/fallback/modules/1209']}>
        <Routes>
          <Route path="/fallback/modules/:moduleId" element={<FallbackModulePage />} />
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByText('模块正在迁移')).toBeInTheDocument()
    expect(screen.getByText(/模块 1209/)).toBeInTheDocument()
  })
})
