import { describe, expect, it } from 'vitest'
import { offersProductFields, showsProductFields, parseProductFieldOption, productFieldLabel, productFieldOptionValue } from './product-field-sources'

describe('product field sources', () => {
  it('are offered only for free-text fields on marketplaces that send free text', () => {
    expect(offersProductFields('HEPSIBURADA', true)).toBe(true)
    expect(offersProductFields('N11', true)).toBe(true)
    expect(offersProductFields('TRENDYOL', false)).toBe(false)
    expect(offersProductFields('PAZARAMA', true)).toBe(false)
    // The group shows on every field there, chosen only where free text goes.
    expect(showsProductFields('HEPSIBURADA')).toBe(true)
    expect(showsProductFields('PAZARAMA')).toBe(false)
  })

  it('round-trip through the select value and never read a panel attribute id as one', () => {
    expect(parseProductFieldOption(productFieldOptionValue('MODEL_CODE'))).toBe('MODEL_CODE')
    expect(parseProductFieldOption('field:COLOUR')).toBeNull()
    expect(parseProductFieldOption('0199c8a2-0000-7000-8000-000000000000')).toBeNull()
    expect(productFieldLabel('BARCODE')).toBe('Barkod')
  })
})
