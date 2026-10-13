import { describe, expect, it } from 'vitest'
import { defaultAppearanceSettings, defaultLightPalette, normalizeAppearanceSettings } from './appearance-settings'

describe('light palette migration', () => {
  it('moves an account that saved an earlier default to the current one', () => {
    const saved = { ...defaultAppearanceSettings, colors: { dark: defaultAppearanceSettings.colors.dark, light: { ...defaultLightPalette, bg: '#d4d9e2', surface: '#e5e8ee', sidebar: '#dde1e8', ink: '#1b2232' } } }
    expect(normalizeAppearanceSettings(saved).colors.light).toEqual(defaultLightPalette)
  })

  it('keeps a palette the user edited', () => {
    const custom = { ...defaultLightPalette, bg: '#e0e0e0', surface: '#f2f2f2' }
    const saved = { ...defaultAppearanceSettings, colors: { dark: defaultAppearanceSettings.colors.dark, light: custom } }
    expect(normalizeAppearanceSettings(saved).colors.light).toEqual(custom)
  })
})
