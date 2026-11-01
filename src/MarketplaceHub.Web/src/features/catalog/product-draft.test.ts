import { describe, expect, it } from 'vitest'
import { canSaveAsDraft, draftBlockingIssues, draftMissingSummary, draftProductTitle, editorStatusFor, isDraftStatus, variantCodeRulesStopSave } from './product-draft'

describe('product drafts', () => {
  it('offers Taslak Olarak Kaydet on a new card and on a draft, never on a card on sale or closed', () => {
    expect(canSaveAsDraft(false, undefined)).toBe(true)
    expect(canSaveAsDraft(true, 'DRAFT')).toBe(true)
    expect(canSaveAsDraft(true, 'ACTIVE')).toBe(false)
    expect(canSaveAsDraft(true, 'ARCHIVED')).toBe(false)
    expect(canSaveAsDraft(true, undefined)).toBe(false)
    expect(isDraftStatus(' draft ')).toBe(true)
  })

  it('starts a draft form on Satışa Açık, the status the normal save gives it', () => {
    expect(editorStatusFor('DRAFT')).toBe('ACTIVE')
    expect(editorStatusFor('ARCHIVED')).toBe('ARCHIVED')
    expect(editorStatusFor('')).toBe('ACTIVE')
  })

  it('names a draft by its name, else its model code, else Taslak Ürün with the Istanbul date', () => {
    const now = new Date('2026-11-01T11:05:00Z')
    expect(draftProductTitle(' Elbise ', 'MZ1', now)).toBe('Elbise')
    expect(draftProductTitle('', ' MZ1 ', now)).toBe('MZ1')
    expect(draftProductTitle('', '', now)).toBe('Taslak Ürün 01.11.2026 14:05')
  })

  it('never makes up a stock code: a row without one stops the draft with what to do', () => {
    const row = { sku: '', barcode: '', optionSignature: 'Renk:Mavi', salePrice: 0, listPrice: 0 }
    const issues = draftBlockingIssues([row, { ...row, sku: '  ', optionSignature: 'Renk:Kırmızı' }, { ...row, sku: 'A', optionSignature: 'Renk:Yeşil' }], { listPrice: '0', salePrice: '0' })
    expect(issues).toHaveLength(1)
    expect(issues[0]).toContain('Stok kodu boş 2 varyant satırı var')
  })

  it('stops a draft only on clashing or malformed values, never on a missing one', () => {
    const row = { sku: 'A', barcode: '', optionSignature: 'Tek Ürün', salePrice: 0, listPrice: 0 }
    expect(draftBlockingIssues([row], { listPrice: '', salePrice: '' })).toEqual([])
    expect(draftBlockingIssues([row, { ...row, optionSignature: 'Renk:Mavi' }], { listPrice: '0', salePrice: '0' })).toEqual(['Stok kodları benzersiz olmalıdır.'])
    expect(draftBlockingIssues([{ ...row, barcode: 'x1' }, { ...row, sku: 'B', barcode: 'X1', optionSignature: 'Renk:Mavi' }], { listPrice: '0', salePrice: '0' })).toEqual(['Barkodlar benzersiz olmalıdır.'])
    expect(draftBlockingIssues([row], { listPrice: '10', salePrice: '20' })).toHaveLength(1)
  })

  it('lets Taslak Olarak Kaydet through red variant codes and stops every other save', () => {
    expect(variantCodeRulesStopSave(true, '2 varyantta kod hatası')).toBe(false)
    expect(variantCodeRulesStopSave(false, '2 varyantta kod hatası')).toBe(true)
    expect(variantCodeRulesStopSave(false, '')).toBe(false)
  })

  it('lists at most three missing items and counts the rest', () => {
    expect(draftMissingSummary('')).toEqual({ count: 0, lines: [] })
    expect(draftMissingSummary('Açıklama zorunludur. Desi sıfırdan büyük olmalıdır. Marka zorunludur. Görsel ekleyin.')).toEqual({ count: 4, lines: ['Açıklama zorunludur.', 'Desi sıfırdan büyük olmalıdır.', 'Marka zorunludur.', 've 1 eksik daha'] })
  })
})
