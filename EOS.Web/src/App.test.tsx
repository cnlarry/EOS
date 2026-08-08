import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import App from './App'

describe('App scaffold', () => {
  it('渲染脚手架并响应计数', () => {
    render(<App />)
    expect(screen.getByText('Get started')).toBeInTheDocument()
    const button = screen.getByRole('button', { name: 'Count is 0' })
    fireEvent.click(button)
    expect(screen.getByRole('button', { name: 'Count is 1' })).toBeInTheDocument()
  })
})
