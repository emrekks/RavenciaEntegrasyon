import { describe, expect, it } from 'vitest'
import { brandMappingReadState, brandPlatformConnectionId } from './brand-mapping-state'

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
