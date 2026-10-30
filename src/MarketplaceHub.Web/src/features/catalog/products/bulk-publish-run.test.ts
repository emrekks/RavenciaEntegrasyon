import { describe, expect, it } from 'vitest'
import { addBulkPublicationResult, bulkPublishChunks, bulkPublishFailureDetails, bulkPublishMissingVat, bulkPublishRequestBody, bulkPublishResultMessage, type BulkPublicationFailure } from './bulk-publish-run'

const record = (id: string, modelCode: string | null, offerIds: Array<string | null> = ['offer']) => ({ id, title: `Bluz ${id}`, modelCode, variants: offerIds.map(offerId => ({ modelCode, offerId })) }) as never

describe('bulk publish slices', () => {
  it('keeps the colour cards of a model family in one slice', () => {
    const records = [record('a1', 'MZ036YNC'), record('b1', 'MZ050YNC'), record('a2', 'mz036ync'), record('b2', 'MZ050YNC'), record('c1', null)]
    expect(bulkPublishChunks(['a1', 'b1', 'a2', 'b2', 'c1'], records, 3)).toEqual([['a1', 'a2'], ['b1', 'b2', 'c1']])
  })

  it('gives a family larger than a slice a slice of its own and keeps every id once', () => {
    const records = ['x1', 'x2', 'x3', 'x4'].map(id => record(id, 'MZ053TCD'))
    const chunks = bulkPublishChunks(['y', 'x1', 'x2', 'x3', 'x4', 'z'], records, 2)
    expect(chunks).toEqual([['y'], ['x1', 'x2', 'x3', 'x4'], ['z']])
  })

  it('treats a record the list does not hold as a family of its own', () => {
    expect(bulkPublishChunks(['u1', 'u2', 'u3'], [], 2)).toEqual([['u1', 'u2'], ['u3']])
  })
})

describe('bulk publish VAT', () => {
  const withVat = (base: object, vatRate: number | null) => ({ ...base, vatRate }) as never

  it('counts records whose card has no KDV Oranı and no marketplace offer to take one from', () => {
    expect(bulkPublishMissingVat([withVat(record('a', 'M', [null, null]), null), withVat(record('b', 'M', [null]), 10), withVat(record('c', 'M', ['o1', null]), null), null])).toBe(1)
    expect(bulkPublishMissingVat([withVat(record('a', 'M', [null]), 20)])).toBe(0)
    expect(bulkPublishMissingVat(null)).toBe(0)
  })

  it('never sends a VAT rate: the server takes it from the product card', () => {
    const body = JSON.parse(bulkPublishRequestBody(['p1', 'p2'], ['c1']))
    expect(body).toEqual({ productIds: ['p1', 'p2'], connectionIds: ['c1'] })
    expect(body).not.toHaveProperty('vatRate')
  })
})

describe('bulk publish result', () => {
  const connections = [{ id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol' }, { id: 'n11', platformCode: 'N11', displayName: '' }, { id: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada' }]
  const failure = (productId: string, connectionId: string, message: string, productTitle = 'Penye Bluz'): BulkPublicationFailure => ({ productId, productTitle, connectionId, code: 'CHANNEL_OFFER_REQUIRED', message })

  it('names each refused record once per reason with the stores that refused it', () => {
    const failed = [failure('p1', 'ty', 'KDV yok'), failure('p1', 'n11', 'KDV yok'), failure('p1', 'hb', 'Kategori eşleşmedi'), failure('p2', 'ty', 'KDV yok', 'Bluz p2')]
    expect(bulkPublishFailureDetails(failed, connections, [{ id: 'p1', title: 'Penye Bluz', modelCode: 'MZ036YNC' }])).toEqual([
      { label: 'Penye Bluz (MZ036YNC)', value: 'Trendyol, n11: KDV yok', tone: 'error' },
      { label: 'Penye Bluz (MZ036YNC)', value: 'Hepsiburada: Kategori eşleşmedi', tone: 'error' },
      { label: 'Bluz p2', value: 'Trendyol: KDV yok', tone: 'error' }
    ])
  })

  it('names a deleted record and caps the rows', () => {
    const failed = Array.from({ length: 5 }, (_, index) => failure(`p${index}`, 'ty', 'Ürün kartı bulunamadı', ''))
    const rows = bulkPublishFailureDetails(failed, connections, [], 3)
    expect(rows).toHaveLength(3)
    expect(rows[0].label).toBe('Ürün Kartı')
    expect(rows[2]).toEqual({ label: 'Diğerleri', value: '3 kayıt daha reddedildi.', tone: 'error' })
  })

  it('sums the slices and says what was queued, skipped and refused', () => {
    const total = addBulkPublicationResult({ queued: 2, skipped: 1, failed: [] }, { queued: 4, skipped: 0, failed: [failure('p1', 'ty', 'x'), failure('p1', 'n11', 'x')] })
    expect(total.queued).toBe(6)
    expect(bulkPublishResultMessage(total)).toBe('6 yayın işi kuyruğa alındı, 1 kayıt zaten yayında veya işlemde, 1 kayıt 2 gönderimde reddedildi.')
    expect(bulkPublishResultMessage({ queued: 0, skipped: 0, failed: [] }, true)).toBe('0 yayın işi kuyruğa alındı. Gönderim yarıda kesildi; kalanlar gönderilmedi.')
  })
})
