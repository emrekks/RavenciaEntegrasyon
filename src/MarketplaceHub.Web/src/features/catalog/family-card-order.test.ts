import { describe, expect, it } from 'vitest'
import { familyCardsInPanelOrder } from './family-card-order'
import { marketplacePublishLines } from './product-publication-submit'

const card = (productId: string, color: string | null) => ['L', 'XL'].map(size => ({ productId, optionSignature: color ? `Renk: ${color} | Beden: ${size}` : `Beden: ${size}`, options: undefined }))

describe('familyCardsInPanelOrder', () => {
  it('queues the colour cards in the panel colour order, not the opened card first', () => {
    // Live 2026-10-29, MZ035YNC: opened Kahverengi first, then the family's own order.
    const family = [['kahve', 'Kahverengi'], ['acik', 'Açık Kahverengi'], ['laci', 'Lacivert'], ['bordo', 'Bordo'], ['haki', 'Haki'], ['siyah', 'Siyah'], ['gri', 'Gri'], ['murdum', 'Mürdüm']] as const
    const variants = family.flatMap(([id, color]) => card(id, color))

    expect(familyCardsInPanelOrder(family.map(([id]) => id), variants, 'kahve'))
      .toEqual(['acik', 'bordo', 'gri', 'haki', 'kahve', 'laci', 'murdum', 'siyah'])
  })

  it('compares colours the Turkish way without regard to case and keeps colourless cards last in their own order', () => {
    const variants = [...card('b', null), ...card('i', 'ışık'), ...card('s', 'Şeftali'), ...card('a', null), ...card('c', 'çağla')]

    expect(familyCardsInPanelOrder(['b', 'i', 's', 'a', 'c'], variants, 'b')).toEqual(['c', 'i', 's', 'b', 'a'])
  })

  it('reads the opened card from variants without a product id and drops repeated ids', () => {
    const variants = [...card('x', 'Siyah').map(variant => ({ ...variant, productId: null })), ...card('y', 'Bej')]

    expect(familyCardsInPanelOrder(['x', 'y', 'x'], variants, 'x')).toEqual(['y', 'x'])
  })
})

describe('marketplacePublishLines with the colour order', () => {
  const outcome = (failures: Map<string, number[]>, openedCard: number) => ({ platformName: 'Pazarama', platformCode: 'PAZARAMA', familyCards: 8, openedCard, enqueuedCards: 7, enqueuedVariants: 21, skipped: { live: 0, running: 0, matched: 0 }, failures })

  it('names a refused card by its place in the colour order and leaves the opened card unnamed', () => {
    expect(marketplacePublishLines(outcome(new Map([['Kategori eksik.', [0]]]), 4))).toContain('Pazarama: reddedildi — Kategori eksik. (1. renk kartı)')
    expect(marketplacePublishLines(outcome(new Map([['Kategori eksik.', [4]]]), 4))).toContain('Pazarama: reddedildi — Kategori eksik.')
  })
})
