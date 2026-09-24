import { describe, expect, it } from 'vitest'
import { formatPanelColorValue, usesCustomPanelColorValue } from './color-value-format'

describe('panel color custom values', () => {
  it.each([
    ['BORDO', 'Bordo'],
    ['AÇIK KREM', 'Açık Krem'],
    ['kİREMİT rENGİ', 'Kiremit Rengi'],
    ['  çok renkli  ', 'Çok Renkli'],
  ])('title-cases %s as %s for Trendyol', (source, expected) => {
    expect(formatPanelColorValue(source)).toBe(expected)
  })

  it('uses panel colors for ordinary color fields, never Web Color', () => {
    expect(usesCustomPanelColorValue('Renk')).toBe(true)
    expect(usesCustomPanelColorValue('[TDG] Renk')).toBe(true)
    expect(usesCustomPanelColorValue('[A-TDG] Renk')).toBe(true)
    expect(usesCustomPanelColorValue('Color')).toBe(true)
    expect(usesCustomPanelColorValue('Web Color')).toBe(false)
  })
})
