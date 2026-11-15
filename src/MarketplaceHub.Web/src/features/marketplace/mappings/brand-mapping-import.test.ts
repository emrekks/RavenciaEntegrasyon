import { describe, expect, it } from 'vitest'
import { brandMappingImportMessage, importBrandMappingItems } from './brand-mapping-import'

describe('brand mapping backup import', () => {
  const items = [{ localId: 'a', externalId: '1' }, { localId: 'b', externalId: '2' }, { localId: 'c', externalId: '3' }]

  it('goes on past a refused mapping and counts what was saved, skipped and refused', async () => {
    const sent: string[] = []
    const result = await importBrandMappingItems(items, {
      target: item => item.localId === 'c' && item.externalId === 'none' ? null : item.localId,
      nameOf: item => `Marka ${item.localId.toUpperCase()}`,
      send: async (item, localId) => { if (localId === 'b') throw new Error('Sürüm değişti.'); sent.push(localId) }
    })
    expect(sent.sort()).toEqual(['a', 'c'])
    expect(result).toEqual({ mapped: 2, skipped: 0, failures: [{ name: 'Marka B', message: 'Sürüm değişti.' }] })
  })

  it('skips a mapping with no target brand or marketplace value', async () => {
    const result = await importBrandMappingItems(items, { target: item => item.localId === 'a' ? null : item.localId, nameOf: item => item.localId, send: async () => undefined })
    expect(result).toEqual({ mapped: 2, skipped: 1, failures: [] })
  })

  it('names what was done and the first refused brands', () => {
    expect(brandMappingImportMessage({ created: 1, brandFailures: [], mapped: 119, skipped: 0, failures: [{ name: 'Marka X', message: 'Sürüm değişti.' }] }))
      .toBe('1 marka oluşturuldu, 119 marka eşleştirmesi kaydedildi. 1 eşleştirme kaydedilemedi: Marka X (Sürüm değişti.).')
    expect(brandMappingImportMessage({ created: 0, brandFailures: [], mapped: 3, skipped: 2, failures: [] }))
      .toBe('0 marka oluşturuldu, 3 marka eşleştirmesi kaydedildi. 2 eşleştirme güncel referansta bulunamadığı için atlandı.')
  })
})
