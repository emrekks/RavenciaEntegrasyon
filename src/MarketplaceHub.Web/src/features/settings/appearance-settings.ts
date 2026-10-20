import { useEffect, useEffectEvent, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'

export type AppearanceFontFamily = 'inter' | 'system' | 'segoe' | 'arial'
export type AppearanceFontSize = 'xs' | 'sm' | 'md' | 'lg' | 'xl' | 'xxl'
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
  bg: '#101014', sidebar: '#17171c', surface: '#17171c', surfaceRaised: '#1d1d23', surfaceSoft: '#22222a', border: '#2a2a32', borderStrong: '#383842', ink: '#ececf0', muted: '#a3a3b0', subtle: '#8a8a99', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#272139', accent: '#46b98f', accentSoft: '#1e2e2c', warning: '#e2aa4b', warningSoft: '#332c23', danger: '#e4636f', dangerSoft: '#342228', info: '#83b4ff'
}

// Soft by design: no pure white anywhere, so large light areas stay easy on the eyes. It is only the starting
// point; the palette is editable in Settings and saved with the account.
export const defaultLightPalette: AppearancePalette = {
  bg: '#e1e4eb', sidebar: '#eaedf2', surface: '#eaedf2', surfaceRaised: '#f0f2f6', surfaceSoft: '#dfe3ea', border: '#c9d0dc', borderStrong: '#a4aec0', ink: '#2f3a4f', muted: '#525e77', subtle: '#59657d', primary: '#5b44d2', primaryHover: '#4b36bb', primarySoft: '#e0dcf6', accent: '#0f7864', accentSoft: '#d3e8df', warning: '#8a560d', warningSoft: '#efe1c0', danger: '#a2344f', dangerSoft: '#efd5dc', info: '#2f5aa8'
}

// Sade: darker, cooler surfaces with thin lines and a single violet accent. It is the default dark theme.
export const defaultAppearanceColorTheme: AppearanceColorThemeProfile = {
  id: 'default-dark',
  name: 'Ravencia — Sade',
  palette: { ...defaultDarkPalette },
  builtIn: true
}

// The former default stays available as a built-in choice.
const grafitDarkPalette: AppearancePalette = {
  bg: '#101014', sidebar: '#18181f', surface: '#18181f', surfaceRaised: '#202028', surfaceSoft: '#272730', border: '#303039', borderStrong: '#41414d', ink: '#f4f4f7', muted: '#aaaab9', subtle: '#8d8d9d', primary: '#8b5cf6', primaryHover: '#7c3aed', primarySoft: '#302443', accent: '#4adea0', accentSoft: '#1c332c', warning: '#f6c16b', warningSoft: '#382f22', danger: '#fb858b', dangerSoft: '#3c252b', info: '#83b4ff'
}

export const grafitAppearanceColorTheme: AppearanceColorThemeProfile = {
  id: 'ravencia-grafit',
  name: 'Ravencia — Grafit',
  palette: { ...grafitDarkPalette },
  builtIn: true
}

export const builtInAppearanceColorThemes: AppearanceColorThemeProfile[] = [defaultAppearanceColorTheme, grafitAppearanceColorTheme]
const builtInThemeProfiles = () => builtInAppearanceColorThemes.map(theme => ({ ...theme, palette: { ...theme.palette } }))

// The theme whose palette is the account's current dark palette; a hand-edited palette falls back to the default.
export function matchingColorThemeId(settings: AppearanceSettings) {
  const dark = settings.colors.dark
  const same = (palette: AppearancePalette) => (Object.keys(palette) as AppearanceColorToken[]).every(key => palette[key].toLowerCase() === dark[key].toLowerCase())
  return settings.colorThemes.find(theme => same(theme.palette))?.id ?? defaultAppearanceColorTheme.id
}

export const defaultAppearanceSettings: AppearanceSettings = {
  fontFamily: 'inter',
  fontSize: 'md',
  themeMode: 'dark',
  colors: {
    dark: { ...defaultDarkPalette },
    light: { ...defaultLightPalette }
  },
  colorThemes: builtInThemeProfiles()
}

export const appearanceColorTokenOptions: Array<{ key: AppearanceColorToken; label: string; description: string }> = [
  { key: 'bg', label: 'Arka Plan', description: 'Sayfanın ana zemini' },
  { key: 'sidebar', label: 'Menü Zemini', description: 'Ana menünün zemini' },
  { key: 'surface', label: 'Yüzey', description: 'Kart ve panel zemini' },
  { key: 'surfaceRaised', label: 'Yükseltilmiş Yüzey', description: 'Alan ve kontrol zemini' },
  { key: 'surfaceSoft', label: 'Yumuşak Yüzey', description: 'İkincil yüzey ve rozet zemini' },
  { key: 'border', label: 'Kenarlık', description: 'Standart ayırıcı çizgiler' },
  { key: 'borderStrong', label: 'Güçlü Kenarlık', description: 'Input ve belirgin çerçeveler' },
  { key: 'ink', label: 'Ana Metin', description: 'Başlık ve önemli metinler' },
  { key: 'muted', label: 'İkincil Metin', description: 'Açıklama ve yardımcı metinler' },
  { key: 'subtle', label: 'Soluk Metin', description: 'Düşük öncelikli metinler' },
  { key: 'primary', label: 'Ana Renk', description: 'Birincil buton ve aktif durum' },
  { key: 'primaryHover', label: 'Ana Renk Hover', description: 'Birincil etkileşim hover durumu' },
  { key: 'primarySoft', label: 'Ana Renk Yumuşak', description: 'Seçili ve hafif vurgulu alanlar' },
  { key: 'accent', label: 'Accent', description: 'Başarı ve ikincil vurgu' },
  { key: 'accentSoft', label: 'Accent Yumuşak', description: 'Başarı arka planları' },
  { key: 'warning', label: 'Uyarı', description: 'Uyarı ve dikkat durumları' },
  { key: 'warningSoft', label: 'Uyarı Yumuşak', description: 'Uyarı arka planları' },
  { key: 'danger', label: 'Hata', description: 'Hata ve tehlikeli işlemler' },
  { key: 'dangerSoft', label: 'Hata Yumuşak', description: 'Hata arka planları' },
  { key: 'info', label: 'Bilgi', description: 'Bilgilendirme vurgusu' }
]

export function appearanceColorCssVariable(token: AppearanceColorToken) {
  return `--rv-color-${token.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`
}

export const appearanceFontFamilyOptions: Array<{ value: AppearanceFontFamily; label: string }> = [
  { value: 'inter', label: 'Inter' },
  { value: 'system', label: 'Sistem Yazı Tipi' },
  { value: 'segoe', label: 'Segoe UI' },
  { value: 'arial', label: 'Arial' }
]

export const appearanceFontSizeOptions: Array<{ value: AppearanceFontSize; label: string; description: string }> = [
  { value: 'xs', label: 'Çok Küçük', description: 'En fazla içerik için en sıkı görünüm' },
  { value: 'sm', label: 'Küçük', description: 'Normalden biraz daha sıkı görünüm' },
  { value: 'md', label: 'Normal', description: 'Varsayılan çalışma görünümü' },
  { value: 'lg', label: 'Büyük', description: 'Normalden biraz daha rahat okuma' },
  { value: 'xl', label: 'Çok Büyük', description: 'Yüksek okunabilirlik için ferah görünüm' },
  { value: 'xxl', label: 'Ekstra Büyük', description: 'Maksimum okunabilirlik ve daha belirgin metinler' }
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
  xs: 0.86,
  sm: 0.93,
  md: 1,
  lg: 1.08,
  xl: 1.16,
  xxl: 1.25
}

// Sizes saved before the scale was re-centred on the working view ("small" was the default) keep their look.
const formerFontSizes: Record<string, AppearanceFontSize> = { compact: 'sm', small: 'md', normal: 'lg', large: 'xl', 'extra-large': 'xxl', huge: 'xxl' }
function normalizeFontSize(value: unknown): AppearanceFontSize {
  if (typeof value !== 'string') return defaultAppearanceSettings.fontSize
  const current = formerFontSizes[value] ?? value
  return fontSizes.has(current as AppearanceFontSize) ? current as AppearanceFontSize : defaultAppearanceSettings.fontSize
}

const fontFamilies = new Set<AppearanceFontFamily>(appearanceFontFamilyOptions.map(option => option.value))
const fontSizes = new Set<AppearanceFontSize>(appearanceFontSizeOptions.map(option => option.value))
const themeModes = new Set<AppearanceThemeMode>(appearanceThemeModeOptions.map(option => option.value))
const colorTokens: AppearanceColorToken[] = appearanceColorTokenOptions.map(option => option.key)
const hexColor = /^#[0-9a-f]{6}$/i

// The former built-in "Soluk Metin" colours were below 4.5:1 on their surfaces; a palette that still carries one follows
// the readable replacement (a colour the person picked by hand is kept).
const formerSubtleColors: Record<string, string> = { '#6f6f7c': '#8a8a99', '#69758d': '#59657d', '#858595': '#8d8d9d' }
function normalizePalette(value: unknown, fallback: AppearancePalette): AppearancePalette {
  if (!value || typeof value !== 'object') return { ...fallback }
  const candidate = value as Partial<AppearancePalette>
  const palette = Object.fromEntries(colorTokens.map(token => [token, typeof candidate[token] === 'string' && hexColor.test(candidate[token]) ? candidate[token] : fallback[token]])) as AppearancePalette
  return { ...palette, subtle: formerSubtleColors[palette.subtle.toLowerCase()] ?? palette.subtle }
}

// Settings saved before the Grafit theme existed carry no Grafit entry; only those move from the former default to Sade.
function normalizeColors(value: unknown, fromBeforeGrafitTheme = false): AppearanceColors {
  if (!value || typeof value !== 'object') return { dark: { ...defaultDarkPalette }, light: { ...defaultLightPalette } }
  const candidate = value as Partial<AppearanceColors>
  return { dark: fromBeforeGrafitTheme ? migrateFormerDarkDefault(normalizePalette(candidate.dark, defaultDarkPalette)) : normalizePalette(candidate.dark, defaultDarkPalette), light: migrateFormerLightDefault(normalizePalette(candidate.light, defaultLightPalette)) }
}

// Accounts that saved an earlier default palette (never edited by hand) follow the current default.
const formerLightBackgrounds = new Set(['#d4d9e2', '#cbd0db', '#d8dce5'])
const formerLightSurfaces = new Set(['#e5e8ee', '#d9dde6', '#dfe3ea'])
function migrateFormerLightDefault(palette: AppearancePalette): AppearancePalette {
  return formerLightBackgrounds.has(palette.bg.toLowerCase()) && formerLightSurfaces.has(palette.surface.toLowerCase()) ? { ...defaultLightPalette } : palette
}
const formerDarkSurfaces = new Set(['#18181f'])
const formerDarkRaised = new Set(['#202028'])
function migrateFormerDarkDefault(palette: AppearancePalette): AppearancePalette {
  return formerDarkSurfaces.has(palette.surface.toLowerCase()) && formerDarkRaised.has(palette.surfaceRaised.toLowerCase()) ? { ...defaultDarkPalette } : palette
}

function normalizeColorThemes(value: unknown): AppearanceColorThemeProfile[] {
  const profiles: AppearanceColorThemeProfile[] = builtInThemeProfiles()
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
    fontSize: normalizeFontSize(candidate.fontSize),
    themeMode: typeof candidate.themeMode === 'string' && themeModes.has(candidate.themeMode as AppearanceThemeMode) ? candidate.themeMode as AppearanceThemeMode : defaultAppearanceSettings.themeMode,
    colors: normalizeColors(candidate.colors, !(Array.isArray(candidate.colorThemes) && candidate.colorThemes.some(theme => (theme as Partial<AppearanceColorThemeProfile> | null)?.id === grafitAppearanceColorTheme.id))),
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

  const resetDraft = useEffectEvent(() => setDraft(settings))
  useEffect(() => resetDraft(), [settings.fontFamily, settings.fontSize, settings.themeMode, settingsColorsKey, settingsThemesKey])

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
