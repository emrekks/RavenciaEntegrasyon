import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, settingsIfMatch } from '../../shared/api'
import { hasStoredShippingLabelSettings, loadShippingLabelSettings, normalizeShippingLabelSettings, type ShippingLabelSettings } from './shipping-label'

export const shippingLabelSettingsQueryKey = ['shipping-label-settings'] as const

export type ShippingLabelSettingsEnvelope = {
  settings: unknown | null
  version: number
}

/**
 * The browser copy is only a migration/offline fallback. Once the tenant has
 * a server record, it is the sole source of truth for every browser.
 */
export function useShippingLabelSettings() {
  const client = useQueryClient()
  const [settings, setSettings] = useState<ShippingLabelSettings>(() => loadShippingLabelSettings())
  const migrationAttempted = useRef(false)
  const query = useQuery({
    queryKey: shippingLabelSettingsQueryKey,
    queryFn: () => hubApi<ShippingLabelSettingsEnvelope>('/settings/shipping-label'),
    staleTime: 60_000,
    retry: 2,
    refetchOnWindowFocus: true
  })

  useEffect(() => {
    if (!query.isSuccess) return
    if (query.data.settings !== null) {
      setSettings(normalizeShippingLabelSettings(query.data.settings))
      return
    }
    if (migrationAttempted.current) return
    migrationAttempted.current = true
    const legacySettings = loadShippingLabelSettings()
    setSettings(legacySettings)
    if (!hasStoredShippingLabelSettings()) return
    void hubApi<ShippingLabelSettingsEnvelope>('/settings/shipping-label', {
      method: 'PUT',
      headers: settingsIfMatch(query.data.version),
      body: JSON.stringify(legacySettings)
    }).then(saved => {
      client.setQueryData(shippingLabelSettingsQueryKey, saved)
    }).catch(() => undefined)
  }, [client, query.data, query.isSuccess])

  async function save(next: ShippingLabelSettings) {
    const normalized = normalizeShippingLabelSettings(next)
    let saved: ShippingLabelSettingsEnvelope
    try {
      saved = await hubApi<ShippingLabelSettingsEnvelope>('/settings/shipping-label', { method: 'PUT', headers: settingsIfMatch(query.data?.version), body: JSON.stringify(normalized) })
    } catch (error) {
      // changed elsewhere meanwhile: read the current settings so the next save is made on them
      if (error instanceof ApiRequestError && error.status === 412) void client.invalidateQueries({ queryKey: shippingLabelSettingsQueryKey })
      throw error
    }
    setSettings(normalized)
    client.setQueryData(shippingLabelSettingsQueryKey, saved)
  }

  return { ...query, settings, setSettings, save }
}
