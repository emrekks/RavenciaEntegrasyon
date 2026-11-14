import { describe, expect, it } from 'vitest'
import { knownJobTypes } from './job-categories'
import { jobPresentation, jobSubtitle, jobTitle, jobTypeCode } from './job-presentation'

describe('job presentation', () => {
  it('names the platform first and says which way the data goes', () => {
    expect(jobTitle('N11_ORDER_SYNC', 'N11')).toBe('N11 Yeni Siparişler')
    expect(jobSubtitle('N11_ORDER_SYNC')).toBe('Okuma · Yeni Sipariş Çekme')
    expect(jobTitle('INVOICE_SUBMIT', 'E-Faturam')).toBe('E-Faturam Fatura Kesimi')
    expect(jobSubtitle('INVOICE_SUBMIT')).toBe('Yazma · Fatura Oluşturma')
  })

  it('takes the platform of the connection for shared types and leaves the panel own work without one', () => {
    expect(jobTitle('ORDER_STATUS_SYNC', 'Shopify')).toBe('Shopify Açık Sipariş Durumları')
    expect(jobTitle('IMPORT_APPLY', 'Ravencia')).toBe('Dosya İçe Aktarma')
    expect(jobTitle('HEPSIBURADA_ORDER_SYNC')).toBe('Hepsiburada Yeni Siparişler')
    expect(jobTitle('PTTAVM_CONNECTION_TEST')).toBe('PttAVM Bağlantı Testi')
    expect(jobSubtitle('PTTAVM_CONNECTION_TEST')).toBe('Okuma · Bağlantı Testi')
  })

  it('describes every known job type in its own words', () => {
    for (const type of knownJobTypes) {
      const presentation = jobPresentation(type)
      expect(presentation.action, type).not.toMatch(/_/)
      expect(presentation.description, type).not.toContain('ayrıntılar için kaydı açabilirsiniz')
    }
  })

  it('never shows a Hepsiburada test order as a Trendyol job', () => {
    expect(jobTitle('TRENDYOL_STAGE_TEST_ORDER', 'Hepsiburada')).toBe('Hepsiburada Test Siparişi')
    expect(jobTypeCode('TRENDYOL_STAGE_TEST_ORDER', 'Hepsiburada')).toBe('STAGE_TEST_ORDER')
    expect(jobTypeCode('TRENDYOL_STAGE_TEST_ORDER', 'Trendyol')).toBe('TRENDYOL_STAGE_TEST_ORDER')
    expect(jobTypeCode('PAZARAMA_ORDER_SYNC', 'Pazarama')).toBe('PAZARAMA_ORDER_SYNC')
    expect(jobPresentation('TRENDYOL_STAGE_TEST_ORDER').description).not.toContain('Trendyol')
  })
})

describe('reference sync names the list it reads', () => {
  it('tells the category job from the brand job by the payload resource type', () => {
    expect(jobTitle('TRENDYOL_REFERENCE_SYNC', 'Trendyol', 'CATEGORIES')).toBe('Trendyol Kategori Listesi')
    expect(jobSubtitle('TRENDYOL_REFERENCE_SYNC', 'CATEGORIES')).toBe('Okuma · Kategori ve Özellik Listesi Çekme')
    expect(jobTitle('TRENDYOL_REFERENCE_SYNC', 'Trendyol', 'BRANDS')).toBe('Trendyol Marka Listesi')
    expect(jobSubtitle('TRENDYOL_REFERENCE_SYNC', 'BRANDS')).toBe('Okuma · Marka Listesi Çekme')
    expect(jobPresentation('TRENDYOL_REFERENCE_SYNC', 'BRANDS').description).toContain('marka')
    for (const resource of ['ATTRIBUTES', 'CATEGORY_ATTRIBUTES', 'ATTRIBUTE_VALUES']) expect(jobSubtitle('N11_REFERENCE_SYNC', resource)).toBe('Okuma · Kategori Özellikleri Çekme')
    expect(jobTitle('TRENDYOL_REFERENCE_SYNC', 'Trendyol')).toBe('Trendyol Kategori Listesi')
    expect(jobTitle('TRENDYOL_ORDER_SYNC', 'Trendyol', 'BRANDS')).toBe('Trendyol Yeni Siparişler')
  })

  it('gives every job a plain Turkish title, not the generic area word', () => {
    for (const type of knownJobTypes) expect(jobPresentation(type).title, type).not.toMatch(/İşlemi$|_/)
    expect(jobTitle('TRENDYOL_ORDER_RECOVERY_SYNC', 'Trendyol')).toBe('Trendyol Sipariş Geçmişi')
    expect(jobTitle('MARKETPLACE_QUESTION_SYNC', 'Trendyol')).toBe('Trendyol Müşteri Soruları')
    expect(jobTitle('HEPSIBURADA_FINANCE_SYNC', 'Hepsiburada')).toBe('Hepsiburada Hakediş Kayıtları')
    expect(jobTitle('PRICE_INVENTORY_SYNC')).toBe('Fiyat ve Stok Gönderimi')
  })
})

describe('jobTypesForDirection', () => {
  it('lists read jobs under Okuma and write jobs under Yazma, never both', async () => {
    const { jobTypesForDirection, jobPresentation } = await import('./job-presentation')
    const read = jobTypesForDirection('Okuma'); const write = jobTypesForDirection('Yazma')
    expect(read).toContain('N11_ORDER_SYNC')
    expect(read.length).toBeGreaterThan(0); expect(write.length).toBeGreaterThan(0)
    expect(read.some(type => write.includes(type))).toBe(false)
    expect(write.every(type => jobPresentation(type).direction === 'Yazma')).toBe(true)
  })

  it('names an unknown job type from its words with English casing, never the Turkish dotted and dotless i', () => {
    expect(jobPresentation('SOME_NEW_INVOICE_SYNC').action).toBe('Some New Invoice Sync')
    expect(jobPresentation('INDEX_ISSUE_CHECK').action).toBe('Index Issue Check')
  })
})
