import { describe, expect, it, vi } from 'vitest'
import { QueryClient } from '@tanstack/react-query'
import { ApiRequestError } from '../../../shared/api'
import { isStaleMappingError, mappingSaveErrorMessage, refreshLocalBrands, refreshLocalCategories, refreshMappingConflicts, saveAllMappings } from './mapping-refresh'

describe('mapping refresh', () => {
  it('refreshes the conflict list of the connection', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    await refreshMappingConflicts(client, 'c1')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['mapping-conflicts', 'c1'] })
  })

  // W2: a brand or category created, archived or imported in the mapping screens is also in the new-product editor's
  // lists (and the bulk field dialog's), not only in the mapping screen's own copy.
  it('refreshes every copy of the panel brands and categories', async () => {
    const client = new QueryClient()
    for (const key of [['brands', 'mapping'], ['brands', 'new-product'], ['categories', 'mapping'], ['categories', 'new-product'], ['categories', 'mapping-builder-assignment']])
      client.setQueryData(key, { items: [] })

    await refreshLocalBrands(client)
    expect(client.getQueryState(['brands', 'mapping'])?.isInvalidated).toBe(true)
    expect(client.getQueryState(['brands', 'new-product'])?.isInvalidated).toBe(true)
    expect(client.getQueryState(['categories', 'new-product'])?.isInvalidated).toBe(false)

    await refreshLocalCategories(client)
    expect(client.getQueryState(['categories', 'mapping'])?.isInvalidated).toBe(true)
    expect(client.getQueryState(['categories', 'new-product'])?.isInvalidated).toBe(true)
    expect(client.getQueryState(['categories', 'mapping-builder-assignment'])?.isInvalidated).toBe(true)
  })

  it('explains an existing mapping and a stale version', () => {
    expect(mappingSaveErrorMessage(new ApiRequestError('x', 409, 'MAPPING_EXISTS'), 'f')).toContain('eşleme zaten var')
    expect(mappingSaveErrorMessage(new ApiRequestError('x', 412, 'CONCURRENCY_CONFLICT'), 'f')).toContain('başka bir işlemle değişti')
    expect(mappingSaveErrorMessage(new Error('Sunucu hatası'), 'f')).toBe('Sunucu hatası')
    expect(isStaleMappingError(new ApiRequestError('x', 422))).toBe(false)
  })

  it('keeps saving after a failure', async () => {
    const result = await saveAllMappings([1, 2, 3], async item => { if (item === 2) throw new Error('hata') })
    expect(result.saved).toBe(2)
    expect(result.failures.map(failure => failure.item)).toEqual([2])
  })
})
