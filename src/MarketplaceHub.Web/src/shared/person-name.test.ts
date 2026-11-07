import { describe, expect, it } from 'vitest'
import { personName } from './person-name'

describe('personName', () => {
  it('capitalises lower-case words with Turkish letters', () => {
    expect(personName('neslihan yurttutaan')).toBe('Neslihan Yurttutaan')
    expect(personName('Melike çağırcı')).toBe('Melike Çağırcı')
    expect(personName('ışıl irem')).toBe('Işıl İrem')
    expect(personName('zahide Ataş')).toBe('Zahide Ataş')
  })
  it('keeps names that are already capitalised or in capitals', () => {
    expect(personName('DSM IGA')).toBe('DSM IGA')
    expect(personName('DSM- CC-Erguvan')).toBe('DSM- CC-Erguvan')
    expect(personName('Merve Gül Işık')).toBe('Merve Gül Işık')
  })
  it('handles empty values', () => {
    expect(personName(null)).toBe('')
    expect(personName('')).toBe('')
  })
})
