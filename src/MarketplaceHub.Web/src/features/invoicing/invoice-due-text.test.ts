import { describe, expect, it } from 'vitest'
import { invoiceDueText } from './invoice-due-text'

const now = Date.parse('2026-10-26T12:00:00Z')

describe('invoice due text', () => {
  it('counts the whole days left and names the day', () => {
    expect(invoiceDueText('2026-10-28T20:59:59Z', now)).toEqual({ text: '2 gün kaldı · 28.10.2026', critical: false })
  })

  it('counts hours and turns red under a day', () => {
    expect(invoiceDueText('2026-10-27T05:30:00Z', now)).toEqual({ text: '17 saat kaldı · 27.10.2026', critical: true })
    expect(invoiceDueText('2026-10-26T12:20:00Z', now)).toEqual({ text: '1 saat kaldı · 26.10.2026', critical: true })
  })

  it('says a passed deadline is over', () => {
    expect(invoiceDueText('2026-10-25T12:00:00Z', now)).toEqual({ text: 'Süre doldu · 25.10.2026', critical: true })
  })
})
