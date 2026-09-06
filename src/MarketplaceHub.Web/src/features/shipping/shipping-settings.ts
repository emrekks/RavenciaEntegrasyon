import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
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
      body: JSON.stringify(legacySettings)
    }).then(saved => {
      client.setQueryData(shippingLabelSettingsQueryKey, saved)
    }).catch(() => undefined)
  }, [client, query.data, query.isSuccess])

  async function save(next: ShippingLabelSettings) {
    const normalized = normalizeShippingLabelSettings(next)
    const saved = await hubApi<ShippingLabelSettingsEnvelope>('/settings/shipping-label', {
      method: 'PUT',
      body: JSON.stringify(normalized)
    })
    setSettings(normalized)
    client.setQueryData(shippingLabelSettingsQueryKey, saved)
  }

  return { ...query, settings, setSettings, save }
}
