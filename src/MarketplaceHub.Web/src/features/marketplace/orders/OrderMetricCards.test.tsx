import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { microCountryRows, OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })
const props = (extra: Partial<Parameters<typeof OrderMetricCards>[0]> = {}) => ({ newOrders: metric(12), processing: metric(1234), late: metric(2), shipped: metric(40), microExport: metric(186), microCountries: { SA: 55, BG: 40, RO: 33, AZ: 24, GR: 22, AE: 12 }, cancelled: metric(68), partiallyCancelled: metric(1), resent: metric(3), onOpenTab: () => undefined, onOpenMicro: () => undefined, ...extra })

describe('order summary cards', () => {
  it('shows the orders taken into processing next to the new ones, seven cards in one grid, a title and a count each', () => {
    const { host } = render(<OrderMetricCards {...props()} />)

    const row = host.querySelector('.rr-metrics.rr-metrics-7')!
    const cards = Array.from(row.querySelectorAll('article')).map(card => [card.querySelector('small')?.textContent, card.querySelector('strong')?.textContent])
    expect(cards).toEqual([
      ['Yeni Sipariş', '12'],
      ['İşleme Alınan', (1234).toLocaleString('tr-TR')],
      ['Geciken', '2'],
      ['Kargoda', '40'],
      ['Mikro İhracat Sipariş', '186'],
      ['İptal', '68'],
      ['Yeniden Gönderim', '3']
    ])
  })

  it('waits for the summary and reads a missing count as zero', () => {
    const { host } = render(<OrderMetricCards {...props({ newOrders: metric(undefined, true), processing: metric(undefined, true), late: metric(undefined), shipped: metric(0), microExport: metric(undefined, true), microCountries: undefined, cancelled: metric(undefined, true), partiallyCancelled: metric(undefined, true), resent: metric(undefined) })} />)

    const values = Array.from(host.querySelectorAll('article strong')).map(value => value.textContent)
    expect(values).toEqual(['…', '…', '0', '0', '…', '…', '0'])
    // While the summary loads, the partial line and the country panel are not drawn.
    expect(host.querySelector('.rr-metric-sub')).toBeNull()
    expect(host.querySelector('.rr-metric-tip')).toBeNull()
  })

  it('opens the İptal tab from the cancelled count, Kısmi İptal from its line and Yeniden Gönderimler from its card', () => {
    const open = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenTab: open })} />)

    Array.from(host.querySelectorAll<HTMLButtonElement>('.rr-metric-open')).find(button => button.textContent?.includes('İptal'))!.click()
    const partial = host.querySelector<HTMLButtonElement>('.rr-metric-sub')!
    expect(partial.textContent).toBe('1 Kısmi İptal')
    partial.click()
    Array.from(host.querySelectorAll<HTMLButtonElement>('.rr-metric-open')).find(button => button.textContent?.includes('Yeniden Gönderim'))!.click()
    expect(open.mock.calls.map(call => call[0])).toEqual(['CANCELLED', 'PARTIALLY_CANCELLED', 'RESENT'])
  })

  it('draws no Kısmi İptal line when no order is partly cancelled', () => {
    const { host } = render(<OrderMetricCards {...props({ partiallyCancelled: metric(0) })} />)

    expect(host.querySelector('.rr-metric-sub')).toBeNull()
    expect(host.querySelectorAll('.rr-metric-open')).toHaveLength(3)
  })

  it('opens the micro-export list from its card and lists the countries, the biggest first', () => {
    const openMicro = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenMicro: openMicro })} />)

    Array.from(host.querySelectorAll<HTMLButtonElement>('.rr-metric-open')).find(button => button.textContent?.includes('Mikro İhracat'))!.click()
    expect(openMicro).toHaveBeenCalledTimes(1)
    const rows = Array.from(host.querySelectorAll('.rr-metric-tip-r')).map(row => [row.querySelector('span')?.textContent, row.querySelector('b')?.textContent, row.querySelector('svg.rr-omark-flag') !== null])
    expect(rows).toEqual([
      ['Suudi Arabistan', '55', true], ['Bulgaristan', '40', true], ['Romanya', '33', true],
      ['Azerbaycan', '24', true], ['Yunanistan', '22', true], ['Birleşik Arap Emirlikleri', '12', true]
    ])
  })

  // Geciken warns only above 0: a faint box and a dot (rr-alert) and a red number; at 0 it is as plain as the other cards.
  // İptal keeps just its red number above 0 and is plain at 0. Yeni Sipariş keeps its amber tone either way.
  it('warns with Geciken and İptal only above zero', () => {
    const classes = (host: HTMLElement) => Object.fromEntries(Array.from(host.querySelectorAll('article')).map(card => [card.querySelector('small')?.textContent, card.className]))
    const warning = render(<OrderMetricCards {...props({ late: metric(3), cancelled: metric(68) })} />).host
    expect(classes(warning)['Geciken']).toBe('rr-bad rr-alert')
    expect(classes(warning)['İptal']).toBe('rr-bad rr-metric-link')
    expect(classes(warning)['Yeni Sipariş']).toBe('rr-warn')
    cleanup()
    const quiet = render(<OrderMetricCards {...props({ late: metric(0), cancelled: metric(0) })} />).host
    expect(classes(quiet)['Geciken']).toBe('')
    expect(classes(quiet)['İptal']).toBe('rr-metric-link')
    expect(classes(quiet)['Yeni Sipariş']).toBe('rr-warn')
    cleanup()
    // While the summary loads nothing warns.
    const loading = render(<OrderMetricCards {...props({ late: metric(undefined, true), cancelled: metric(undefined, true) })} />).host
    expect(classes(loading)['Geciken']).toBe('')
    expect(classes(loading)['İptal']).toBe('rr-metric-link')
  })

  it('puts the orders of no known country last and draws no panel without countries', () => {
    expect(microCountryRows({ '': 4, SA: 2, BG: 2, RO: 0 })).toEqual([['BG', 2], ['SA', 2], ['', 4]])
    expect(microCountryRows(undefined)).toEqual([])
    const { host } = render(<OrderMetricCards {...props({ microExport: metric(0), microCountries: {} })} />)
    expect(host.querySelector('.rr-metric-tip')).toBeNull()
  })
})
