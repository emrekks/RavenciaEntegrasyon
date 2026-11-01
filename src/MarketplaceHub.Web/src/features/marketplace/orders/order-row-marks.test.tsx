import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { OrderRowMarkIcons, type OrderRowMarks } from './order-row-marks'

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
})
