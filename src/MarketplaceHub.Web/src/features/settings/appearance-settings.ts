import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'

export type AppearanceFontFamily = 'inter' | 'system' | 'segoe' | 'arial'
export type AppearanceFontSize = 'small' | 'normal' | 'large' | 'extra-large'
export type AppearanceThemeMode = 'dark'
export type AppearanceColorTheme = 'dark'
export type AppearanceColorToken = 'bg' | 'surface' | 'surfaceRaised' | 'surfaceSoft' | 'border' | 'borderStrong' | 'ink' | 'muted' | 'subtle' | 'primary' | 'primaryHover' | 'primarySoft' | 'accent' | 'accentSoft' | 'warning' | 'warningSoft' | 'danger' | 'dangerSoft' | 'info'

export type AppearancePalette = Record<AppearanceColorToken, string>

export type AppearanceColors = Record<AppearanceColorTheme, AppearancePalette>

export type AppearanceColorThemeProfile = {
  id: string
  name: string
  palette: AppearancePalette
  builtIn: boolean
}

export type AppearanceSettings = {
  fontFamily: AppearanceFontFamily
  fontSize: AppearanceFontSize
  themeMode: AppearanceThemeMode
  colors: { dark: AppearancePalette }
  colorThemes: AppearanceColorThemeProfile[]
}

export type AppearanceSettingsEnvelope = {
  settings: AppearanceSettings | null
  version: number
}

const defaultDarkPalette: AppearancePalette = {
  bg: '#101014', surface: '#18181f', surfaceRaised: '#202028', surfaceSoft: '#272730', border: '#303039', borderStrong: '#41414d', ink: '#f4f4f7', muted: '#aaaab9', subtle: '#858595', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#302443', accent: '#4adea0', accentSoft: '#1c332c', warning: '#f6c16b', warningSoft: '#382f22', danger: '#fb858b', dangerSoft: '#3c252b', info: '#83b4ff'
}

export const defaultAppearanceColorTheme: AppearanceColorThemeProfile = {
  id: 'default-dark',
  name: 'Ravencia — Grafit',
  palette: { ...defaultDarkPalette },
  builtIn: true
}

export const defaultAppearanceSettings: AppearanceSettings = {
  fontFamily: 'inter',
  fontSize: 'normal',
  themeMode: 'dark',
  colors: {
    dark: { ...defaultDarkPalette }
  },
  colorThemes: [{ ...defaultAppearanceColorTheme, palette: { ...defaultAppearanceColorTheme.palette } }]
}

export const appearanceColorTokenOptions: Array<{ key: AppearanceColorToken; label: string; description: string }> = [
  { key: 'bg', label: 'Arka plan', description: 'Sayfanın ana zemini' },
  { key: 'surface', label: 'Yüzey', description: 'Kart ve panel zemini' },
  { key: 'surfaceRaised', label: 'Yükseltilmiş yüzey', description: 'Alan ve kontrol zemini' },
  { key: 'surfaceSoft', label: 'Yumuşak yüzey', description: 'İkincil yüzey ve rozet zemini' },
  { key: 'border', label: 'Kenarlık', description: 'Standart ayırıcı çizgiler' },
  { key: 'borderStrong', label: 'Güçlü kenarlık', description: 'Input ve belirgin çerçeveler' },
  { key: 'ink', label: 'Ana metin', description: 'Başlık ve önemli metinler' },
  { key: 'muted', label: 'İkincil metin', description: 'Açıklama ve yardımcı metinler' },
  { key: 'subtle', label: 'Soluk metin', description: 'Düşük öncelikli metinler' },
  { key: 'primary', label: 'Ana renk', description: 'Birincil buton ve aktif durum' },
  { key: 'primaryHover', label: 'Ana renk hover', description: 'Birincil etkileşim hover durumu' },
  { key: 'primarySoft', label: 'Ana renk yumuşak', description: 'Seçili ve hafif vurgulu alanlar' },
  { key: 'accent', label: 'Accent', description: 'Başarı ve ikincil vurgu' },
  { key: 'accentSoft', label: 'Accent yumuşak', description: 'Başarı arka planları' },
  { key: 'warning', label: 'Uyarı', description: 'Uyarı ve dikkat durumları' },
  { key: 'warningSoft', label: 'Uyarı yumuşak', description: 'Uyarı arka planları' },
  { key: 'danger', label: 'Hata', description: 'Hata ve tehlikeli işlemler' },
  { key: 'dangerSoft', label: 'Hata yumuşak', description: 'Hata arka planları' },
  { key: 'info', label: 'Bilgi', description: 'Bilgilendirme vurgusu' }
]

export function appearanceColorCssVariable(token: AppearanceColorToken) {
  return `--rv-color-${token.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`
}

export const appearanceFontFamilyOptions: Array<{ value: AppearanceFontFamily; label: string }> = [
  { value: 'inter', label: 'Inter' },
  { value: 'system', label: 'Sistem yazı tipi' },
  { value: 'segoe', label: 'Segoe UI' },
  { value: 'arial', label: 'Arial' }
]

export const appearanceFontSizeOptions: Array<{ value: AppearanceFontSize; label: string; description: string }> = [
  { value: 'small', label: 'Küçük', description: 'Daha fazla içerik için daha sıkı görünüm' },
  { value: 'normal', label: 'Normal', description: 'Önerilen, okunabilirliği artırılmış görünüm' },
  { value: 'large', label: 'Büyük', description: 'Yoğun tablolarda daha rahat okuma' },
  { value: 'extra-large', label: 'Çok büyük', description: 'En yüksek okunabilirlik' }
]

export const appearanceThemeModeOptions: Array<{ value: AppearanceThemeMode; label: string; description: string }> = [
  { value: 'dark', label: 'Ravencia — Grafit', description: 'Çalışma alanının varsayılan koyu teması' }
]

export const appearanceFontFamilyCss: Record<AppearanceFontFamily, string> = {
  inter: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  system: 'ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  segoe: '"Segoe UI", system-ui, sans-serif',
  arial: 'Arial, Helvetica, sans-serif'
}

export const appearanceFontScale: Record<AppearanceFontSize, number> = {
  small: 0.93,
  normal: 1,
  large: 1.1,
  'extra-large': 1.2
}

const fontFamilies = new Set<AppearanceFontFamily>(appearanceFontFamilyOptions.map(option => option.value))
const fontSizes = new Set<AppearanceFontSize>(appearanceFontSizeOptions.map(option => option.value))
const themeModes = new Set<AppearanceThemeMode>(appearanceThemeModeOptions.map(option => option.value))
const colorTokens: AppearanceColorToken[] = appearanceColorTokenOptions.map(option => option.key)
const hexColor = /^#[0-9a-f]{6}$/i

function normalizePalette(value: unknown, fallback: AppearancePalette): AppearancePalette {
  if (!value || typeof value !== 'object') return { ...fallback }
  const candidate = value as Partial<AppearancePalette>
  return Object.fromEntries(colorTokens.map(token => [token, typeof candidate[token] === 'string' && hexColor.test(candidate[token]) ? candidate[token] : fallback[token]])) as AppearancePalette
}

function normalizeColors(value: unknown): AppearanceColors {
  if (!value || typeof value !== 'object') return { dark: { ...defaultDarkPalette } }
  const candidate = value as Partial<AppearanceColors>
  const dark = normalizePalette(candidate.dark, defaultDarkPalette)
  return { dark }
}

function normalizeColorThemes(value: unknown): AppearanceColorThemeProfile[] {
  const profiles: AppearanceColorThemeProfile[] = [{ ...defaultAppearanceColorTheme, palette: { ...defaultAppearanceColorTheme.palette } }]
  if (!Array.isArray(value)) return profiles
  const ids = new Set(profiles.map(profile => profile.id.toLowerCase()))
  for (const item of value) {
    if (!item || typeof item !== 'object') continue
    const candidate = item as Partial<AppearanceColorThemeProfile>
    const id = typeof candidate.id === 'string' ? candidate.id.trim().slice(0, 80) : ''
    const name = typeof candidate.name === 'string' ? candidate.name.trim().slice(0, 60) : ''
    if (!id || !name || ids.has(id.toLowerCase()) || !candidate.palette) continue
    const palette = normalizePalette(candidate.palette, defaultDarkPalette)
    profiles.push({ id, name, palette, builtIn: false })
    ids.add(id.toLowerCase())
    if (profiles.length >= 24) break
  }
  return profiles
}

export function normalizeAppearanceSettings(value: unknown): AppearanceSettings {
  if (!value || typeof value !== 'object') return defaultAppearanceSettings
  const candidate = value as Partial<AppearanceSettings>
  return {
    fontFamily: typeof candidate.fontFamily === 'string' && fontFamilies.has(candidate.fontFamily as AppearanceFontFamily) ? candidate.fontFamily as AppearanceFontFamily : defaultAppearanceSettings.fontFamily,
    fontSize: typeof candidate.fontSize === 'string' && fontSizes.has(candidate.fontSize as AppearanceFontSize) ? candidate.fontSize as AppearanceFontSize : defaultAppearanceSettings.fontSize,
    themeMode: typeof candidate.themeMode === 'string' && themeModes.has(candidate.themeMode as AppearanceThemeMode) ? candidate.themeMode as AppearanceThemeMode : defaultAppearanceSettings.themeMode,
    colors: normalizeColors(candidate.colors),
    colorThemes: normalizeColorThemes(candidate.colorThemes)
  }
}

export const appearanceSettingsQueryKey = ['appearance-settings'] as const

export function useAppearanceSettings() {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: appearanceSettingsQueryKey,
    queryFn: () => hubApi<AppearanceSettingsEnvelope>('/settings/appearance'),
    staleTime: 60_000,
    refetchOnWindowFocus: true
  })
  const settings = normalizeAppearanceSettings(query.data?.settings)
  const [draft, setDraft] = useState<AppearanceSettings>(settings)
  const settingsColorsKey = JSON.stringify(settings.colors)
  const settingsThemesKey = JSON.stringify(settings.colorThemes)

  useEffect(() => setDraft(settings), [settings.fontFamily, settings.fontSize, settings.themeMode, settingsColorsKey, settingsThemesKey])

  async function save(next: AppearanceSettings) {
    const normalized = normalizeAppearanceSettings(next)
    const saved = await hubApi<AppearanceSettingsEnvelope>('/settings/appearance', {
      method: 'PUT',
      body: JSON.stringify(normalized)
    })
    client.setQueryData(appearanceSettingsQueryKey, saved)
    setDraft(normalizeAppearanceSettings(saved.settings))
  }

  return { ...query, settings, draft, setDraft, save }
}
