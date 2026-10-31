import { describe, expect, it } from 'vitest'
import { brandPlatformConnectionId } from './brand-mapping-state'

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
