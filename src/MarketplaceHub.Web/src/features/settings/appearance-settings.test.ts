import { describe, expect, it } from 'vitest'
import { defaultAppearanceSettings, defaultLightPalette, matchingColorThemeId, normalizeAppearanceSettings, readRememberedAppearance, rememberAppearance } from './appearance-settings'
import { setStorageScope } from '../../shared/storage-scope'

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

describe('readable low-priority text', () => {
  it('moves a palette that kept a former default "Soluk Metin" colour to the readable one, and keeps a picked one', () => {
    const saved = { ...defaultAppearanceSettings, colors: { dark: { ...defaultAppearanceSettings.colors.dark, subtle: '#6f6f7c' }, light: { ...defaultLightPalette, subtle: '#69758d' } } }
    const normalized = normalizeAppearanceSettings(saved)
    expect(normalized.colors.dark.subtle).toBe('#8a8a99')
    expect(normalized.colors.light.subtle).toBe('#59657d')
    const picked = normalizeAppearanceSettings({ ...saved, colors: { ...saved.colors, dark: { ...saved.colors.dark, subtle: '#9999aa' } } })
    expect(picked.colors.dark.subtle).toBe('#9999aa')
  })
})

describe('remembered appearance', () => {
  function memoryStorage() {
    const values = new Map<string, string>()
    return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) }, keys: () => [...values.keys()] }
  }

  it('reads back the settings this browser last read, for the same tenant and user only', () => {
    const storage = memoryStorage()
    setStorageScope('tenant-a', 'user-1')
    rememberAppearance({ settings: { ...defaultAppearanceSettings, fontSize: 'lg' }, version: 4 }, storage)
    expect(readRememberedAppearance(storage)).toEqual({ settings: { ...defaultAppearanceSettings, fontSize: 'lg' }, version: 4 })
    setStorageScope('tenant-a', 'user-2')
    expect(readRememberedAppearance(storage)).toBeUndefined()
    setStorageScope(null, null)
  })

  it('ignores a damaged copy', () => {
    setStorageScope('tenant-a', 'user-1')
    expect(readRememberedAppearance({ getItem: () => '{not json' })).toBeUndefined()
    expect(readRememberedAppearance({ getItem: () => 'null' })).toBeUndefined()
    setStorageScope(null, null)
  })
})
