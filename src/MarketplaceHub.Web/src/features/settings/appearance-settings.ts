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

export type AppearanceSettings = {
  fontFamily: AppearanceFontFamily
  fontSize: AppearanceFontSize
  themeMode: AppearanceThemeMode
  colors: { dark: AppearancePalette }
}

export type AppearanceSettingsEnvelope = {
  settings: AppearanceSettings | null
  version: number
}

export const defaultAppearanceSettings: AppearanceSettings = {
  fontFamily: 'inter',
  fontSize: 'normal',
  themeMode: 'dark',
  colors: {
    dark: {
      bg: '#0a0e1a', surface: '#111827', surfaceRaised: '#1a2235', surfaceSoft: '#1d2638', border: '#1e2d45', borderStrong: '#243352', ink: '#f1f5f9', muted: '#94a3b8', subtle: '#64748b', primary: '#6366f1', primaryHover: '#4f46e5', primarySoft: '#292d67', accent: '#10b981', accentSoft: '#173c3a', warning: '#f59e0b', warningSoft: '#4a3514', danger: '#ef4444', dangerSoft: '#4a282c', info: '#3b82f6'
    }
  }
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
  { value: 'dark', label: 'Moda Zeyn ERP – Koyu Tema', description: 'Çalışma alanının varsayılan ve tek arayüz teması' }
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
  if (!value || typeof value !== 'object') return { dark: { ...defaultAppearanceSettings.colors.dark } }
  const candidate = value as Partial<AppearanceColors>
  const dark = normalizePalette(candidate.dark, defaultAppearanceSettings.colors.dark)
  return { dark }
}

export function normalizeAppearanceSettings(value: unknown): AppearanceSettings {
  if (!value || typeof value !== 'object') return defaultAppearanceSettings
  const candidate = value as Partial<AppearanceSettings>
  return {
    fontFamily: typeof candidate.fontFamily === 'string' && fontFamilies.has(candidate.fontFamily as AppearanceFontFamily) ? candidate.fontFamily as AppearanceFontFamily : defaultAppearanceSettings.fontFamily,
    fontSize: typeof candidate.fontSize === 'string' && fontSizes.has(candidate.fontSize as AppearanceFontSize) ? candidate.fontSize as AppearanceFontSize : defaultAppearanceSettings.fontSize,
    themeMode: typeof candidate.themeMode === 'string' && themeModes.has(candidate.themeMode as AppearanceThemeMode) ? candidate.themeMode as AppearanceThemeMode : defaultAppearanceSettings.themeMode,
    colors: normalizeColors(candidate.colors)
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

  useEffect(() => setDraft(settings), [settings.fontFamily, settings.fontSize, settings.themeMode, settingsColorsKey])

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
