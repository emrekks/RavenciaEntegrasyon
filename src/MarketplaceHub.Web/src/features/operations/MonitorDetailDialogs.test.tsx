// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiHealthAttemptsDialog, PriceStockProblemDialog, problemTotal, reasonText } from './MonitorDetailDialogs'

const hubPoll = vi.fn()
vi.mock('../../shared/api', () => ({ hubPoll: (...args: unknown[]) => hubPoll(...args) }))

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
let container: HTMLDivElement
let root: Root
beforeEach(() => { hubPoll.mockReset(); container = document.createElement('div'); document.body.appendChild(container) })
afterEach(() => { act(() => root.unmount()); container.remove(); document.body.innerHTML = '' })

async function mount(node: React.ReactNode) {
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter>{node}</MemoryRouter></QueryClientProvider>) })
  for (let i = 0; i < 6; i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}
const counts = { invalidOffers: 50, failedPushes: 3, manualReview: 2, notFoundLinks: 1, rejectedLinks: 1 }

describe('PriceStockProblemDialog', () => {
  it('puts the cell total in the title and lists the invalid offers with the human reason', async () => {
    hubPoll.mockResolvedValue({ items: [{ variantId: 'v1', productId: 'p1', sku: 'SKU-1', barcode: '869001', productTitle: 'Gömlek', variantLabel: 'Bej · L', reasonCode: 'CHANNEL_OFFER_INVALID', reasonSummary: 'raw text', panelListPrice: 100, panelSalePrice: 90, offerListPrice: 80, offerSalePrice: 85, currency: 'TRY', lastSeenAt: '2026-11-19T00:00:00Z' }], page: 1, pageSize: 50, totalCount: 50, filteredCount: 50, reasons: [{ code: 'CHANNEL_OFFER_INVALID', count: 50 }], reasonCodes: ['CHANNEL_OFFER_INVALID'] })
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="Trendyol" counts={counts} onClose={() => undefined} />)
    expect(problemTotal(counts)).toBe(57)
    expect(document.body.querySelector('.rr-m-head h2')?.textContent).toBe('Gönderim Sorunları · Trendyol (57)')
    expect(hubPoll.mock.calls[0][0]).toContain('/price-stock-monitor/c1/invalid-offers?page=1&pageSize=50')
    const row = document.body.querySelector('.md-tbl tbody tr')!
    expect(row.textContent).toContain('Fiyat girilmemiş ya da liste fiyatı satış fiyatından düşük')
    expect(row.querySelector('td[title="raw text"]')).not.toBeNull()
    expect(row.querySelector('a')?.getAttribute('href')).toBe('/products/p1')
    expect(document.body.textContent).toContain('Yalnız okuma')
    expect(document.body.querySelectorAll('.md-tabs button')).toHaveLength(4)
  })

  it('names a lone tab in the title and reads the failed pushes of the chosen kind', async () => {
    hubPoll.mockResolvedValue({ items: [{ jobId: 'j1', errorCode: 'E1', errorSummary: 'boom', status: 'DEAD', at: '2026-11-19T00:00:00Z' }], page: 1, pageSize: 50, totalCount: 3 })
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="N11" counts={{ invalidOffers: 0, failedPushes: 3, manualReview: 0, notFoundLinks: 0, rejectedLinks: 0 }} onClose={() => undefined} />)
    expect(document.body.querySelector('.rr-m-head h2')?.textContent).toBe('Başarısız Gönderim · N11 (3)')
    expect(hubPoll.mock.calls[0][0]).toContain('failed-pushes?kind=failed')
    expect(document.body.querySelector('.md-tbl a')?.getAttribute('href')).toBe('/jobs?ara=j1')
  })
})

describe('ApiHealthAttemptsDialog', () => {
  it('shows the four numbers and starts on the rate-limited class for Hız Sınırı', async () => {
    hubPoll.mockResolvedValue({ totals: { attempts: 40, failed: 6, remoteSide: 3, ourSide: 1, rateLimited: 2, workflowWait: 0 }, items: [{ jobId: 'j2', jobType: 'PRICE_UPDATE', attemptedAt: '2026-11-19T00:00:00Z', class: 'rateLimited', errorCode: '429', errorSummary: 'too many' }], page: 1, pageSize: 50, totalCount: 2, classes: ['marketplace', 'ours', 'rateLimited', 'workflowOther'] })
    await mount(<ApiHealthAttemptsDialog connectionId="c9" displayName="Pazarama" kind="rateLimit" onClose={() => undefined} />)
    expect(document.body.querySelector('.rr-m-head h2')?.textContent).toBe('Hız Sınırı · Pazarama (son 24 saat)')
    expect(hubPoll.mock.calls[0][0]).toContain('class=rateLimited')
    expect(Array.from(document.body.querySelectorAll('.md-totals dd')).map(item => item.textContent)).toEqual(['40', '3', '1', '2'])
    expect(document.body.textContent).toContain('Oran = (pazaryeri tarafı + bizim taraf + kota sınırı) ÷ toplam deneme')
    expect(document.body.querySelector('.md-tbl a')?.getAttribute('href')).toBe('/jobs?ara=j2')
  })

  it('has an empty and an error state', async () => {
    hubPoll.mockResolvedValue({ totals: { attempts: 0, failed: 0, remoteSide: 0, ourSide: 0, rateLimited: 0, workflowWait: 0 }, items: [], page: 1, pageSize: 50, totalCount: 0, classes: [] })
    await mount(<ApiHealthAttemptsDialog connectionId="c9" displayName="Pazarama" kind="errorRate" onClose={() => undefined} />)
    expect(document.body.querySelector('.md-empty')?.textContent).toBe('Bu listede kayıt yok.')
    expect(reasonText('SOMETHING_ELSE')).toBe('SOMETHING_ELSE')
  })
})

describe('opening a job from a detail window', () => {
  it('hands the job to the page and keeps the window open underneath', async () => {
    hubPoll.mockResolvedValue({ items: [{ jobId: 'j7', errorCode: 'E', errorSummary: 'boom', status: 'DEAD', at: '2026-11-19T00:00:00Z' }], page: 1, pageSize: 50, totalCount: 1 })
    const opened: string[] = []; let closed = 0
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="N11" counts={{ invalidOffers: 0, failedPushes: 1, manualReview: 0, notFoundLinks: 0, rejectedLinks: 0 }} onClose={() => { closed += 1 }} onOpenJob={id => opened.push(id)} />)
    const button = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.md-tbl button')).find(item => item.textContent === 'İşi Aç')!
    expect(document.body.querySelector('.md-tbl a')).toBeNull()
    await act(async () => { button.click() })
    expect(opened).toEqual(['j7']); expect(closed).toBe(0)
  })
})
