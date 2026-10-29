import { describe, expect, it } from 'vitest'
import { invoicePreviewNotice } from './invoice-preview-notice'

// Audit 29 Oct 2026, W-6: an order with a manual invoice document was reported as missing an E-Faturam connection.
describe('invoice preview notice', () => {
  it('says an order with a manual document was skipped, not that a connection is missing', () => {
    const notice = invoicePreviewNotice([], ['#123'])
    expect(notice).toEqual({ text: 'Manuel fatura belgesi zaten kayıtlı olduğu için atlandı: #123.', kind: 'info' })
    expect(notice?.text).not.toContain('E-Faturam')
  })

  it('names the orders without a connection apart from those with a manual document', () => {
    expect(invoicePreviewNotice(['#7 (PRODUCTION)'], ['#123'])).toEqual({
      text: 'Aynı ortamda etkin ve API bilgileri kayıtlı E-Faturam bağlantısı bulunamadı: #7 (PRODUCTION). Manuel fatura belgesi zaten kayıtlı olduğu için atlandı: #123.',
      kind: 'error',
    })
  })

  it('shortens a long list and says nothing when every order goes on', () => {
    expect(invoicePreviewNotice(['#1', '#2', '#3', '#4', '#5', '#6'], [])?.text).toBe('Aynı ortamda etkin ve API bilgileri kayıtlı E-Faturam bağlantısı bulunamadı: #1, #2, #3, #4 ve 2 kayıt daha.')
    expect(invoicePreviewNotice([], [])).toBeNull()
  })
})
