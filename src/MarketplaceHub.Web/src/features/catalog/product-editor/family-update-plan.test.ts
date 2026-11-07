import { describe, expect, it } from 'vitest'
import { productSaveFeedback } from '../product-publication-submit'
import { familyUpdatePlan, familyUpdateReach, updatesFamilyFromOneCard } from './family-update-plan'

const TRENDYOL = 'conn-ty'
const linked = { platform: 'Trendyol', platformCode: 'TRENDYOL', connectionId: TRENDYOL, isLinked: true, isMatched: true, isMissing: false }
// One colour card of three sizes, on Trendyol or not.
const card = (productId: string, colour: string, onTrendyol: boolean) => ['S', 'M', 'L'].map(size => ({
  productId,
  sku: `${productId}-${size}`,
  optionSignature: `Renk: ${colour} | Beden: ${size}`,
  options: undefined,
  platformStatuses: onTrendyol ? [linked] : []
}))

// RYP003T, live 7 Nov 2026: 7 colour cards of 3 variants each, the editor opened on one of them.
const colours = [['p1', 'Bej'], ['p2', 'Ekru'], ['p3', 'Gri'], ['p4', 'Haki'], ['p5', 'Lacivert'], ['p6', 'Siyah'], ['p7', 'Vizon']] as const

describe('familyUpdatePlan', () => {
  it('sends the product information for every colour card on the marketplace, not only the opened one', () => {
    const variants = colours.flatMap(([id, colour]) => card(id, colour, true))
    const plan = familyUpdatePlan({ openedProductId: 'p7', familyProductIds: colours.map(([id]) => id).filter(id => id !== 'p7'), variants, connectionId: TRENDYOL, platformCode: 'TRENDYOL' })

    expect(plan.send.map(item => item.productId)).toEqual(['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'])
    expect(plan.send.reduce((sum, item) => sum + item.variants, 0)).toBe(21)
    expect(plan.absent).toEqual([])
    expect(familyUpdateReach(plan)).toBe('7 rengin tamamı (21 varyant) güncellenir.')
  })

  it('names the colours the marketplace does not hold instead of skipping them silently', () => {
    const variants = colours.flatMap(([id, colour]) => card(id, colour, id !== 'p1' && id !== 'p6'))
    const plan = familyUpdatePlan({ openedProductId: 'p7', familyProductIds: ['p1', 'p2', 'p3', 'p4', 'p5', 'p6'], variants, connectionId: TRENDYOL, platformCode: 'TRENDYOL' })

    expect(plan.send).toHaveLength(5)
    expect(plan.absent.map(item => item.colour)).toEqual(['Bej', 'Siyah'])
    expect(familyUpdateReach(plan)).toBe('7 renkten 5 renk (15 varyant) güncellenir; bu mağazada olmayan 2 renk gönderilmez: Bej, Siyah.')
  })

  it('does not read a missing variant (NOT_FOUND) as present', () => {
    const variants = [...card('a', 'Bej', true), ...card('b', 'Gri', false).map(variant => ({ ...variant, platformStatuses: [{ ...linked, isMatched: false, isMissing: true }] }))]
    const plan = familyUpdatePlan({ openedProductId: 'a', familyProductIds: ['b'], variants, connectionId: TRENDYOL, platformCode: 'TRENDYOL' })
    expect(plan.absent.map(item => item.productId)).toEqual(['b'])
  })

  it('keeps a product without a family as it was: the opened card goes and the server answers for it', () => {
    const plan = familyUpdatePlan({ openedProductId: 'solo', familyProductIds: [], variants: card('solo', 'Bej', false), connectionId: TRENDYOL, platformCode: 'TRENDYOL' })
    expect(plan.send.map(item => item.productId)).toEqual(['solo'])
    expect(plan.absent).toEqual([])
    expect(familyUpdateReach(plan)).toBe('')
  })

  it('sends one update on Shopify and PTT AVM, whose single product carries the whole family', () => {
    expect(updatesFamilyFromOneCard('shopify')).toBe(true)
    expect(updatesFamilyFromOneCard('PTTAVM')).toBe(true)
    expect(updatesFamilyFromOneCard('TRENDYOL')).toBe(false)
    expect(updatesFamilyFromOneCard('N11')).toBe(false)
    const variants = colours.flatMap(([id, colour]) => card(id, colour, false))
    const plan = familyUpdatePlan({ openedProductId: 'p3', familyProductIds: ['p1', 'p2', 'p4', 'p5', 'p6', 'p7'], variants, connectionId: 'conn-ptt', platformCode: 'PTTAVM' })
    expect(plan.send).toEqual([{ productId: 'p3', colour: 'Gri', variants: 21 }])
    expect(plan.absent).toEqual([])
  })
})

describe('the update result of a model family', () => {
  const save = (updates: Parameters<typeof productSaveFeedback>[0]['updates']) => productSaveFeedback({ created: false, productChanged: false, savedParts: [], versionRetried: false, saveWarnings: [], publishRequested: false, publications: [], publicationErrors: [], updates })

  it('counts every colour and variant that went', () => {
    const feedback = save([{ platformName: 'Trendyol', queued: { productInformation: true, priceInventory: false }, family: { cards: 7, queuedCards: 7, queuedVariants: 21, absent: [], failures: new Map() } }])
    expect(feedback.title).toBe('Aktarım Başladı')
    expect(feedback.lines).toEqual(['Trendyol: ürün bilgisi (7 rengin tamamı, 21 varyant) gönderiliyor.'])
  })

  it('names the colours not on the marketplace and the refused ones, and stays on screen', () => {
    const feedback = save([{ platformName: 'Trendyol', queued: { productInformation: true, priceInventory: false }, family: { cards: 7, queuedCards: 4, queuedVariants: 12, absent: ['Bej', 'Siyah'], failures: new Map([['Ürün güncellenemedi.', ['Vizon']]]) } }])
    expect(feedback).toMatchObject({ title: 'Aktarım Kısmen Başladı', kind: 'error', persistent: true })
    expect(feedback.lines).toEqual([
      'Trendyol: ürün bilgisi (7 renkten 4 renk, 12 varyant) gönderiliyor.',
      'Trendyol: 2 renk bu mağazada yok (Bej, Siyah); bu renkler güncellenmedi. Önce Yayınla ile gönderilmeleri gerekir.',
      'Trendyol: reddedildi — Ürün güncellenemedi. (Vizon)'
    ])
  })

  it('says nothing was started when no colour of the family is on the marketplace', () => {
    const feedback = save([{ platformName: 'Hepsiburada', queued: { productInformation: false, priceInventory: false }, family: { cards: 2, queuedCards: 0, queuedVariants: 0, absent: ['Bej', 'Gri'], failures: new Map() } }])
    expect(feedback.title).toBe('Aktarım Başlatılamadı')
    expect(feedback.lines).toEqual(['Hepsiburada: 2 renk bu mağazada yok (Bej, Gri); bu renkler güncellenmedi. Önce Yayınla ile gönderilmeleri gerekir.'])
  })
})
