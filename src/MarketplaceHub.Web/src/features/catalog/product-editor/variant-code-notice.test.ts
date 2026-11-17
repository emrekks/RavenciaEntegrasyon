import { describe, expect, it } from 'vitest'
import { variantCodeNoticeTail } from './variant-code-notice'
import { summarizeVariantCodeIssues, type PlatformCodeIssue } from '../platform-code-rules'

// Owner, 15 Nov 2026: a draft saved without a barcode said "Düzeltilene kadar Kaydet pasif kalır." in the editor while
// Taslak Olarak Kaydet still worked.
describe('the variant code callout of a draft', () => {
  const barcodeIssue = { platformCode: 'TRENDYOL', field: 'barcode', message: 'barkod zorunlu' } as PlatformCodeIssue
  const missingBarcode = summarizeVariantCodeIssues([{ key: 'v1', barcode: '', sku: 'SKU-1' }], new Map([['v1', [barcodeIssue]]]))!

  it('says a draft without a barcode can still be saved and needs the barcode to open for sale', () => {
    expect(missingBarcode.title).toBe('1 varyantta barkod eksik')
    expect(variantCodeNoticeTail(missingBarcode, true, false)).toBe('Taslak yine de kaydedilebilir; ürünü satışa açmak için barkod gerekir.')
    // A code that breaks a marketplace rule does not stop a draft either.
    expect(variantCodeNoticeTail({ ...missingBarcode, ruleRows: 1 }, true, false)).toBe('Taslak yine de kaydedilebilir; ürünü satışa açmak için kodlar düzeltilmeli.')
  })

  it('keeps today\'s text for a card on sale or a new card, and for a draft a stock code stops', () => {
    const today = 'Düzeltilene kadar Kaydet pasif kalır.'
    expect(variantCodeNoticeTail(missingBarcode, false, false)).toBe(today)
    expect(variantCodeNoticeTail(missingBarcode, true, true)).toBe(today)
    expect(variantCodeNoticeTail({ ...missingBarcode, missingSkus: 1 }, true, false)).toBe(today)
    expect(variantCodeNoticeTail({ ...missingBarcode, heldRows: 1 }, true, false)).toBe(today)
    expect(variantCodeNoticeTail({ ...missingBarcode, duplicateRows: 1 }, true, false)).toBe(today)
  })
})
