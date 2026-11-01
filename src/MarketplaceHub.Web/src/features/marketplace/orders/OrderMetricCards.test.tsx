import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })
const props = (extra: Partial<Parameters<typeof OrderMetricCards>[0]> = {}) => ({ newOrders: metric(12), processing: metric(1234), late: metric(2), shipped: metric(40), uninvoiced: metric(5), cancelled: metric(68), partiallyCancelled: metric(1), resent: metric(3), onOpenTab: () => undefined, ...extra })

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
      ['Fatura Bekleyen', '5'],
      ['İptal', '68'],
      ['Yeniden Gönderim', '3']
    ])
  })

  it('waits for the summary and reads a missing count as zero', () => {
    const { host } = render(<OrderMetricCards {...props({ newOrders: metric(undefined, true), processing: metric(undefined, true), late: metric(undefined), shipped: metric(0), uninvoiced: metric(undefined), cancelled: metric(undefined, true), partiallyCancelled: metric(undefined, true), resent: metric(undefined) })} />)

    const values = Array.from(host.querySelectorAll('article strong')).map(value => value.textContent)
    expect(values).toEqual(['…', '…', '0', '0', '0', '…', '0'])
    // While the summary loads, the partial line is not drawn.
    expect(host.querySelector('.rr-metric-sub')).toBeNull()
  })

  it('opens the İptal tab from the cancelled count, Kısmi İptal from its line and Yeniden Gönderimler from its card', () => {
    const open = vi.fn()
    const { host } = render(<OrderMetricCards {...props({ onOpenTab: open })} />)

    const cancelled = host.querySelector<HTMLButtonElement>('.rr-metric-link .rr-metric-open')!
    expect(cancelled.textContent).toContain('İptal')
    cancelled.click()
    const partial = host.querySelector<HTMLButtonElement>('.rr-metric-sub')!
    expect(partial.textContent).toBe('1 Kısmi İptal')
    partial.click()
    Array.from(host.querySelectorAll<HTMLButtonElement>('.rr-metric-open')).find(button => button.textContent?.includes('Yeniden Gönderim'))!.click()
    expect(open.mock.calls.map(call => call[0])).toEqual(['CANCELLED', 'PARTIALLY_CANCELLED', 'RESENT'])
  })

  it('draws no Kısmi İptal line when no order is partly cancelled', () => {
    const { host } = render(<OrderMetricCards {...props({ partiallyCancelled: metric(0) })} />)

    expect(host.querySelector('.rr-metric-sub')).toBeNull()
    expect(host.querySelectorAll('.rr-metric-open')).toHaveLength(2)
  })
})
