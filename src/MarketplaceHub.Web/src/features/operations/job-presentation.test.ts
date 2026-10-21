import { describe, expect, it } from 'vitest'
import { knownJobTypes } from './job-categories'
import { jobPresentation, jobSubtitle, jobTitle, jobTypeCode } from './job-presentation'

describe('job presentation', () => {
  it('names the platform first and says which way the data goes', () => {
    expect(jobTitle('N11_ORDER_SYNC', 'N11')).toBe('N11 Sipariş İşlemi')
    expect(jobSubtitle('N11_ORDER_SYNC')).toBe('Okuma · Yeni Sipariş Çekme')
    expect(jobTitle('INVOICE_SUBMIT', 'E-Faturam')).toBe('E-Faturam Fatura İşlemi')
    expect(jobSubtitle('INVOICE_SUBMIT')).toBe('Yazma · Fatura Oluşturma')
  })

  it('takes the platform of the connection for shared types and leaves the panel own work without one', () => {
    expect(jobTitle('ORDER_STATUS_SYNC', 'Shopify')).toBe('Shopify Sipariş İşlemi')
    expect(jobTitle('IMPORT_APPLY', 'Ravencia')).toBe('İçe Aktarım İşlemi')
    expect(jobTitle('HEPSIBURADA_ORDER_SYNC')).toBe('Hepsiburada Sipariş İşlemi')
    expect(jobTitle('PTTAVM_CONNECTION_TEST')).toBe('PTT AVM Bağlantı İşlemi')
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
    expect(jobTitle('TRENDYOL_STAGE_TEST_ORDER', 'Hepsiburada')).toBe('Hepsiburada Sipariş İşlemi')
    expect(jobTypeCode('TRENDYOL_STAGE_TEST_ORDER', 'Hepsiburada')).toBe('STAGE_TEST_ORDER')
    expect(jobTypeCode('TRENDYOL_STAGE_TEST_ORDER', 'Trendyol')).toBe('TRENDYOL_STAGE_TEST_ORDER')
    expect(jobTypeCode('PAZARAMA_ORDER_SYNC', 'Pazarama')).toBe('PAZARAMA_ORDER_SYNC')
    expect(jobPresentation('TRENDYOL_STAGE_TEST_ORDER').description).not.toContain('Trendyol')
  })
})
