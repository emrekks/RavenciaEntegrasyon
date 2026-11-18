import { describe, expect, it } from 'vitest'
import { shortRemaining } from './return-remaining'

describe('shortRemaining', () => {
  it('keeps days and hours when a day or more is left', () => expect(shortRemaining('1 gün 20 saat 44 dakika kaldı')).toBe('1 g 20 sa kaldı'))
  it('keeps hours and minutes under a day', () => expect(shortRemaining('20 saat 44 dakika kaldı')).toBe('20 sa 44 dk kaldı'))
  it('keeps minutes under an hour', () => expect(shortRemaining('44 dakika kaldı')).toBe('44 dk kaldı'))
  it('leaves other texts untouched', () => expect(shortRemaining('Süre doldu')).toBe('Süre doldu'))
})
