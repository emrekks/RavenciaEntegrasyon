import { describe, expect, it } from 'vitest'
import { confirmationMatches } from './confirmation'

describe('confirmationMatches', () => {
  it('ignores the case of the letters and the spaces at the ends', () => {
    expect(confirmationMatches('verileri SIL ', 'Verileri sil')).toBe(true)
    expect(confirmationMatches('VERİLERİ SİL', 'Verileri sil')).toBe(true)
    expect(confirmationMatches(' hepsiburada', 'Hepsiburada')).toBe(true)
    expect(confirmationMatches('HEPSİBURADA', 'Hepsiburada')).toBe(true)
  })
  it('does not accept another word or an empty field', () => {
    expect(confirmationMatches('Verileri', 'Verileri sil')).toBe(false)
    expect(confirmationMatches('', 'Trendyol')).toBe(false)
    expect(confirmationMatches('   ', '')).toBe(false)
  })
})
