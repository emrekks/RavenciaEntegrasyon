import { describe, expect, it } from 'vitest'
import { attributeIssueTip } from './publish-issue-chips'

// The live Hepsiburada Kazak case (2 Nov 2026): Kumaş Tipi required and unmapped; the panel's Materyal would feed it.
describe('attributeIssueTip', () => {
  it('names the marketplace field and the panel attribute it would map from', () => {
    expect(attributeIssueTip({ attribute: 'Kumaş Tipi', detail: '', state: 'unmapped', panelAttribute: 'Materyal' })).toEqual(['Kumaş Tipi eşlenmemiş', 'Eşlenebilecek panel özelliği: Materyal'])
    // A warning named after the panel attribute still names the marketplace field.
    expect(attributeIssueTip({ attribute: 'Materyal', detail: '', state: 'unmapped', panelAttribute: 'Materyal', fieldName: 'Kumaş Tipi' })).toEqual(['Kumaş Tipi eşlenmemiş', 'Eşlenebilecek panel özelliği: Materyal'])
    expect(attributeIssueTip({ attribute: 'Boy', detail: '', state: 'unmapped', panelAttribute: 'Boy', fieldName: 'Boy' })).toEqual(['Boy eşlenmemiş', 'Aynı adlı panel özelliğiyle eşlenebilir'])
  })
  it('says an outdated mapping must be checked again, and a value gap leaves the values to the chip', () => {
    expect(attributeIssueTip({ attribute: 'Yaka Stili', detail: '', state: 'stale', panelAttribute: 'Yaka Tipi' })).toEqual(['Eşleme eski kategori listesine göre, yeniden doğrulanmalı', 'Kayıtlı panel özelliği: Yaka Tipi'])
    expect(attributeIssueTip({ attribute: 'Kalınlık', detail: '“Orta” için değer yok.', state: 'values' })).toEqual([])
    expect(attributeIssueTip({ attribute: 'Kategori eşlemesi', detail: 'Seçilmemiş.' })).toEqual(['Seçilmemiş.'])
  })
})
