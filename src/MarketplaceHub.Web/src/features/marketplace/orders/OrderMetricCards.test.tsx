import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { microCountryRows, OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })
const newRows = [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', units: 36 }, { key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 1 }]
const props = (extra: Partial<Parameters<typeof OrderMetricCards>[0]> = {}) => ({ newOrders: metric(12), newUnits: 37, newRows, processing: metric(1234), late: metric(2), microExport: metric(186), microCountries: { SA: 55, BG: 40, RO: 33, AZ: 24, GR: 22, AE: 12 }, cancelled: metric(68), cancelledUnits: 75, partiallyCancelled: metric(1), partialUnits: 2, resent: metric(3), onOpenDay: () => undefined, ...extra })
const card = (host: HTMLElement, title: string) => Array.from(host.querySelectorAll('article')).find(item => item.querySelector('small')?.textContent === title)!
const open = (host: HTMLElement, title: string) => card(host, title).querySelector<HTMLButtonElement>('.rr-metric-open')!.click()

describe('order summary cards', () => {
  it('shows six cards in one grid, a title and a package count each, with their products on Yeni Sipariş', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const row = host.querySelector('.rr-metrics.rr-metrics-6')!
    const cards = Array.from(row.querySelectorAll('article')).map(item => [item.querySelector('small')?.textContent, item.querySelector('strong')?.textContent])
    expect(cards).toEqual([
      ['Yeni Sipariş', '12 Paket | 37 Adet Ürün'],
      ['İşleme Alınan', `${(1234).toLocaleString('tr-TR')} Paket`],
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
    // While the data loads, the chip and the hover panels are not drawn.
    expect(host.querySelector('.rr-metric-chip')).toBeNull()
    expect(host.querySelector('.rr-metric-tip')).toBeNull()
    cleanup()
    // An older API: the packages without their products.
    const older = render(<OrderMetricCards {...props({ newUnits: undefined, newRows: undefined, cancelledUnits: undefined, partialUnits: undefined })} />).host
    expect(card(older, 'Yeni Sipariş').querySelector('strong')!.textContent).toBe('12 Paket')
    expect(older.querySelector('.rr-metric-chip')!.textContent).toBe('1 Kısmi İptal')
  })

  it('opens the day lists from the cancelled count and its chip, and the unshipped resends from their card', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'İptal')
    const chip = host.querySelector<HTMLButtonElement>('.rr-metric-chip')!
    expect(chip.textContent).toBe('1 Kısmi İptal | 2 Adet')
    chip.click()
    open(host, 'Yeniden Gönderim')
    open(host, 'Mikro İhracat Sipariş')
    expect(openDay.mock.calls.map(call => call[0])).toEqual(['cancelled', 'partial', 'resent', 'micro'])
  })

  it('keeps the cancelled count and its pulsing chip together, and draws no chip when no order is partly cancelled', () => {
    const { host } = render(<OrderMetricCards {...props()} />)
    const cancelled = card(host, 'İptal')
    expect(cancelled.classList.contains('rr-metric-chipped')).toBe(true)
    expect(Array.from(cancelled.children).map(child => child.className)).toEqual(['rr-metric-open', 'rr-metric-chip rr-metric-chip-alert'])
    cleanup()

    const plain = render(<OrderMetricCards {...props({ partiallyCancelled: metric(0) })} />).host
    expect(plain.querySelector('.rr-metric-chip')).toBeNull()
    expect(card(plain, 'İptal').classList.contains('rr-metric-chipped')).toBe(false)
    expect(plain.querySelectorAll('.rr-metric-open')).toHaveLength(3)
  })

  it('lists the countries of the pending micro-export orders on hover, the biggest first, and opens their list', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'Mikro İhracat Sipariş')
    expect(openDay.mock.calls).toEqual([['micro']])
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
    expect(classes(warning)['Geciken']).toBe('rr-bad rr-alert')
    expect(classes(warning)['İptal']).toBe('rr-bad rr-metric-link')
    expect(classes(warning)['Yeni Sipariş']).toBe('rr-warn rr-metric-tipped')
    cleanup()
    const quiet = render(<OrderMetricCards {...props({ late: metric(0), cancelled: metric(0), partiallyCancelled: metric(0), newRows: [] })} />).host
    expect(classes(quiet)['Geciken']).toBe('')
    expect(classes(quiet)['İptal']).toBe('rr-metric-link')
    expect(classes(quiet)['Yeni Sipariş']).toBe('rr-warn')
    cleanup()
    // While the summary loads nothing warns.
    const loading = render(<OrderMetricCards {...props({ late: metric(undefined, true), cancelled: metric(undefined, true), partiallyCancelled: metric(0) })} />).host
    expect(classes(loading)['Geciken']).toBe('')
    expect(classes(loading)['İptal']).toBe('rr-metric-link')
  })

  it('puts the orders of no known country last and draws no panel without countries', () => {
    expect(microCountryRows({ '': 4, SA: 2, BG: 2, RO: 0 })).toEqual([['BG', 2], ['SA', 2], ['', 4]])
    expect(microCountryRows(undefined)).toEqual([])
    const { host } = render(<OrderMetricCards {...props({ microExport: metric(0), microCountries: {} })} />)
    expect(card(host, 'Mikro İhracat Sipariş').querySelector('.rr-metric-tip')).toBeNull()
    // Without the panel a native title says what it counts.
    expect(card(host, 'Mikro İhracat Sipariş').querySelector('.rr-metric-open')!.getAttribute('title')).toBe('Kargoya verilmemiş mikro ihracat siparişleri')
  })
})
