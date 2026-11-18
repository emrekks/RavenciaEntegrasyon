import { describe, expect, it } from 'vitest'
import { panelPriceFillConfirmLines, panelPriceFillSummary, type PanelPriceFillPreview } from './panel-price-fill'

const preview = (values: Partial<PanelPriceFillPreview>): PanelPriceFillPreview => ({
  withoutPanelPrice: 3721, fillable: 3617, zeroPrice: 104, withoutOffer: 0, fillableBySource: { TRENDYOL: 3617 }, sample: [],
  offerChanges: [{ platformCode: 'PTTAVM', offers: 468, totalDifference: 46_800, medianDifference: 100, largestDifference: 100 }], ...values
})

describe('panel price fill (owner decision, 18 Nov 2026)', () => {
  it('names how many sizes fill from which marketplace', () => {
    expect(panelPriceFillSummary(preview({}))).toBe('Panel fiyatı boş 3.721 beden: 3.617 tanesi ilk pazaryeri fiyatından doldurulabilir (TRENDYOL 3.617).')
    expect(panelPriceFillSummary(preview({ fillable: 0 }))).toBeNull()
  })

  it('says what stays empty, which offers move and that nothing is sent now', () => {
    expect(panelPriceFillConfirmLines(preview({}))).toEqual([
      '3.617 bedenin panel fiyatı, ilk çekildiği pazaryerinin fiyatıyla doldurulacak; dolu panel fiyatlarına dokunulmaz.',
      'İlk pazaryeri fiyatı 0 TL olan 104 beden boş kalacak.',
      "PTTAVM: Özel Fiyat'ı kapalı 468 teklifin kayıtlı fiyatı panel fiyatına çekilecek (toplam fark 46.800 TL, ortanca 100 TL, en büyük 100 TL).",
      'Şimdi pazaryerine hiçbir şey gönderilmeyecek; fiyat, bedenin bir sonraki stok ya da fiyat gönderiminde gider.'
    ])
  })
})
