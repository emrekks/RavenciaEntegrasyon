import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { BuyerTypeTag, buyerTypeLabel, OrderRowMarkIcons, unreadableLinesLabel, type OrderRowMarks } from './order-row-marks'

afterEach(cleanup)

const none: OrderRowMarks = { microExport: false, microExportCountryCode: null, deliveryPoint: false, deliveryPointType: null, fastDeliveryType: null }
const tips = (host: HTMLElement) => Array.from(host.querySelectorAll('.rr-omark')).map(mark => [mark.getAttribute('data-tip'), mark.getAttribute('aria-label')])

describe('order row marks', () => {
  it('shows the flag, the delivery point and the fast delivery icons, each with its explanation', () => {
    const { host } = render(<OrderRowMarkIcons marks={{ microExport: true, microExportCountryCode: 'RO', deliveryPoint: true, deliveryPointType: null, fastDeliveryType: 'SameDayShipping' }} />)

    expect(tips(host)).toEqual([
      ['Mikro İhracat · Romanya', 'Mikro İhracat · Romanya'],
      ['Teslimat Noktası Siparişi', 'Teslimat Noktası Siparişi'],
      ['Aynı Gün Kargo', 'Aynı Gün Kargo'],
    ])
    expect(host.querySelector('.rr-omark-micro .rr-omark-flag')).not.toBeNull()
    expect(host.querySelector('img')).toBeNull()
  })

  it.each([
    ['TodayDelivery', 'Bugün Teslimat'],
    ['SameDayShipping', 'Aynı Gün Kargo'],
    ['FastDelivery', 'Hızlı Teslimat'],
  ])('names the fast delivery type %s', (type, label) => {
    const { host } = render(<OrderRowMarkIcons marks={{ ...none, fastDeliveryType: type }} />)
    expect(tips(host)).toEqual([[label, label]])
  })

  it('shows nothing when the order has no marks or the fast delivery value is unknown', () => {
    expect(render(<OrderRowMarkIcons marks={null} />).host.querySelector('.rr-omarks')).toBeNull()
    expect(render(<OrderRowMarkIcons marks={undefined} />).host.querySelector('.rr-omarks')).toBeNull()
    expect(render(<OrderRowMarkIcons marks={none} />).host.querySelector('.rr-omarks')).toBeNull()
    expect(render(<OrderRowMarkIcons marks={{ ...none, fastDeliveryType: 'SomethingNew' }} />).host.querySelector('.rr-omarks')).toBeNull()
  })

  it('shows only the marks that are set', () => {
    const { host } = render(<OrderRowMarkIcons marks={{ ...none, deliveryPoint: true, deliveryPointType: 'KTN' }} />)
    expect(tips(host)).toEqual([['Teslimat Noktası Siparişi', 'Teslimat Noktası Siparişi']])
    expect(host.querySelector('.rr-omark-micro, .rr-omark-fast')).toBeNull()
  })

  it('keeps the micro export mark without a flag when the country is unknown, and names a country it has no drawing for by its code', () => {
    const unknown = render(<OrderRowMarkIcons marks={{ ...none, microExport: true }} />).host
    expect(tips(unknown)).toEqual([['Mikro İhracat', 'Mikro İhracat']])
    expect(unknown.querySelector('.rr-omark-flag')).toBeNull()
    cleanup()
    const other = render(<OrderRowMarkIcons marks={{ ...none, microExport: true, microExportCountryCode: 'FR' }} />).host
    expect(tips(other)).toEqual([['Mikro İhracat · FR', 'Mikro İhracat · FR']])
  })

  it.each([
    ['SA', 'Suudi Arabistan'], ['AZ', 'Azerbaycan'], ['BG', 'Bulgaristan'], ['RO', 'Romanya'], ['GR', 'Yunanistan'], ['AE', 'Birleşik Arap Emirlikleri'],
  ])('draws the %s flag for the storefront countries', (code, name) => {
    const { host } = render(<OrderRowMarkIcons marks={{ ...none, microExport: true, microExportCountryCode: code }} />)
    expect(tips(host)).toEqual([[`Mikro İhracat · ${name}`, `Mikro İhracat · ${name}`]])
    expect(host.querySelector('.rr-omark-flag')).not.toBeNull()
  })

  // Every marketplace: a product line that could not be read marks the order, with or without other marks.
  it('marks an order with unreadable product lines, first, and says no invoice is issued', () => {
    const alone = render(<OrderRowMarkIcons marks={null} unreadableLines={2} />).host
    expect(tips(alone)).toEqual([[unreadableLinesLabel(2), unreadableLinesLabel(2)]])
    expect(unreadableLinesLabel(2)).toContain('Okunamayan Ürün Satırı (2)')
    expect(unreadableLinesLabel(2)).toContain('fatura kesilmez')
    // PTT AVM sends no order total: the amount is the read lines' sum and the tip says it may be short.
    expect(unreadableLinesLabel(2)).not.toContain('Tutar eksik olabilir')
    expect(unreadableLinesLabel(2, true)).toContain('Tutar eksik olabilir')
    const ptt = render(<OrderRowMarkIcons marks={null} unreadableLines={1} amountMayBeIncomplete />).host
    expect(tips(ptt)[0][0]).toBe(unreadableLinesLabel(1, true))
    cleanup()
    const withOthers = render(<OrderRowMarkIcons marks={{ ...none, deliveryPoint: true }} unreadableLines={1} />).host
    expect(tips(withOthers).map(([tip]) => tip)).toEqual([unreadableLinesLabel(1), 'Teslimat Noktası Siparişi'])
    cleanup()
    expect(render(<OrderRowMarkIcons marks={none} unreadableLines={0} />).host.querySelector('.rr-omarks')).toBeNull()
  })
})

// The buyer kind by the invoice's rule, on every marketplace: one tag, its tone, the reason as the tooltip.
describe('buyer type tag', () => {
  it.each([
    ['Individual', 'Pazaryeri bireysel olarak işaretledi', 'Bireysel', 'rr-buyer-individual'],
    ['Corporate', 'Vergi numarası var', 'Kurumsal', 'rr-buyer-corporate'],
    ['CorporateTaxIdMissing', 'Şirket adı var, vergi numarası yok — fatura kesilirken sorulacak', 'Kurumsal · Vergi No Yok', 'rr-buyer-missing'],
  ])('shows %s as its label with the reason', (kind, reason, label, tone) => {
    const { host } = render(<BuyerTypeTag buyerType={{ kind, reason }} />)
    const tag = host.querySelector('.rr-buyer-tag')
    expect(tag?.textContent).toBe(label)
    expect(tag?.classList.contains(tone)).toBe(true)
    expect(tag?.getAttribute('title')).toBe(`${label}: ${reason}`)
    expect(tag?.getAttribute('data-tip')).toBe(`${label}: ${reason}`)
    expect(buyerTypeLabel(kind)).toBe(label)
  })

  it('shows nothing when the buyer type is missing or unknown', () => {
    expect(render(<BuyerTypeTag buyerType={null} />).host.querySelector('.rr-buyer-tag')).toBeNull()
    expect(render(<BuyerTypeTag buyerType={undefined} />).host.querySelector('.rr-buyer-tag')).toBeNull()
    expect(render(<BuyerTypeTag buyerType={{ kind: 'Other', reason: '' }} />).host.querySelector('.rr-buyer-tag')).toBeNull()
    expect(buyerTypeLabel(null)).toBeNull()
  })
})
