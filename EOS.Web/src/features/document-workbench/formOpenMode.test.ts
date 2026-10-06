import { describe, expect, it } from 'vitest'
import {
  DEFAULT_DIALOG_HEIGHT,
  DEFAULT_DIALOG_WIDTH,
  normalizeFormOpenMode,
  resolveDialogSize,
} from './formOpenMode'

describe('formOpenMode', () => {
  it('未配置或无法识别的取值一律回落本页签', () => {
    expect(normalizeFormOpenMode(null)).toBe('TAB')
    expect(normalizeFormOpenMode(undefined)).toBe('TAB')
    expect(normalizeFormOpenMode('')).toBe('TAB')
    expect(normalizeFormOpenMode('POPUP')).toBe('TAB')
  })

  it('识别新页签与弹窗（大小写与首尾空白不敏感）', () => {
    expect(normalizeFormOpenMode('TAB')).toBe('TAB')
    expect(normalizeFormOpenMode(' newtab ')).toBe('NEWTAB')
    expect(normalizeFormOpenMode('dialog')).toBe('DIALOG')
  })

  it('弹窗尺寸：配了就用配的，未配置或非正数回落默认', () => {
    expect(resolveDialogSize(900, 640)).toEqual({ width: 900, height: 640 })
    expect(resolveDialogSize(null, null)).toEqual({ width: DEFAULT_DIALOG_WIDTH, height: DEFAULT_DIALOG_HEIGHT })
    expect(resolveDialogSize(0, -10)).toEqual({ width: DEFAULT_DIALOG_WIDTH, height: DEFAULT_DIALOG_HEIGHT })
  })
})
