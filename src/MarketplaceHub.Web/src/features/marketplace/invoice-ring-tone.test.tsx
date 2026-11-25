import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { ReturnInvoiceRing } from './shared'

afterEach(cleanup)

const toneOf = (status: string, isMicroExport = false) => {
  const { host } = render(<ReturnInvoiceRing status={status} isMicroExport={isMicroExport} />)
  return [...host.querySelector('.rr-ring')!.classList].find(name => name.startsWith('rr-tone-'))
}

// Scan C-7: a state that waits for the seller (correction, review, unknown or unread E-Faturam answer) read grey like a
// cancelled invoice; it reads amber, also on a micro-export order (where pending states read blue).
describe('the invoice ring tone', () => {
  it('reads amber for the states that wait for the seller', () => {
    for (const status of ['FATURA_DUZELTME_GEREKLI', 'ADJUSTMENT_REQUIRED', 'FATURA_INCELEME_GEREKLI', 'FATURA_BELIRSIZ', 'UNKNOWN_RESULT', 'FATURA_CEVAP_BEKLENIYOR']) {
      expect(toneOf(status), status).toBe('rr-tone-wait')
      expect(toneOf(status, true), `${status} micro-export`).toBe('rr-tone-wait')
    }
  })

  it('keeps the other tones', () => {
    expect(toneOf('FATURA_BEKLIYOR')).toBe('rr-tone-wait')
    expect(toneOf('FATURA_BEKLIYOR', true)).toBe('rr-tone-move')
    expect(toneOf('FATURA_YUKLENDI')).toBe('rr-tone-ok')
    expect(toneOf('FATURA_REDDEDILDI')).toBe('rr-tone-bad')
    expect(toneOf('FATURA_IPTAL')).toBe('rr-tone-off')
  })
})
