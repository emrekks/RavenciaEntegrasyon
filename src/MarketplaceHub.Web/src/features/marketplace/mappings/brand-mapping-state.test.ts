import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { brandListChangedMessage, brandMappingReadState, brandPlatformConnectionId, isBrandListChangedError, reloadAfterBrandListChange } from './brand-mapping-state'
import { brandReferenceKey } from './brand-reference'

describe('a save refused because the brand list changed', () => {
  it('is the 409 REFERENCE_ITEM_CHANGED or the 412 of a mapping moved aside, not any other refusal', () => {
    expect(isBrandListChangedError(new ApiRequestError('x', 409, 'REFERENCE_ITEM_CHANGED'))).toBe(true)
    expect(isBrandListChangedError(new ApiRequestError('x', 412, 'CONCURRENCY_CONFLICT'))).toBe(true)
    expect(isBrandListChangedError(new ApiRequestError('x', 409, 'MAPPING_EXISTS'))).toBe(false)
    expect(isBrandListChangedError(new Error('x'))).toBe(false)
    expect(brandListChangedMessage).toMatch(/^Marka Listesi Değişti, Yeniden Seçin/)
  })

  it('reads the brand list and the mappings of that connection again', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    await reloadAfterBrandListChange(client, 'ty-1', 'brand-1')
    const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey))
    expect(keys).toEqual([JSON.stringify(brandReferenceKey('ty-1')), JSON.stringify(queryKeys.brandMapping('brand-1', 'ty-1')), JSON.stringify(queryKeys.brandMappings('ty-1'))])
  })
})

describe('brandPlatformConnectionId', () => {
  const pazarama = [{ id: 'pz-1' }, { id: 'pz-2' }]

  it('takes the platform first connection the moment its tab opens', () => {
    expect(brandPlatformConnectionId(pazarama, '')).toBe('pz-1')
  })

  it('keeps the connection picked on this platform', () => {
    expect(brandPlatformConnectionId(pazarama, 'pz-2')).toBe('pz-2')
  })

  it('does not carry a connection of another platform over', () => {
    expect(brandPlatformConnectionId(pazarama, 'ty-1')).toBe('pz-1')
  })

  it('has none when the platform has no connection', () => {
    expect(brandPlatformConnectionId([], 'ty-1')).toBe('')
  })
})

describe('brandMappingReadState', () => {
  it('reads a failed read as failed, never as no mapping', () => {
    expect(brandMappingReadState({ isSuccess: false, isError: true }, true)).toBe('failed')
  })

  it('knows the mapping only once the read answered, also when it answered none', () => {
    expect(brandMappingReadState({ isSuccess: false, isError: false }, true)).toBe('loading')
    expect(brandMappingReadState({ isSuccess: true, isError: false }, true)).toBe('known')
    expect(brandMappingReadState({ isSuccess: false, isError: false }, false)).toBe('idle')
  })
})
