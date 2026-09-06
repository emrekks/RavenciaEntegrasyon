import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'

export type AppearanceFontFamily = 'inter' | 'system' | 'segoe' | 'arial'
export type AppearanceFontSize = 'small' | 'normal' | 'large' | 'extra-large'

export type AppearanceSettings = {
  fontFamily: AppearanceFontFamily
  fontSize: AppearanceFontSize
}

export type AppearanceSettingsEnvelope = {
  settings: AppearanceSettings | null
  version: number
}

export const defaultAppearanceSettings: AppearanceSettings = {
  fontFamily: 'inter',
  fontSize: 'normal'
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

export const appearanceFontFamilyCss: Record<AppearanceFontFamily, string> = {
  inter: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  system: 'ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  segoe: '"Segoe UI", system-ui, sans-serif',
  arial: 'Arial, Helvetica, sans-serif'
}

export const appearanceFontScale: Record<AppearanceFontSize, number> = {
  small: 1.18,
  normal: 1.32,
  large: 1.48,
  'extra-large': 1.68
}

const fontFamilies = new Set<AppearanceFontFamily>(appearanceFontFamilyOptions.map(option => option.value))
const fontSizes = new Set<AppearanceFontSize>(appearanceFontSizeOptions.map(option => option.value))

export function normalizeAppearanceSettings(value: unknown): AppearanceSettings {
  if (!value || typeof value !== 'object') return defaultAppearanceSettings
  const candidate = value as Partial<AppearanceSettings>
  return {
    fontFamily: typeof candidate.fontFamily === 'string' && fontFamilies.has(candidate.fontFamily as AppearanceFontFamily) ? candidate.fontFamily as AppearanceFontFamily : defaultAppearanceSettings.fontFamily,
    fontSize: typeof candidate.fontSize === 'string' && fontSizes.has(candidate.fontSize as AppearanceFontSize) ? candidate.fontSize as AppearanceFontSize : defaultAppearanceSettings.fontSize
  }
}

export const appearanceSettingsQueryKey = ['appearance-settings'] as const
const appearanceStorageKey = 'ravencia.appearanceSettings'

function readCachedAppearance(): AppearanceSettings | null {
  try {
    const raw = localStorage.getItem(appearanceStorageKey)
    return raw ? normalizeAppearanceSettings(JSON.parse(raw)) : null
  } catch {
    return null
  }
}

async function loadAppearanceSettings(): Promise<AppearanceSettingsEnvelope> {
  try {
    const response = await hubApi<AppearanceSettingsEnvelope>('/settings/appearance')
    const settings = normalizeAppearanceSettings(response.settings)
    localStorage.setItem(appearanceStorageKey, JSON.stringify(settings))
    return { ...response, settings }
  } catch {
    const cached = readCachedAppearance()
    return { settings: cached ?? defaultAppearanceSettings, version: 0 }
  }
}

export function useAppearanceSettings() {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: appearanceSettingsQueryKey,
    queryFn: loadAppearanceSettings,
    staleTime: 60_000,
    retry: 2,
    refetchOnWindowFocus: true
  })
  const settings = normalizeAppearanceSettings(query.data?.settings)
  const [draft, setDraft] = useState<AppearanceSettings>(settings)

  useEffect(() => setDraft(settings), [settings.fontFamily, settings.fontSize])

  async function save(next: AppearanceSettings) {
    const normalized = normalizeAppearanceSettings(next)
    const saved = await hubApi<AppearanceSettingsEnvelope>('/settings/appearance', {
      method: 'PUT',
      body: JSON.stringify(normalized)
    })
    client.setQueryData(appearanceSettingsQueryKey, saved)
    const settings = normalizeAppearanceSettings(saved.settings)
    localStorage.setItem(appearanceStorageKey, JSON.stringify(settings))
    setDraft(settings)
  }

  return { ...query, settings, draft, setDraft, save }
}
