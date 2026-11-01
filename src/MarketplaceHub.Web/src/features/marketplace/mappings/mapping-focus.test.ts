import { describe, expect, it } from 'vitest'
import { attributeMappingPlatform, mappingFocusItems, nextMissingEntryKey } from './mapping-focus'

describe('mappingFocusItems', () => {
  it('lists the required fields first, a field in both lists once as required, with the values of its value gaps', () => {
    const items = mappingFocusItems(
      [
        { attribute: 'Kumaş Tipi', detail: 'Eşleme yok.', externalAttributeId: 'k1' },
        { attribute: 'Ürün Görseli', detail: 'Görsel yok.' },
        { attribute: 'Kesim Kalıbı', detail: 'Eşleme yok.', externalAttributeId: 'k2' }
      ],
      [
        { attribute: 'Materyal', detail: '“Kumaş Tipi” alanı eşlenmemiş.', externalAttributeId: 'k1' },
        { attribute: 'Kalınlık', detail: '“Orta” için değer eşlemesi yok.', externalAttributeId: 'k4', localValueId: 'orta' },
        { attribute: 'Kalınlık', detail: '“İnce” için değer eşlemesi yok.', externalAttributeId: 'k4', localValueId: 'ince' },
        { attribute: 'Boy', detail: 'Alan eşlenmemiş.', externalAttributeId: 'k3' }
      ]
    )
    expect(items.map(item => [item.externalAttributeId, item.required])).toEqual([['k1', true], ['k2', true], ['k4', false], ['k3', false]])
    expect(items[0].details).toEqual(['Eşleme yok.', '“Kumaş Tipi” alanı eşlenmemiş.'])
    expect(items[2].localValueIds).toEqual(['orta', 'ince'])
  })
})

describe('nextMissingEntryKey', () => {
  const gaps = ['kumas', 'kesim', 'boy', 'kalinlik']
  it('walks the product gaps in order, then the window’s own next open field, then wraps', () => {
    expect(nextMissingEntryKey(gaps, 'kumas', 'other')).toBe('kesim')
    expect(nextMissingEntryKey(gaps, 'kesim', 'other')).toBe('boy')
    expect(nextMissingEntryKey(gaps, 'boy', 'other')).toBe('kalinlik')
    expect(nextMissingEntryKey(gaps, 'kalinlik', 'other')).toBe('other')
    expect(nextMissingEntryKey(gaps, 'kalinlik', undefined)).toBe('kumas')
  })
  it('goes back to the first gap from a field outside them', () => {
    expect(nextMissingEntryKey(gaps, 'extra', 'other')).toBe('kumas')
  })
})

describe('attributeMappingPlatform', () => {
  it('knows the marketplaces whose category fields the window maps', () => {
    expect(attributeMappingPlatform(' hepsiburada ')).toBe('HEPSIBURADA')
    expect(attributeMappingPlatform('PTTAVM')).toBeNull()
  })
})
