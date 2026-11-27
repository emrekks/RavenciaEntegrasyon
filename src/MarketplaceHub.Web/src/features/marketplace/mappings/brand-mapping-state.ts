import type { QueryClient } from '@tanstack/react-query'
import { ApiRequestError } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { brandReferenceKey } from './brand-reference'

/**
 * The connection the brand mapping card works on: the one picked on this platform, else the platform's first. It is
 * derived while rendering, so a platform tab never paints a frame without a connection (no footer, "no saved mapping").
 */
export function brandPlatformConnectionId(platformConnections: readonly { id: string }[], choice: string) {
  return platformConnections.some(item => item.id === choice) ? choice : platformConnections[0]?.id ?? ''
}

/**
 * Whether the panel brand's saved mapping on this connection is known (scan 25 Nov 2026, D-4). Only a read that
 * answered (a mapping, or none) is known: a failed read is not "no mapping", so the card neither offers
 * "Eşleştirmeyi Kaydet" without the saved version nor shows the picked brand as new.
 */
export type BrandMappingReadState = 'idle' | 'loading' | 'failed' | 'known'
export function brandMappingReadState(query: { isSuccess: boolean; isError: boolean }, enabled: boolean): BrandMappingReadState {
  if (!enabled) return 'idle'
  if (query.isSuccess) return 'known'
  if (query.isError) return 'failed'
  return 'loading'
}

/**
 * Kuyruk Faz 1B (review O1): the brand list is updated in place, so a save names the brand as the page showed it and
 * the server refuses (409 REFERENCE_ITEM_CHANGED) when the brand was renamed or removed since. A mapping a list refresh
 * moved aside (412, its version changed) is the same story for the user: the list changed, choose again.
 */
export const brandListChangedMessage = 'Marka Listesi Değişti, Yeniden Seçin. Seçtiğiniz pazaryeri markası siz seçtikten sonra değişti ya da listeden kalktı; liste yenilendi.'

export function isBrandListChangedError(error: unknown) {
  return error instanceof ApiRequestError && (error.code === 'REFERENCE_ITEM_CHANGED' || error.status === 412)
}

/** Reads the connection's brand list (summary, search, names) and its brand mappings again after such a refusal. */
export function reloadAfterBrandListChange(client: Pick<QueryClient, 'invalidateQueries'>, connectionId: string, localId?: string) {
  return Promise.all([
    client.invalidateQueries({ queryKey: brandReferenceKey(connectionId) }),
    client.invalidateQueries({ queryKey: queryKeys.brandMapping(localId, connectionId) }),
    client.invalidateQueries({ queryKey: queryKeys.brandMappings(connectionId) })
  ])
}
