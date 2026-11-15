import type { QueryClient } from '@tanstack/react-query'
import { ApiRequestError } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'

// Every mapping save or delete can create or clear a conflict, so the "Gözden Geçirilecek Eşlemeler" list is
// refreshed after each one instead of waiting for its cache to go stale.
export function refreshMappingConflicts(client: QueryClient, connectionId: string | null | undefined) {
  return connectionId
    ? client.invalidateQueries({ queryKey: queryKeys.mappingConflicts(connectionId) })
    : client.invalidateQueries({ queryKey: queryKeys.mappingConflicts() })
}

// W2: the panel's brands and categories are read under several keys (the mapping screens', the new-product editor's
// ['brands', 'new-product'], the bulk field dialog's): one created, archived or imported here refreshes them all through
// the root, so the editor offers it at once instead of after its cache expires.
export function refreshLocalBrands(client: QueryClient) {
  return client.invalidateQueries({ queryKey: queryKeys.brands() })
}

export function refreshLocalCategories(client: QueryClient) {
  return client.invalidateQueries({ queryKey: queryKeys.categories() })
}

// A mapping that another save created or changed meanwhile: the list must be reloaded before trying again.
export function isStaleMappingError(error: unknown) {
  return error instanceof ApiRequestError && (error.status === 409 || error.status === 412)
}

export function mappingSaveErrorMessage(error: unknown, fallback: string) {
  if (error instanceof ApiRequestError && error.code === 'MAPPING_EXISTS') return 'Bu kayıt için eşleme zaten var. Liste yenilendi; mevcut eşlemeyi düzenleyin.'
  if (isStaleMappingError(error)) return 'Eşleme bu sırada başka bir işlemle değişti. Liste yenilendi; kontrol edip yeniden deneyin.'
  return error instanceof Error && error.message ? error.message : fallback
}

// Runs every save even when some fail, so one refused value does not leave the rest unsaved or the form stuck.
export async function saveAllMappings<T>(items: T[], save: (item: T) => Promise<unknown>) {
  let saved = 0
  const failures: Array<{ item: T; error: unknown }> = []
  for (const item of items) {
    try {
      await save(item)
      saved++
    } catch (error) {
      failures.push({ item, error })
    }
  }
  return { saved, failures }
}
