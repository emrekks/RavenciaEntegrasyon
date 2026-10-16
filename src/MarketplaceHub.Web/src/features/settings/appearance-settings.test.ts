import { describe, expect, it } from 'vitest'
import { defaultAppearanceSettings, defaultLightPalette, matchingColorThemeId, normalizeAppearanceSettings } from './appearance-settings'

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

describe('built-in colour themes', () => {
  it('offers Sade as the default and keeps Grafit as a choice', () => {
    const settings = normalizeAppearanceSettings(null)
    expect(settings.colorThemes.map(theme => theme.name)).toEqual(['Ravencia — Sade', 'Ravencia — Grafit'])
    expect(matchingColorThemeId(settings)).toBe('default-dark')
  })
  it('moves an account saved on the former default to Sade, but keeps a chosen Grafit', () => {
    const grafit = normalizeAppearanceSettings(null).colorThemes[1]
    const before = normalizeAppearanceSettings({ colors: { dark: { ...grafit.palette } }, colorThemes: [{ id: 'default-dark', name: 'Ravencia — Grafit', palette: grafit.palette, builtIn: true }] })
    expect(matchingColorThemeId(before)).toBe('default-dark')
    const chosen = normalizeAppearanceSettings({ colors: { dark: { ...grafit.palette } }, colorThemes: [{ ...grafit }] })
    expect(matchingColorThemeId(chosen)).toBe('ravencia-grafit')
  })

})
