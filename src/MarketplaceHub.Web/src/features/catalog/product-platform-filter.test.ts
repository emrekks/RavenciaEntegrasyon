import { describe, expect, it } from 'vitest'
import { productPlatformFilterGroups } from './product-platform-filter'

describe('product platform status filters', () => {
  it('exposes Hepsiburada beside the other marketplace status filters', () => {
    const hepsiburada = productPlatformFilterGroups.find(group => group.label === 'Hepsiburada')

    expect(hepsiburada?.options.map(option => option.value)).toEqual([
      'HEPSIBURADA:ACTIVE',
      'HEPSIBURADA:PARTIAL',
      'HEPSIBURADA:PASSIVE'
    ])
  })
})
