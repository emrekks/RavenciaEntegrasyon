// The Hızlı Güncelle choice ("Platformlarda Güncelle" → Kaydet), kept per tenant on the server so every device sends the
// same fields to the same stores (9.1.16). A choice this browser still holds from before (localStorage) is moved there
// once, only while the server has none; the local copy goes only after the server took it. A failed move (offline, a
// role that may not save, a save from another device meanwhile) keeps the local copy, which this browser goes on using
// until the next page open tries again.
import { useEffect, useRef } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, settingsIfMatch } from '../../../shared/api'
import { clearLegacyPlatformUpdateChoice, readLegacyPlatformUpdateChoice, type PlatformUpdateChoiceRecord } from './platform-update-fields'

export type PlatformUpdateChoiceEnvelope = { settings: PlatformUpdateChoiceRecord | null; version: number }
export const platformUpdateChoiceQueryKey = ['settings', 'platform-update'] as const
const path = '/settings/platform-update'
const readChoice = () => hubApi<PlatformUpdateChoiceEnvelope>(path)

export function usePlatformUpdateChoice() {
  const client = useQueryClient()
  const query = useQuery({ queryKey: platformUpdateChoiceQueryKey, queryFn: readChoice, staleTime: 60_000 })
  const migration = useRef<'idle' | 'running' | 'done'>('idle')
  const envelope = query.data

  useEffect(() => {
    if (!envelope || envelope.settings || migration.current !== 'idle') return
    const legacy = readLegacyPlatformUpdateChoice()
    if (!legacy) return
    migration.current = 'running'
    hubApi<PlatformUpdateChoiceEnvelope>(path, { method: 'PUT', headers: settingsIfMatch(envelope.version), body: JSON.stringify(legacy) })
      .then(saved => {
        client.setQueryData(platformUpdateChoiceQueryKey, saved)
        clearLegacyPlatformUpdateChoice()
      })
      .catch(() => { /* The local copy stays and keeps working here; the next page open tries again. */ })
      .finally(() => { migration.current = 'done' })
  }, [client, envelope])

  // The server's choice; while it has none, what this browser still holds from before.
  const record = envelope ? envelope.settings ?? readLegacyPlatformUpdateChoice() : null

  // Kaydet (and Gönder): over the version read (If-Match), so a choice saved meanwhile on another device is not
  // overwritten unseen; a refusal re-reads it and is reported by the caller.
  async function save(next: PlatformUpdateChoiceRecord) {
    const current = client.getQueryData<PlatformUpdateChoiceEnvelope>(platformUpdateChoiceQueryKey)
      ?? await client.fetchQuery({ queryKey: platformUpdateChoiceQueryKey, queryFn: readChoice })
    try {
      const saved = await hubApi<PlatformUpdateChoiceEnvelope>(path, { method: 'PUT', headers: settingsIfMatch(current?.version), body: JSON.stringify(next) })
      client.setQueryData(platformUpdateChoiceQueryKey, saved)
      // What this browser held from before is now superseded by the saved choice.
      clearLegacyPlatformUpdateChoice()
    } catch (error) {
      void client.invalidateQueries({ queryKey: platformUpdateChoiceQueryKey })
      throw error
    }
  }

  return { record, loaded: envelope !== undefined, failed: query.isError && envelope === undefined, save }
}
