import { describe, expect, it } from 'vitest'
import { requiredFieldsSkipHref, requiredFieldsSkipNotice, type RequiredFieldsSkipped } from './required-fields-skip'
import { bulkStatusResultMessage } from './bulk-status-run'
import { bulkFieldResultMessage } from './bulk-field-run'
import { importTemplateColumns, importTemplateCsv, importTemplateFields } from './imports/import-template'

const card = (productId: string, title: string, modelCode: string | null, missingFields: string[]): RequiredFieldsSkipped => ({ productId, title, modelCode, missingFields })

describe('required fields skip notice (owner, 13 Nov 2026: "Eksik olanı atlasın, bildirsin bildirimlerde")', () => {
  it('names the skipped cards with what they miss and leads to the closed cards', () => {
    const notice = requiredFieldsSkipNotice([card('p1', 'Triko Bluz', 'MZ051', ['Marka', 'Desi']), card('p2', 'Keten Gömlek', 'KG10', ['Barkod'])])
    expect(notice?.message).toBe('2 ürün eksik alan nedeniyle satışa açılmadı: Triko Bluz (MZ051): Marka, Desi; Keten Gömlek (KG10): Barkod. Ürün düzenleme ekranından tamamlayıp tekrar deneyin.')
    expect(notice?.details).toEqual([
      { label: 'Triko Bluz (MZ051)', value: 'Eksik: Marka, Desi', tone: 'error' },
      { label: 'Keten Gömlek (KG10)', value: 'Eksik: Barkod', tone: 'error' }
    ])
    expect(notice?.target).toEqual({ href: '/products?status=ARCHIVED', label: 'Eksik Ürünleri Göster' })
  })

  it('opens one model straight away and counts the cards it does not name', () => {
    const skipped = ['S', 'M', 'L', 'XL'].map((size, index) => card(`p${index}`, `Bluz ${size}`, 'MZ051', ['Desi']))
    expect(requiredFieldsSkipHref(skipped)).toBe('/products?status=ARCHIVED&q=MZ051')
    expect(requiredFieldsSkipNotice(skipped)?.message).toContain('Bluz L (MZ051): Desi ve 1 ürün daha.')
    expect(requiredFieldsSkipHref([card('a', 'A', 'MZ051', ['Desi']), card('b', 'B', null, ['Desi'])])).toBe('/products?status=ARCHIVED')
  })

  it('has nothing to say when nothing was skipped', () => {
    expect(requiredFieldsSkipNotice([])).toBeNull()
  })

  it('counts the skipped cards apart from the opened ones in Toplu Satışa Aç', () => {
    const result = { doneIds: ['a', 'b', 'c'], remainingIds: [], error: null }
    expect(bulkStatusResultMessage('Aktif', result, 1)).toEqual({ title: 'Ürün Durumları Kısmen Güncellendi', message: '2 ürün “Aktif” olarak güncellendi.', kind: 'error' })
    expect(bulkStatusResultMessage('Aktif', result, 3).title).toBe('Ürün Durumları Güncellenemedi')
    expect(bulkStatusResultMessage('Aktif', result).kind).toBe('success')
  })

  it('carries the skipped cards of Toplu Değer Ata › Satış Durumu into its notification', () => {
    const outcome = bulkFieldResultMessage({ doneIds: ['a', 'b'], remainingIds: [], error: null }, { updatedCount: 1, unchangedCount: 0, variantCount: 5, failures: [], skipped: [card('b', 'Eski Kapalı', null, ['Marka', 'Desi'])] }, 'status')
    expect(outcome.title).toBe('Satış Durumu Kısmen Atandı')
    expect(outcome.kind).toBe('error')
    expect(outcome.message).toContain('1 ürün eksik alan nedeniyle satışa açılmadı: Eski Kapalı: Marka, Desi.')
    expect(outcome.skipped?.target.href).toBe('/products?status=ARCHIVED')
    const clean = bulkFieldResultMessage({ doneIds: ['a'], remainingIds: [], error: null }, { updatedCount: 1, unchangedCount: 0, variantCount: 5, failures: [], skipped: [] }, 'status')
    expect(clean.kind).toBe('success')
    expect(clean.skipped).toBeUndefined()
  })
})

describe('classic import template', () => {
  it('offers Garanti Süresi (Ay) and Teslimat Süresi (Gün) with their mapping', () => {
    expect(importTemplateColumns.slice(-2)).toEqual([{ header: 'Garanti Süresi (Ay)', field: 'warrantyMonths' }, { header: 'Teslimat Süresi (Gün)', field: 'deliveryDays' }])
    expect(importTemplateFields.split(',')).toContain('deliveryDays')
    expect(importTemplateCsv().charCodeAt(0)).toBe(0xfeff)
    expect(importTemplateCsv().slice(1)).toMatch(/^Ürün Adı;Stok Kodu;.*;Garanti Süresi \(Ay\);Teslimat Süresi \(Gün\)\r\n$/)
  })
})
