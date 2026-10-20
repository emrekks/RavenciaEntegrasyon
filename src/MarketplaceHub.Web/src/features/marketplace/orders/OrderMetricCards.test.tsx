import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { OrderMetricCards } from './OrderMetricCards'

afterEach(cleanup)

const metric = (value: number | undefined, loading = false) => ({ value, loading })

describe('order summary cards', () => {
  it('shows the orders taken into processing next to the new ones, five cards in one row', () => {
    const { host } = render(<OrderMetricCards newOrders={metric(12)} processing={metric(1234)} late={metric(2)} shipped={metric(40)} uninvoiced={metric(5)} />)

    const row = host.querySelector('.rr-metrics.rr-metrics-5')!
    const cards = Array.from(row.querySelectorAll('article')).map(card => [card.querySelector('small')?.textContent, card.querySelector('strong')?.textContent, card.querySelector('span')?.textContent])
    expect(cards).toEqual([
      ['Yeni Sipariş', '12', 'işleme alınmayı bekliyor'],
      ['İşleme Alınan', (1234).toLocaleString('tr-TR'), 'kargoya verilmeyi bekliyor'],
      ['Geciken', '2', 'kargoya verme süresi doldu'],
      ['Kargoda', '40', 'alıcıya doğru yolda'],
      ['Fatura Bekleyen', '5', 'kesilmesi gereken fatura']
    ])
  })

  it('waits for the summary and reads a missing count as zero', () => {
    const { host } = render(<OrderMetricCards newOrders={metric(undefined, true)} processing={metric(undefined, true)} late={metric(undefined)} shipped={metric(0)} uninvoiced={metric(undefined)} />)

    const values = Array.from(host.querySelectorAll('article strong')).map(value => value.textContent)
    expect(values).toEqual(['…', '…', '0', '0', '0'])
  })
})
