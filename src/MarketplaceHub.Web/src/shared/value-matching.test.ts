import { describe, expect, it } from 'vitest'
import { canonicalKey, matchingKey, valueMatchScore } from './value-matching'

describe('value matching', () => {
  it('folds Turkish letters, case and punctuation into one key', () => {
    expect(matchingKey('ÇOK RENKLİ')).toBe('cok renkli')
    expect(matchingKey('cok-renkli')).toBe('cok renkli')
    expect(matchingKey('Siyah / Beyaz')).toBe('siyah beyaz')
    expect(matchingKey('  Kahve,  Rengi ')).toBe('kahve rengi')
  })

  it('knows the clothing synonyms', () => {
    expect(canonicalKey('Black')).toBe('siyah')
    expect(canonicalKey('Navy Blue')).toBe('lacivert')
    expect(canonicalKey('Materyal')).toBe(canonicalKey('Kumaş Tipi'))
    expect(canonicalKey('Mürdüm')).toBe('murdum')
  })

  it('scores from exact names down to shared words', () => {
    expect(valueMatchScore('Siyah', 'SİYAH')).toBe(100)
    expect(valueMatchScore('Çok Renkli', 'Cok-Renkli')).toBe(95)
    expect(valueMatchScore('Black', 'Siyah')).toBe(90)
    expect(valueMatchScore('Laci', 'Lacivert')).toBe(70)
    expect(valueMatchScore('Siyah', 'Açık Siyah')).toBe(50)
    expect(valueMatchScore('Uzun Kol', 'Kol Boyu Uzun')).toBe(30)
    expect(valueMatchScore('Mor', 'Beyaz')).toBe(0)
  })
})
