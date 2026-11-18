import { describe, expect, it } from 'vitest'
import { panelPriceOfferNotice, panelPriceOfferSummary, type PanelPriceOfferPreview } from './panel-price-offers'

const preview = (values: Partial<PanelPriceOfferPreview>): PanelPriceOfferPreview => ({
  connectionId: 'c-hb', platformCode: 'HEPSIBURADA', opensOffers: true, automaticWritesOpen: false, linkedWithoutOffer: 0, openable: 0, missingPanelPrice: 0, missingVat: 0,
  following: 0, followingAtOtherPrice: 0, followingWithoutPanelPrice: 0, customPrice: 0, differences: [], ...values
})

describe('panel price offers (owner rule, 18 Nov 2026)', () => {
  it('tells the switch confirmation how many offers open and that nothing is sent now', () => {
    expect(panelPriceOfferNotice(preview({ linkedWithoutOffer: 3137, openable: 3000, missingPanelPrice: 130, missingVat: 7 }))).toEqual([
      '3.000 beden için panel fiyatıyla teklif açılacak; şimdi pazaryerine bir şey gönderilmeyecek; bedenin bir sonraki stok gönderiminde stokla birlikte geçerli fiyat da gider.',
      'Panel fiyatı olmayan 130 beden gönderime alınmadı.',
      'KDV oranı olmayan 7 beden gönderime alınmadı.'
    ])
    // Live on 18 Nov: no panel price anywhere, so nothing opens and every size is named as left out.
    expect(panelPriceOfferNotice(preview({ linkedWithoutOffer: 3133, missingPanelPrice: 3133 }))).toEqual(['Panel fiyatı olmayan 3.133 beden gönderime alınmadı.'])
    expect(panelPriceOfferNotice(preview({}))).toEqual([])
    expect(panelPriceOfferNotice(null)).toEqual(['Panel fiyatıyla açılacak teklif sayısı okunamadı.'])
  })

  it('says a connection that opens no offers (not a marketplace store) opens none', () => {
    expect(panelPriceOfferNotice(preview({ platformCode: 'TRENDYOL_EFATURAM', opensOffers: false, linkedWithoutOffer: 12, openable: 0, missingPanelPrice: 2 }))).toEqual([
      '12 bağlı bedenin bu bağlantıda teklifi yok; bu bağlantıda panel fiyatıyla teklif açılmaz.'
    ])
  })

  it('offers "Eksik Teklifleri Aç" only when something can open', () => {
    expect(panelPriceOfferSummary(preview({ linkedWithoutOffer: 5, openable: 4, missingPanelPrice: 1 }))).toEqual({ text: 'Teklifsiz bağlı 5 beden · 4 panel fiyatıyla açılabilir · 1 panel fiyatı yok', canOpen: true })
    expect(panelPriceOfferSummary(preview({ linkedWithoutOffer: 5, missingPanelPrice: 5 }))?.canOpen).toBe(false)
    expect(panelPriceOfferSummary(preview({ followingAtOtherPrice: 2 }))).toEqual({ text: 'panel fiyatından farklı 2 teklif', canOpen: false })
    expect(panelPriceOfferSummary(preview({}))).toBeNull()
  })
})
