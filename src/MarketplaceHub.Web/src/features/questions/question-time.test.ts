import { describe, expect, it } from 'vitest'
import { questionDeadline, questionDeadlineLabel } from './question-time'

describe('questionDeadline', () => {
  const now = Date.parse('2026-10-05T12:00:00Z')

  it('does not invent a countdown when the platform has no exact deadline', () => {
    expect(questionDeadline(null, now)).toBeNull()
    expect(questionDeadline(undefined, now)).toBeNull()
  })

  it('marks deadlines under 24 hours as urgent and keeps the remaining time exact to minutes', () => {
    expect(questionDeadline('2026-10-06T11:30:00Z', now)).toEqual({ text: '23 saat 30 dakika', urgent: true })
    expect(questionDeadline('2026-10-06T12:00:00Z', now)?.urgent).toBe(false)
  })

  it('reports expired and invalid dates without estimating', () => {
    expect(questionDeadline('2026-10-05T11:00:00Z', now)).toEqual({ text: 'Süre Doldu', urgent: true, expired: true })
    expect(questionDeadline('not-a-date', now)).toBeNull()
  })
})

describe('questionDeadlineLabel', () => {
  const now = Date.parse('2026-10-05T12:00:00Z')

  it('says Süre Doldu once the deadline passed, never "Süre Doldu kaldı" (scan D-11)', () => {
    expect(questionDeadlineLabel(questionDeadline('2026-10-05T11:00:00Z', now)!)).toBe('Süre Doldu')
    expect(questionDeadlineLabel(questionDeadline('2026-10-06T11:30:00Z', now)!)).toBe('23 saat 30 dakika kaldı')
  })
})
