import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'

export type AppearanceFontFamily = 'inter' | 'system' | 'segoe' | 'arial'
export type AppearanceFontSize = 'compact' | 'small' | 'normal' | 'large' | 'extra-large' | 'huge'
export type AppearanceThemeMode = 'dark'
export type AppearanceColorTheme = 'dark' | 'light'
export type AppearanceColorToken = 'bg' | 'sidebar' | 'surface' | 'surfaceRaised' | 'surfaceSoft' | 'border' | 'borderStrong' | 'ink' | 'muted' | 'subtle' | 'primary' | 'primaryHover' | 'primarySoft' | 'accent' | 'accentSoft' | 'warning' | 'warningSoft' | 'danger' | 'dangerSoft' | 'info'

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
  colors: AppearanceColors
  colorThemes: AppearanceColorThemeProfile[]
}

export type AppearanceSettingsEnvelope = {
  settings: AppearanceSettings | null
  version: number
}

const defaultDarkPalette: AppearancePalette = {
  bg: '#101014', sidebar: '#17171c', surface: '#17171c', surfaceRaised: '#1d1d23', surfaceSoft: '#22222a', border: '#2a2a32', borderStrong: '#383842', ink: '#ececf0', muted: '#a3a3b0', subtle: '#6f6f7c', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#272139', accent: '#46b98f', accentSoft: '#1e2e2c', warning: '#e2aa4b', warningSoft: '#332c23', danger: '#e4636f', dangerSoft: '#342228', info: '#83b4ff'
}

// Clean light surfaces with grey lines and one violet accent. It is only the starting point; the palette is
// editable in Settings and saved with the account.
export const defaultLightPalette: AppearancePalette = {
  bg: '#f6f6f8', sidebar: '#ffffff', surface: '#ffffff', surfaceRaised: '#f4f4f6', surfaceSoft: '#eeeef2', border: '#e4e4ea', borderStrong: '#d3d3dc', ink: '#17171c', muted: '#5b5b68', subtle: '#8c8c99', primary: '#6d3df0', primaryHover: '#5b2ee0', primarySoft: '#f2eefe', accent: '#1f9a6c', accentSoft: '#e3f2ec', warning: '#b7791f', warningSoft: '#f6ecdc', danger: '#c8414e', dangerSoft: '#f8e3e5', info: '#2f5aa8'
}

export const defaultAppearanceColorTheme: AppearanceColorThemeProfile = {
  id: 'default-dark',
  name: 'Ravencia — Grafit',
  palette: { ...defaultDarkPalette },
  builtIn: true
}

export const defaultAppearanceSettings: AppearanceSettings = {
  fontFamily: 'inter',
  fontSize: 'small',
  themeMode: 'dark',
  colors: {
    dark: { ...defaultDarkPalette },
    light: { ...defaultLightPalette }
  },
  colorThemes: [{ ...defaultAppearanceColorTheme, palette: { ...defaultAppearanceColorTheme.palette } }]
}

export const appearanceColorTokenOptions: Array<{ key: AppearanceColorToken; label: string; description: string }> = [
  { key: 'bg', label: 'Arka plan', description: 'Sayfanın ana zemini' },
  { key: 'sidebar', label: 'Menü zemini', description: 'Ana menünün zemini' },
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
  { value: 'compact', label: 'Çok küçük', description: 'En fazla içerik için en sıkı görünüm' },
  { value: 'small', label: 'Küçük', description: 'Normal ve varsayılan çalışma görünümü' },
  { value: 'normal', label: 'Normal', description: 'Daha rahat okuma için biraz daha geniş görünüm' },
  { value: 'large', label: 'Büyük', description: 'Yoğun tablolarda daha rahat okuma' },
  { value: 'extra-large', label: 'Çok büyük', description: 'Yüksek okunabilirlik için ferah görünüm' },
  { value: 'huge', label: 'Ekstra büyük', description: 'Maksimum okunabilirlik ve daha belirgin metinler' }
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
  compact: 0.92,
  small: 1,
  normal: 1.08,
  large: 1.16,
  'extra-large': 1.25,
  huge: 1.34
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
  if (!value || typeof value !== 'object') return { dark: { ...defaultDarkPalette }, light: { ...defaultLightPalette } }
  const candidate = value as Partial<AppearanceColors>
  return { dark: migrateFormerDarkDefault(normalizePalette(candidate.dark, defaultDarkPalette)), light: migrateFormerLightDefault(normalizePalette(candidate.light, defaultLightPalette)) }
}

// Accounts that saved an earlier default palette (never edited by hand) follow the current default.
const formerLightBackgrounds = new Set(['#d4d9e2', '#cbd0db', '#d8dce5', '#e1e4eb'])
const formerLightSurfaces = new Set(['#e5e8ee', '#d9dde6', '#dfe3ea', '#eaedf2'])
function migrateFormerLightDefault(palette: AppearancePalette): AppearancePalette {
  return formerLightBackgrounds.has(palette.bg.toLowerCase()) && formerLightSurfaces.has(palette.surface.toLowerCase()) ? { ...defaultLightPalette } : palette
}
const formerDarkSurfaces = new Set(['#18181f'])
const formerDarkRaised = new Set(['#202028'])
function migrateFormerDarkDefault(palette: AppearancePalette): AppearancePalette {
  return formerDarkSurfaces.has(palette.surface.toLowerCase()) && formerDarkRaised.has(palette.surfaceRaised.toLowerCase()) ? { ...defaultDarkPalette } : palette
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
