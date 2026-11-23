import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { ExchangeResendTag, exchangeResendStatus, exchangeResendTitle } from './exchange-resend-tag'

afterEach(cleanup)

// Owner, 18 Nov 2026: an order the Yeniden Gönderimler tab lists for an exchange reads "Değişim"; others show nothing.
describe('exchange resend tag', () => {
  it('tags an exchange resend with its package in the hover, and nothing for another order', () => {
    const { host } = render(<><ExchangeResendTag resend={{ packageId: '28717254', cargoProviderName: 'Aras Kargo', cargoTrackingNumber: '7260', shipped: false }} /><ExchangeResendTag resend={null} /></>)
    const tags = host.querySelectorAll('.rr-return-exchange')
    expect(tags).toHaveLength(1)
    expect(tags[0].textContent).toBe('Değişim')
    expect(tags[0].getAttribute('title')).toBe('Değişim paketi 28717254 · Aras Kargo · 7260')
  })

  it('says when only the owner\'s mark lists it, and when the package is with the carrier', () => {
    expect(exchangeResendTitle({ packageId: null, shipped: false })).toContain('Trendyol değişim paketini bildirmedi')
    expect(exchangeResendTitle({ packageId: '1', shipped: true })).toBe('Değişim paketi 1 · kargoya verildi')
  })
})

// Owner, 23 Nov 2026: a tracking number alone is not "handed to the carrier"; the link or the owner's mark is.
describe('exchange resend status', () => {
  it('waits for the carrier until the link comes or the owner marks it', () => {
    expect(exchangeResendStatus({ packageId: '1', cargoTrackingNumber: '7330', shipped: false })).toBe('Kargoya Verilmesi Bekleniyor')
    expect(exchangeResendStatus({ packageId: '1', shipped: true, shippedSource: 'MANUAL' })).toBe('Kargoya Verildi (Elle)')
    expect(exchangeResendStatus({ packageId: '1', shipped: true, shippedSource: 'MARKETPLACE' })).toBe('Kargoya Verildi')
    expect(exchangeResendTitle({ packageId: '1', shipped: true, shippedSource: 'MANUAL' })).toBe('Değişim paketi 1 · kargoya verildi (elle işaretlendi)')
  })
})
