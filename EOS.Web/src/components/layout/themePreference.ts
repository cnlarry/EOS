import { createContext, useContext } from 'react'

/** 主题偏好：跟随系统，或显式锁定浅色 / 深色。 */
export type ThemePreference = 'system' | 'light' | 'dark'

/** 实际生效的主题：`system` 已按系统偏好解析过。 */
export type ResolvedTheme = 'light' | 'dark'

export interface ThemePreferenceValue {
  preference: ThemePreference
  /** 当前真正生效的主题，即写进 `data-bs-theme` 的那个值。 */
  theme: ResolvedTheme
  setPreference: (next: ThemePreference) => void
}

/**
 * 主题偏好上下文：深浅模式是外壳级状态——侧栏底部的切换按钮与个人设置页改的是同一件事，
 * 两边都必须立即生效。由 AppShell 提供、页面消费；不在各页各读一次 localStorage，
 * 否则同一份偏好在两处各留一个副本，改了一边另一边不变。
 */
export const ThemePreferenceContext = createContext<ThemePreferenceValue | null>(null)

/** 读主题偏好与其设值入口；不在 AppShell 内使用即报错（主题状态只有一处真源）。 */
export function useThemePreference(): ThemePreferenceValue {
  const value = useContext(ThemePreferenceContext)
  if (!value) throw new Error('useThemePreference 必须在 AppShell 内使用：主题偏好由外壳统一提供。')
  return value
}
