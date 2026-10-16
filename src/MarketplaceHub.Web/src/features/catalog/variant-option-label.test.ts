import { describe, expect, it } from 'vitest'
import { titleCaseTr, variantOptionParts } from './variant-option-label'

describe('variant option label', () => {
  it('splits the signature and capitalises Turkish words', () => {
    expect(variantOptionParts('BEDEN=L|RENK=AÇıK KAHVERENGI')).toEqual([{ name: 'Beden', value: 'L' }, { name: 'Renk', value: 'Açık Kahverengi' }])
  })
  it('keeps sizes upper case', () => {
    expect(titleCaseTr('2xl')).toBe('2XL')
    expect(titleCaseTr('İLKBAHAR/YAZ')).toBe('İlkbahar/Yaz')
  })
  it('ignores empty or broken parts', () => {
    expect(variantOptionParts('')).toEqual([])
    expect(variantOptionParts('BEDEN=|RENK')).toEqual([])
  })
})
