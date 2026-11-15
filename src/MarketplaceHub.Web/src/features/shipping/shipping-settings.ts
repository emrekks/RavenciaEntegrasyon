import type { ApiAnswer } from '../../shared/api/api-answer'
import { useLayoutEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, settingsIfMatch, type Me } from '../../shared/api'
import { lacksElevatedRole } from '../elevated-role'
import { hasStoredShippingLabelSettings, loadShippingLabelSettings, normalizeShippingLabelSettings, type ShippingLabelSettings } from './shipping-label'
import { queryKeys } from '../../shared/api/query-keys'

export const shippingLabelSettingsQueryKey = queryKeys.shippingLabelSettings()

export type ShippingLabelSettingsEnvelope = {
  settings: unknown | null
  version: number
}

function sameSettings(left: ShippingLabelSettings, right: ShippingLabelSettings) {
  return JSON.stringify(normalizeShippingLabelSettings(left)) === JSON.stringify(normalizeShippingLabelSettings(right))
}

/**
 * The browser copy is only a migration/offline fallback. Once the tenant has
 * a server record, it is the sole source of truth for every browser.
 */
export function useShippingLabelSettings() {
  const client = useQueryClient()
  // Starts from the account's settings when they are already read, so a page does not show this browser's copy first.
  const [settings, setSettings] = useState<ShippingLabelSettings>(() => {
    const cached = client.getQueryData<ShippingLabelSettingsEnvelope>(shippingLabelSettingsQueryKey)?.settings
    return cached != null ? normalizeShippingLabelSettings(cached) : loadShippingLabelSettings()
  })
  // The server's settings the draft (settings) was made from, with their version (WEB-SAYFALAR-4). Newer settings read
  // later (another session's save, a 412, a focus refetch) replace only a draft equal to this; an edited draft is kept
  // and the newer copy waits as a conflict, offered by acceptServer and overwrite.
  const [base, setBase] = useState<{ settings: ShippingLabelSettings; version: number } | null>(() => {
    const cached = client.getQueryData<ShippingLabelSettingsEnvelope>(shippingLabelSettingsQueryKey)
    return cached?.settings != null ? { settings: normalizeShippingLabelSettings(cached.settings), version: cached.version } : null
  })
  const draftRef = useRef(settings)
  draftRef.current = settings
  const baseRef = useRef(base)
  baseRef.current = base
  const migrationAttempted = useRef(false)
  const query = useQuery({
    queryKey: shippingLabelSettingsQueryKey,
    queryFn: ({ signal }) => hubApi<ApiAnswer<'getSettingsShippingLabel'>>('/settings/shipping-label', { signal }),
    staleTime: 60_000,
    retry: 2,
    refetchOnWindowFocus: true
  })

  // Before paint: the server's settings replace the first value without showing it.
  useLayoutEffect(() => {
    if (!query.isSuccess) return
    if (query.data.settings !== null) {
      const server = normalizeShippingLabelSettings(query.data.settings)
      const current = baseRef.current
      const draftClean = current === null || sameSettings(draftRef.current, current.settings)
      if (draftClean) {
        setSettings(server)
        setBase({ settings: server, version: query.data.version })
      } else if (sameSettings(server, current.settings)) {
        // Only the version moved: nothing to choose between.
        setBase({ settings: current.settings, version: query.data.version })
      }
      return
    }
    if (migrationAttempted.current) return
    migrationAttempted.current = true
    const legacySettings = loadShippingLabelSettings()
    setSettings(legacySettings)
    if (!hasStoredShippingLabelSettings()) return
    // Only the owner and administrators may write the account's settings: the other roles keep the browser copy on
    // screen without a PUT the server would refuse.
    if (lacksElevatedRole(client.getQueryData<Me>(queryKeys.me())?.role)) return
    void hubApi<ApiAnswer<'putSettingsShippingLabel'>>('/settings/shipping-label', {
      method: 'PUT',
      headers: settingsIfMatch(query.data.version),
      body: JSON.stringify(legacySettings)
    }).then(saved => {
      client.setQueryData(shippingLabelSettingsQueryKey, saved)
    }).catch(() => undefined)
  }, [client, query.data, query.isSuccess])

  async function save(next: ShippingLabelSettings, overVersion?: number) {
    const normalized = normalizeShippingLabelSettings(next)
    let saved: ShippingLabelSettingsEnvelope
    try {
      // D9: a stored setting is written only over the version read (If-Match); one not read yet is read first. The
      // version is the one the draft was made from, so a draft kept over newer settings is refused, not written over them.
      const current = query.data ?? await client.fetchQuery({ queryKey: shippingLabelSettingsQueryKey, queryFn: ({ signal }) => hubApi<ApiAnswer<'getSettingsShippingLabel'>>('/settings/shipping-label', { signal }) })
      saved = await hubApi<ApiAnswer<'putSettingsShippingLabel'>>('/settings/shipping-label', { method: 'PUT', headers: settingsIfMatch(overVersion ?? baseRef.current?.version ?? current?.version), body: JSON.stringify(normalized) })
    } catch (error) {
      // changed elsewhere meanwhile: read the current settings; the draft stays and the newer copy waits as a conflict
      if (error instanceof ApiRequestError && (error.status === 412 || error.status === 428)) void client.invalidateQueries({ queryKey: shippingLabelSettingsQueryKey })
      throw error
    }
    setSettings(normalized)
    setBase({ settings: saved.settings != null ? normalizeShippingLabelSettings(saved.settings) : normalized, version: saved.version })
    client.setQueryData(shippingLabelSettingsQueryKey, saved)
  }

  // Newer settings were saved elsewhere while this draft held edits.
  const conflict = Boolean(base && query.data?.settings != null && query.data.version !== base.version)
  // Güncel Ayarı Yükle: the draft gives way to the newer settings.
  function acceptServer() {
    if (query.data?.settings == null) return
    const server = normalizeShippingLabelSettings(query.data.settings)
    setSettings(server)
    setBase({ settings: server, version: query.data.version })
  }
  // Benimkiyle Üzerine Yaz: the draft is written over the newer settings, knowingly.
  function overwrite() {
    return save(draftRef.current, query.data?.version)
  }

  return { ...query, settings, setSettings, save, conflict, acceptServer, overwrite }
}
