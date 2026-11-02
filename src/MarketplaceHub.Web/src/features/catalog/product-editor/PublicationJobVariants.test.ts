import { describe, expect, it } from 'vitest'
import { rejectedBarcodes, variantResultReason, variantResultSummary, type JobVariantResult } from './PublicationJobVariants'

const row = (barcode: string, status: string, reason: string | null = null): JobVariantResult => ({ barcode, stockCode: barcode, options: null, status, reason })

describe('publication job variant results', () => {
  it('counts an approval check by outcome and closes with the variants it did not carry', () => {
    const items = [row('A', 'APPROVED'), row('B', 'APPROVED'), row('C', 'REJECTED', 'Görsel eksik'), row('D', 'NOT_SENT')]
    expect(variantResultSummary(items, true)).toBe('3 varyant gönderildi · 2 onaylandı · 1 reddedildi · 0 bekliyor · 1 gönderilmedi')
  })

  it('names a send by what went out and what was refused', () => {
    expect(variantResultSummary([row('A', 'SENT'), row('B', 'REJECTED')], false)).toBe('2 varyant gönderildi · 1 reddedildi')
  })

  it('says a reason is unknown instead of inventing one', () => {
    expect(variantResultReason(row('A', 'NOT_SENT'))).toBe('neden bilinmiyor')
    expect(variantResultReason(row('A', 'REJECTED', 'Kategori hatalı'))).toBe('Kategori hatalı')
    expect(variantResultReason(row('A', 'APPROVED'))).toBeNull()
  })

  it('copies only the refused barcodes', () => {
    expect(rejectedBarcodes([row('A', 'APPROVED'), row('B', 'REJECTED'), row('C', 'REJECTED'), row('D', 'NOT_SENT')])).toEqual(['B', 'C'])
  })
})
