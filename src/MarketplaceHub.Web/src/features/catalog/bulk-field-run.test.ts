import { describe, expect, it } from 'vitest'
import { bulkFieldChunks, bulkFieldIssue, bulkFieldOptions, bulkFieldPayloadValue, bulkFieldResultMessage, bulkFieldSummary, bulkFieldValueLabel, runBulkFieldChunks } from './bulk-field-run'

describe('Toplu Değer Ata', () => {
  it('offers the owner’s fields in his order, KDV once', () => {
    expect(bulkFieldOptions.map(option => option.label)).toEqual(['KDV Oranı', 'KDV Dahil mi', 'Desi', 'Garanti Süresi (Ay)', 'Teslimat Süresi (Gün)', 'Satış Durumu', 'Marka', 'Kategori', 'Güvenlik Stoğu', 'Açıklama'])
  })

  it('checks each value as the product editor does', () => {
    expect(bulkFieldIssue('desi', '0,5')).toBeNull()
    expect(bulkFieldIssue('desi', '0.5')).toBeNull()
    expect(bulkFieldIssue('desi', '0')).not.toBeNull()
    expect(bulkFieldIssue('desi', '0,555')).not.toBeNull()
    expect(bulkFieldIssue('desi', '')).not.toBeNull()
    expect(bulkFieldIssue('warrantyMonths', '0')).toBeNull()
    expect(bulkFieldIssue('warrantyMonths', '241')).not.toBeNull()
    expect(bulkFieldIssue('warrantyMonths', '1.5')).not.toBeNull()
    expect(bulkFieldIssue('deliveryDays', '0')).not.toBeNull()
    expect(bulkFieldIssue('deliveryDays', '30')).toBeNull()
    expect(bulkFieldIssue('safetyStock', '0')).toBeNull()
    expect(bulkFieldIssue('safetyStock', '-1')).not.toBeNull()
    expect(bulkFieldIssue('vatRate', '18')).not.toBeNull()
    expect(bulkFieldIssue('vatRate', '10')).toBeNull()
    expect(bulkFieldIssue('vatRate', '8')).toBeNull()
    expect(bulkFieldIssue('brandId', '')).not.toBeNull()
    expect(bulkFieldIssue('description', '<p> </p>')).not.toBeNull()
    expect(bulkFieldIssue('description', '<p>Pamuklu</p>')).toBeNull()
  })

  it('sends a decimal comma as a point and the description cleaned', () => {
    expect(bulkFieldPayloadValue('desi', '0,5')).toBe('0.5')
    expect(bulkFieldPayloadValue('description', '<p onclick="x()">Yeni</p><script>alert(1)</script>')).toBe('<p>Yeni</p>')
  })

  it('names what will be written in one line', () => {
    expect(bulkFieldSummary(3, 12, 'desi', bulkFieldValueLabel('desi', '0.5'))).toBe('3 ürün (12 varyant) için Desi = 0,5 atanacak')
    expect(bulkFieldValueLabel('brandId', 'b1', { brands: [{ id: 'b1', name: 'Ravencia' }] })).toBe('Ravencia')
    expect(bulkFieldValueLabel('status', 'ARCHIVED')).toBe('Satışa Kapalı')
    expect(bulkFieldValueLabel('warrantyMonths', '0')).toBe('0 (garanti yok)')
  })

  it('keeps a model in one request', async () => {
    const chunks = bulkFieldChunks([['a1', 'a2'], ['b1'], ['c1', 'c2', 'c3']], 3)
    expect(chunks).toEqual([['a1', 'a2', 'b1'], ['c1', 'c2', 'c3']])
    const failed = await runBulkFieldChunks(chunks, async chunk => { if (chunk.includes('c1')) throw new Error('Sunucu hatası.') })
    expect(failed.doneIds).toEqual(['a1', 'a2', 'b1'])
    expect(failed.remainingIds).toEqual(['c1', 'c2', 'c3'])
  })

  it('lists the models it could not write by their codes', () => {
    const done = { doneIds: ['a'], remainingIds: [], error: null }
    expect(bulkFieldResultMessage(done, { updatedCount: 4, unchangedCount: 0, variantCount: 16, failures: [] }, 'desi'))
      .toEqual({ title: 'Desi Atandı', message: '4 ürün kartı güncellendi (16 varyant). Pazar yerlerine bir şey gönderilmedi.', kind: 'success' })
    const partial = bulkFieldResultMessage(done, { updatedCount: 1, unchangedCount: 1, variantCount: 4, failures: [{ model: 'RAB01', message: 'Taslak ürün kartı.' }] }, 'status')
    expect(partial.kind).toBe('error')
    expect(partial.title).toBe('Satış Durumu Kısmen Atandı')
    expect(partial.message).toContain('RAB01: Taslak ürün kartı.')
  })
})
