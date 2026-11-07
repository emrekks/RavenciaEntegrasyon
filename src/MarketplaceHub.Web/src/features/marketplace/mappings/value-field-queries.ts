import { hubApi } from '../../../shared/api'

// The two reads a field's value table needs, shared by the table, the Web Color editor and the field list's prefetch
// (hover, focus, a click), so a field opened from the list usually finds both in the cache.
// A marketplace value list changes only when it is read from the marketplace again (Pazaryerinden Güncelle), which
// invalidates it; the saved pairs change through this window, which invalidates them after every save.
export const valueListStaleTime = 5 * 60_000
export const valueMappingStaleTime = 2 * 60_000

export function valueListQuery<T>(connectionId: string, categoryScope: string, externalAttributeId: string) {
  return {
    queryKey: ['reference-attribute-values', connectionId, `${categoryScope}/${externalAttributeId}`] as const,
    queryFn: () => hubApi<T>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`),
    retry: false,
    staleTime: valueListStaleTime
  }
}

export function valueMappingsQuery<T>(connectionId: string, valueScope: string) {
  return {
    queryKey: ['attribute-value-mappings', connectionId, valueScope] as const,
    queryFn: () => hubApi<T>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`),
    retry: false,
    staleTime: valueMappingStaleTime
  }
}

// Which panel values are mapped in each field of a category (by the field's marketplace id), in one read: the field
// list's progress ("21 / 73 değer"). It sits under the same key prefix as the value lists, so "Tüm Değerleri Eşle"
// (which invalidates them all) reads it again too.
export type ValueMappingProgress = Record<string, string[]>
export function valueMappingProgressQuery(connectionId: string, categoryScope: string) {
  return {
    queryKey: ['attribute-value-mappings', connectionId, `${categoryScope}/*`, 'progress'] as const,
    queryFn: () => hubApi<ValueMappingProgress>(`/mappings/attribute-value-progress?connectionId=${encodeURIComponent(connectionId)}&categoryExternalId=${encodeURIComponent(categoryScope)}`),
    retry: false,
    staleTime: valueMappingStaleTime
  }
}

// The mapped panel values of one field: its own saved list when that is at least as new as the category's progress
// read (a field saved in this window), otherwise the progress read; null while neither has arrived.
export function mappedLocalIds(
  field: { data?: readonly { localId: string }[]; dataUpdatedAt: number } | undefined,
  progress: { data?: ValueMappingProgress; dataUpdatedAt: number },
  externalAttributeId: string
): readonly string[] | null {
  const own = field?.data
  if (own && (!progress.data || field!.dataUpdatedAt >= progress.dataUpdatedAt)) return own.map(item => item.localId)
  if (progress.data) return progress.data[externalAttributeId] ?? []
  return own ? own.map(item => item.localId) : null
}
