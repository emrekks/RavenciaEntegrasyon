import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { microCountryRows, OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })
const shipRows = [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', units: 36 }, { key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 1 }]
const props = (extra: Partial<Parameters<typeof OrderMetricCards>[0]> = {}) => ({ newOrders: metric(12), processing: metric(1234), late: metric(2), shipped: metric(40), microExport: metric(186), microCountries: { SA: 55, BG: 40, RO: 33, AZ: 24, GR: 22, AE: 12 }, cancelled: metric(68), partiallyCancelled: metric(1), resent: metric(3), shipUnits: metric(37), shipOrders: 30, shipRows, onOpenDay: () => undefined, ...extra })
const card = (host: HTMLElement, title: string) => Array.from(host.querySelectorAll('article')).find(item => item.querySelector('small')?.textContent === title)!
const open = (host: HTMLElement, title: string) => card(host, title).querySelector<HTMLButtonElement>('.rr-metric-open')!.click()

describe('order summary cards', () => {
  it('shows eight cards in one grid, a title and a count each', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const row = host.querySelector('.rr-metrics.rr-metrics-8')!
    const cards = Array.from(row.querySelectorAll('article')).map(item => [item.querySelector('small')?.textContent, item.querySelector('strong')?.textContent])
    expect(cards).toEqual([
      ['Yeni Sipariş', '12'],
      ['İşleme Alınan', (1234).toLocaleString('tr-TR')],
      ['Geciken', '2'],
      ['Kargoda', '40'],
      ['Mikro İhracat Sipariş', '186'],
      ['İptal', '68'],
      ['Yeniden Gönderim', '3'],
      ['Kargolanacak Ürün Adedi', '37']
    ])
  })

  it('waits for the summary and reads a missing count as zero', () => {
    const { host } = render(<OrderMetricCards {...props({ newOrders: metric(undefined, true), processing: metric(undefined, true), late: metric(undefined), shipped: metric(0), microExport: metric(undefined, true), microCountries: undefined, cancelled: metric(undefined, true), partiallyCancelled: metric(undefined, true), resent: metric(undefined, true), shipUnits: metric(undefined, true), shipOrders: undefined, shipRows: undefined })} />)

    const values = Array.from(host.querySelectorAll('article strong')).map(value => value.textContent)
    expect(values).toEqual(['…', '…', '0', '0', '…', '…', '…', '…'])
    // While the data loads, the chip, the notes and the hover panels are not drawn.
    expect(host.querySelector('.rr-metric-chip')).toBeNull()
    expect(host.querySelector('.rr-metric-note')?.textContent).toBe('Kargoya verilmedi')
    expect(host.querySelector('.rr-metric-tip')).toBeNull()
  })

  it('opens the day lists from the cancelled count and its chip, the unshipped resends from their card and the orders to ship from theirs', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'İptal')
    const chip = host.querySelector<HTMLButtonElement>('.rr-metric-chip')!
    expect(chip.textContent).toBe('1 Kısmi')
    chip.click()
    open(host, 'Yeniden Gönderim')
    open(host, 'Kargolanacak Ürün Adedi')
    open(host, 'Mikro İhracat Sipariş')
    expect(openDay.mock.calls.map(call => call[0])).toEqual(['cancelled', 'partial', 'resent', 'ship', 'micro'])
  })

  it('keeps the cancelled count and its chip on one line like the other cards, and draws no chip when no order is partly cancelled', () => {
    const { host } = render(<OrderMetricCards {...props()} />)
    const cancelled = card(host, 'İptal')
    expect(cancelled.classList.contains('rr-metric-chipped')).toBe(true)
    expect(Array.from(cancelled.children).map(child => child.className)).toEqual(['rr-metric-open', 'rr-metric-chip'])
    cleanup()

    const plain = render(<OrderMetricCards {...props({ partiallyCancelled: metric(0) })} />).host
    expect(plain.querySelector('.rr-metric-chip')).toBeNull()
    expect(card(plain, 'İptal').classList.contains('rr-metric-chipped')).toBe(false)
    expect(plain.querySelectorAll('.rr-metric-open')).toHaveLength(4)
  })

  it('says it is today on hover, lists the countries of the micro-export orders, the biggest first, and opens their list', () => {
    const openDay = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenDay: openDay })} />)

    open(host, 'Mikro İhracat Sipariş')
    expect(openDay.mock.calls).toEqual([['micro']])
    const micro = card(host, 'Mikro İhracat Sipariş')
    expect(micro.querySelector('.rr-metric-tip-h')?.textContent).toBe('Bugün')
    expect(micro.querySelector('.rr-metric-open')!.getAttribute('title')).toBe('Bugün verilen mikro ihracat siparişleri')
    const rows = Array.from(micro.querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent, row.querySelector('svg.rr-omark-flag') !== null])
    expect(rows).toEqual([
      ['Suudi Arabistan', '55', true], ['Bulgaristan', '40', true], ['Romanya', '33', true],
      ['Azerbaycan', '24', true], ['Yunanistan', '22', true], ['Birleşik Arap Emirlikleri', '12', true]
    ])
  })

  it('writes the orders to ship as "N siparişte" and lists the products per store on hover', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const ship = card(host, 'Kargolanacak Ürün Adedi')
    expect(ship.querySelector('.rr-metric-note')?.textContent).toBe('30 siparişte')
    expect(Array.from(ship.querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent])).toEqual([['Trendyol', '36 adet'], ['Pazarama', '1 adet']])
  })

  it('writes "Kargoya verilmedi" beside the unshipped resends', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    expect(card(host, 'Yeniden Gönderim').querySelector('.rr-metric-note')?.textContent).toBe('Kargoya verilmedi')
  })

  // Geciken warns only above 0: a faint box and a dot (rr-alert) and a red number; at 0 it is as plain as the other cards.
  // İptal keeps just its red number above 0 and is plain at 0. Yeni Sipariş keeps its amber tone either way.
  it('warns with Geciken and İptal only above zero', () => {
    const classes = (host: HTMLElement) => Object.fromEntries(Array.from(host.querySelectorAll('article')).map(item => [item.querySelector('small')?.textContent, item.className]))
    const warning = render(<OrderMetricCards {...props({ late: metric(3), cancelled: metric(68), partiallyCancelled: metric(0) })} />).host
    expect(classes(warning)['Geciken']).toBe('rr-bad rr-alert')
    expect(classes(warning)['İptal']).toBe('rr-bad rr-metric-link')
    expect(classes(warning)['Yeni Sipariş']).toBe('rr-warn')
    cleanup()
    const quiet = render(<OrderMetricCards {...props({ late: metric(0), cancelled: metric(0), partiallyCancelled: metric(0) })} />).host
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
  })
})
