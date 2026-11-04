import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { cancelRowText, microCountryRows, OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })
const newRows = [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', units: 36 }, { key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 1 }]
const props = (extra: Partial<Parameters<typeof OrderMetricCards>[0]> = {}) => ({ newOrders: metric(12), newUnits: 37, newRows, processing: metric(1234), processingUnits: 2, processingRows: [{ key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 2 }], late: metric(2), microExport: metric(186), microCountries: { SA: 55, BG: 40, RO: 33, AZ: 24, GR: 22, AE: 12 }, cancelled: metric(68), cancelledUnits: 75, partiallyCancelled: metric(1), partialUnits: 2, resent: metric(3), onOpenDay: () => undefined, ...extra })
const card = (host: HTMLElement, title: string) => Array.from(host.querySelectorAll('article')).find(item => item.querySelector('small')?.textContent === title)!
const open = (host: HTMLElement, title: string) => card(host, title).querySelector<HTMLButtonElement>('.rr-metric-open')!.click()

describe('order summary cards', () => {
  it('shows six cards in one grid, a title and a package count each, with their products on Yeni Sipariş and İşleme Alınan', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const row = host.querySelector('.rr-metrics.rr-metrics-6')!
    const cards = Array.from(row.querySelectorAll('article')).map(item => [item.querySelector('small')?.textContent, item.querySelector('strong')?.textContent])
    expect(cards).toEqual([
      ['Yeni Sipariş', '12 Paket | 37 Adet Ürün'],
      ['İşleme Alınan', `${(1234).toLocaleString('tr-TR')} Paket | 2 Adet Ürün`],
      ['Geciken', '2 Paket'],
      ['Mikro İhracat Sipariş', '186 Paket'],
      ['İptal', '68 Paket'],
      ['Yeniden Gönderim', '3 Paket']
    ])
    // Neither Kargoda nor Kargolanacak Ürün Adedi is a card any more.
    expect(card(host, 'Kargoda')).toBeUndefined()
    expect(card(host, 'Kargolanacak Ürün Adedi')).toBeUndefined()
  })

  it('waits for the summary, reads a missing count as zero and never shows unknown units as 0', () => {
    const { host } = render(<OrderMetricCards {...props({ newOrders: metric(undefined, true), newUnits: undefined, newRows: undefined, processing: metric(undefined, true), late: metric(undefined), microExport: metric(undefined, true), microCountries: undefined, cancelled: metric(undefined, true), cancelledUnits: undefined, partiallyCancelled: metric(undefined, true), resent: metric(undefined, true) })} />)

    const values = Array.from(host.querySelectorAll('article strong')).map(value => value.textContent)
    expect(values).toEqual(['…', '…', '0 Paket', '…', '…', '…'])
    // While the data loads, the chip is not drawn and the hover panels list no store (only their empty row).
    expect(host.querySelector('.rr-metric-chip')).toBeNull()
    expect(host.querySelectorAll('.rr-metric-tip-r:not(.is-empty)')).toHaveLength(0)
    expect(host.querySelectorAll('.rr-metric-tip-r.is-empty')).toHaveLength(4)
    cleanup()
    // An older API: the packages without their products.
    const older = render(<OrderMetricCards {...props({ newUnits: undefined, newRows: undefined, processingUnits: undefined, processingRows: undefined, cancelledUnits: undefined, partialUnits: undefined })} />).host
    expect(card(older, 'Yeni Sipariş').querySelector('strong')!.textContent).toBe('12 Paket')
    expect(card(older, 'İşleme Alınan').querySelector('strong')!.textContent).toBe(`${(1234).toLocaleString('tr-TR')} Paket`)
    expect(older.querySelector('.rr-metric-chip')!.textContent).toBe('1 Kısmi İptal')
  })

  it('never switches the list: clicking İptal, its chip, Yeniden Gönderim or Mikro İhracat does not open a day list', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'İptal')
    const chip = host.querySelector<HTMLButtonElement>('.rr-metric-chip')!
    expect(chip.textContent).toBe('1 Kısmi İptal | 2 Adet')
    chip.click()
    open(host, 'Yeniden Gönderim')
    open(host, 'Mikro İhracat Sipariş')
    expect(openDay).not.toHaveBeenCalled()
  })

  it('keeps the cancelled count and its pulsing chip together, and draws no chip when no order is partly cancelled', () => {
    const { host } = render(<OrderMetricCards {...props()} />)
    const cancelled = card(host, 'İptal')
    expect(cancelled.classList.contains('rr-metric-chipped')).toBe(true)
    // The decorative background icon comes first, then the open button, the chip and the hover panel.
    expect(Array.from(cancelled.children).map(child => child.className)).toEqual(['rv-card-bg', 'rr-metric-open', 'rr-metric-chip rr-metric-chip-alert', 'rr-metric-tip'])
    cleanup()

    const plain = render(<OrderMetricCards {...props({ partiallyCancelled: metric(0) })} />).host
    expect(plain.querySelector('.rr-metric-chip')).toBeNull()
    expect(card(plain, 'İptal').classList.contains('rr-metric-chipped')).toBe(false)
    expect(plain.querySelectorAll('.rr-metric-open')).toHaveLength(3)
  })

  it('lists the countries of the pending micro-export orders on hover, the biggest first, without opening their list', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'Mikro İhracat Sipariş')
    expect(openDay).not.toHaveBeenCalled()
    const micro = card(host, 'Mikro İhracat Sipariş')
    // No day heading: the card counts the pending orders, whatever day they were placed.
    expect(micro.querySelector('.rr-metric-tip-h')).toBeNull()
    // The panel says it; a native title would open over it.
    expect(micro.querySelector('.rr-metric-open')!.getAttribute('title')).toBeNull()
    const rows = Array.from(micro.querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent, row.querySelector('svg.rr-omark-flag') !== null])
    expect(rows).toEqual([
      ['Suudi Arabistan', '55', true], ['Bulgaristan', '40', true], ['Romanya', '33', true],
      ['Azerbaycan', '24', true], ['Yunanistan', '22', true], ['Birleşik Arap Emirlikleri', '12', true]
    ])
  })

  it('lists the processing orders\' products per store on hover like Yeni Sipariş', () => {
    const { host } = render(<OrderMetricCards {...props()} />)
    const processing = card(host, 'İşleme Alınan')
    expect(processing.className).toBe('rr-warn rr-metric-tipped')
    expect(Array.from(processing.querySelectorAll('.rr-metric-tip-r')).map(row => row.textContent)).toEqual(['Pazarama2 adet'])
    cleanup()
    expect(card(render(<OrderMetricCards {...props({ processingRows: [] })} />).host, 'İşleme Alınan').className).toBe('rr-warn')
  })

  it('lists the new orders\' products per store on hover, and draws no panel without them', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const fresh = card(host, 'Yeni Sipariş')
    expect(fresh.classList.contains('rr-metric-tipped')).toBe(true)
    expect(fresh.getAttribute('tabindex')).toBe('0')
    expect(Array.from(fresh.querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent])).toEqual([['Trendyol', '36 adet'], ['Pazarama', '1 adet']])
    cleanup()
    const none = render(<OrderMetricCards {...props({ newRows: [] })} />).host
    expect(card(none, 'Yeni Sipariş').querySelector('.rr-metric-tip')).toBeNull()
    expect(card(none, 'Yeni Sipariş').getAttribute('tabindex')).toBeNull()
  })

  it('shows the unshipped resends as a bare count, like the other cards', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    expect(card(host, 'Yeniden Gönderim').querySelector('.rr-metric-note')).toBeNull()
  })

  // Geciken warns only above 0: a red number and a softly pulsing red box (rr-alert); at 0 it is as plain as the other
  // cards. İptal keeps just its red number above 0 and is plain at 0. Yeni Sipariş keeps its amber tone either way.
  it('warns with Geciken and İptal only above zero', () => {
    const classes = (host: HTMLElement) => Object.fromEntries(Array.from(host.querySelectorAll('article')).map(item => [item.querySelector('small')?.textContent, item.className]))
    const warning = render(<OrderMetricCards {...props({ late: metric(3), cancelled: metric(68), partiallyCancelled: metric(0) })} />).host
    expect(classes(warning)['Geciken']).toBe('rr-bad rr-alert rr-metric-tipped')
    expect(classes(warning)['İptal']).toBe('rr-bad rr-metric-link rr-metric-tipped')
    expect(classes(warning)['Yeni Sipariş']).toBe('rr-warn rr-metric-tipped')
    cleanup()
    const quiet = render(<OrderMetricCards {...props({ late: metric(0), cancelled: metric(0), partiallyCancelled: metric(0), newRows: [] })} />).host
    expect(classes(quiet)['Geciken']).toBe('rr-metric-tipped')
    expect(classes(quiet)['İptal']).toBe('rr-metric-link rr-metric-tipped')
    expect(classes(quiet)['Yeni Sipariş']).toBe('rr-warn')
    cleanup()
    // While the summary loads nothing warns.
    const loading = render(<OrderMetricCards {...props({ late: metric(undefined, true), cancelled: metric(undefined, true), partiallyCancelled: metric(0) })} />).host
    expect(classes(loading)['Geciken']).toBe('rr-metric-tipped')
    expect(classes(loading)['İptal']).toBe('rr-metric-link rr-metric-tipped')
  })

  it('puts the orders of no known country last and shows only the empty row without countries', () => {
    expect(microCountryRows({ '': 4, SA: 2, BG: 2, RO: 0 })).toEqual([['BG', 2], ['SA', 2], ['', 4]])
    expect(microCountryRows(undefined)).toEqual([])
    const { host } = render(<OrderMetricCards {...props({ microExport: metric(0), microCountries: {} })} />)
    const micro = card(host, 'Mikro İhracat Sipariş')
    expect(Array.from(micro.querySelectorAll('.rr-metric-tip-r')).map(row => [row.className, row.textContent])).toEqual([['rr-metric-tip-r is-empty', 'Bugün mikro ihracat paketi yok']])
    expect(micro.classList.contains('rr-metric-tipped')).toBe(true)
    // Without countries a native title says what it counts.
    expect(card(host, 'Mikro İhracat Sipariş').querySelector('.rr-metric-open')!.getAttribute('title')).toBe('Bugün gelen, kargoya verilmemiş mikro ihracat siparişleri')
  })

  it('lists the stores of Geciken, İptal and Yeniden Gönderim on hover while they count any', () => {
    const lateRows = [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', packages: 2 }]
    const resentRows = [{ key: 'hb', code: 'HEPSIBURADA', name: 'Hepsiburada', packages: 3 }]
    const cancelledRows = [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', packages: 68, units: 75, partial: 0, partialUnits: 0 }, { key: 'hb', code: 'HEPSIBURADA', name: 'Hepsiburada', packages: 0, units: 0, partial: 1, partialUnits: 2 }]
    const tip = (host: HTMLElement, title: string) => Array.from(card(host, title).querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent])
    const { host } = render(<OrderMetricCards {...props({ lateRows, resentRows, cancelledRows })} />)
    expect(tip(host, 'Geciken')).toEqual([['Trendyol', '2 paket']])
    expect(tip(host, 'Yeniden Gönderim')).toEqual([['Hepsiburada', '3 paket']])
    expect(tip(host, 'İptal')).toEqual([['Trendyol', '68 paket · 75 adet'], ['Hepsiburada', '1 kısmi · 2 adet']])
    expect(card(host, 'Geciken').className).toBe('rr-bad rr-alert rr-metric-tipped')
    expect(card(host, 'İptal').className).toBe('rr-bad rr-metric-link rr-metric-chipped rr-metric-tipped')
    // The panel says it; a native title would open over it.
    expect(card(host, 'İptal').querySelector('.rr-metric-open')!.getAttribute('title')).toBeNull()
    cleanup()
    // At 0 a card lists no store: the panel is still there with its one empty row.
    const quiet = render(<OrderMetricCards {...props({ late: metric(0), resent: metric(0), cancelled: metric(0), partiallyCancelled: metric(0), lateRows, resentRows, cancelledRows })} />).host
    for (const title of ['Geciken', 'İptal', 'Yeniden Gönderim']) {
      expect(card(quiet, title).querySelector('.rr-metric-tip')).not.toBeNull()
      expect(Array.from(card(quiet, title).querySelectorAll('.rr-metric-tip-r')).map(row => [row.className, row.textContent])).toEqual([['rr-metric-tip-r is-empty', 'Bugün bu kartta paket yok']])
    }
    expect(card(quiet, 'Geciken').getAttribute('tabindex')).toBe('0')
  })

  it('marks the card whose list is open as selected, and clicking it still does not call onOpenDay', () => {
    const onOpenDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay, activeCard: 'resent' })} />)
    expect(card(host, 'Yeniden Gönderim').classList.contains('rr-metric-on')).toBe(true)
    expect(card(host, 'Yeniden Gönderim').querySelector('.rr-metric-open')!.getAttribute('aria-pressed')).toBe('true')
    expect(card(host, 'İptal').classList.contains('rr-metric-on')).toBe(false)
    expect(card(host, 'İptal').querySelector('.rr-metric-open')!.getAttribute('aria-pressed')).toBe('false')
    open(host, 'Yeniden Gönderim')
    expect(onOpenDay).not.toHaveBeenCalled()
    cleanup()
    // The Kısmi İptal list selects its chip and the İptal card it sits on.
    const partial = render(<OrderMetricCards {...props({ activeCard: 'partial' })} />).host
    expect(card(partial, 'İptal').classList.contains('rr-metric-on')).toBe(true)
    expect(partial.querySelector('.rr-metric-chip')!.classList.contains('rr-metric-on')).toBe(true)
    expect(card(partial, 'İptal').querySelector('.rr-metric-open')!.getAttribute('aria-pressed')).toBe('false')
  })

  it('writes an İptal store row with its partly cancelled orders and leaves unknown units out', () => {
    expect(cancelRowText({ packages: 2, units: 3, partial: 1, partialUnits: 1 })).toBe('2 paket · 3 adet, 1 kısmi · 1 adet')
    expect(cancelRowText({ packages: 2, units: undefined, partial: 0, partialUnits: undefined })).toBe('2 paket')
  })
})
