import { describe, expect, it, vi } from 'vitest'
import { QueryClient } from '@tanstack/react-query'
import { ApiRequestError } from '../../../shared/api'
import { isStaleMappingError, mappingSaveErrorMessage, refreshMappingConflicts, saveAllMappings } from './mapping-refresh'

describe('mapping refresh', () => {
  it('refreshes the conflict list of the connection', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    await refreshMappingConflicts(client, 'c1')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['mapping-conflicts', 'c1'] })
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
