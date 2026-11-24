// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { queryKeys } from '../../shared/api/query-keys'
import { ApiHealthAttemptsDialog, PriceStockProblemDialog, problemTotal, reasonText } from './MonitorDetailDialogs'

const hubPoll = vi.fn()
const hubApi = vi.fn()
vi.mock('../../shared/api', () => ({ hubPoll: (...args: unknown[]) => hubPoll(...args), hubApi: (...args: unknown[]) => hubApi(...args) }))

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
let container: HTMLDivElement
let root: Root
beforeEach(() => { hubPoll.mockReset(); hubApi.mockReset(); container = document.createElement('div'); document.body.appendChild(container) })
afterEach(() => { act(() => root.unmount()); container.remove(); document.body.innerHTML = '' })

async function mount(node: React.ReactNode, role?: string) {
  root = createRoot(container)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  if (role) client.setQueryData(queryKeys.me(), { role })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter>{node}</MemoryRouter></QueryClientProvider>) })
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

  it('marks one row or the picked rows "Sorun Yok" and says it only hides the row', async () => {
    const offer = (id: string) => ({ variantId: id, productId: 'p1', sku: `SKU-${id}`, barcode: null, productTitle: 'Gömlek', variantLabel: null, reasonCode: 'CHANNEL_OFFER_INVALID', reasonSummary: 'raw', panelListPrice: null, panelSalePrice: null, offerListPrice: 0, offerSalePrice: 0, currency: 'TRY', lastSeenAt: '2026-11-19T00:00:00Z' })
    hubPoll.mockResolvedValue({ items: [offer('v1'), offer('v2'), offer('v3')], page: 1, pageSize: 50, totalCount: 3, filteredCount: 3, reasons: [{ code: 'CHANNEL_OFFER_INVALID', count: 3 }], reasonCodes: ['CHANNEL_OFFER_INVALID'] })
    hubApi.mockResolvedValue({ marked: 1, alreadyClosed: 0 })
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="Trendyol" counts={{ invalidOffers: 3, failedPushes: 0, manualReview: 0, notFoundLinks: 0, rejectedLinks: 0 }} onClose={() => undefined} />, 'OWNER')
    expect(document.body.textContent).toContain('"Sorun Yok" yalnız satırı gizler')
    const bulk = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.startsWith('Seçilenleri Sorun Yok İşaretle'))!
    expect(bulk.disabled).toBe(true)
    const rowButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.md-tbl tbody button')).find(button => button.textContent === 'Sorun Yok')!
    await act(async () => { rowButton.click() })
    expect(hubApi.mock.calls[0][0]).toBe('/price-stock-monitor/c1/invalid-offers/no-problem')
    expect(JSON.parse(hubApi.mock.calls[0][1].body)).toEqual({ variantIds: ['v1'] })
    const boxes = Array.from(document.body.querySelectorAll<HTMLInputElement>('.md-tbl tbody input.rr-cb'))
    expect(boxes).toHaveLength(3)
    await act(async () => { boxes[1].click() })
    await act(async () => { boxes[2].click() })
    expect(bulk.textContent).toBe('Seçilenleri Sorun Yok İşaretle (2)')
    await act(async () => { bulk.click() })
    expect(JSON.parse(hubApi.mock.calls[1][1].body)).toEqual({ variantIds: ['v2', 'v3'] })
  })

  it('shows no "Sorun Yok" control to a role the server refuses', async () => {
    hubPoll.mockResolvedValue({ items: [{ variantId: 'v1', productId: 'p1', sku: 'S', barcode: null, productTitle: 'G', variantLabel: null, reasonCode: 'CHANNEL_OFFER_INVALID', reasonSummary: 'r', panelListPrice: null, panelSalePrice: null, offerListPrice: 0, offerSalePrice: 0, currency: 'TRY', lastSeenAt: '2026-11-19T00:00:00Z' }], page: 1, pageSize: 50, totalCount: 1, filteredCount: 1, reasons: [], reasonCodes: [] })
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="Trendyol" counts={{ invalidOffers: 1, failedPushes: 0, manualReview: 0, notFoundLinks: 0, rejectedLinks: 0 }} onClose={() => undefined} />, 'OPERATIONS')
    expect(document.body.querySelector('.md-tbl input')).toBeNull()
    expect(document.body.textContent).not.toContain('Seçilenleri Sorun Yok')
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

describe('a list that shrinks under the person', () => {
  it('goes back to the last real page instead of staying on an empty page with no pager', async () => {
    const row = { jobId: 'j1', errorCode: 'E', errorSummary: 'boom', status: 'DEAD', at: '2026-11-19T00:00:00Z' }
    hubPoll.mockImplementation(async (path: string) => String(path).includes('page=2') ? { items: [], page: 2, pageSize: 50, totalCount: 30 } : { items: [row], page: 1, pageSize: 50, totalCount: String(path).includes('page=1') && hubPoll.mock.calls.length < 3 ? 120 : 30 })
    await mount(<PriceStockProblemDialog connectionId="c1" displayName="N11" counts={{ invalidOffers: 0, failedPushes: 120, manualReview: 0, notFoundLinks: 0, rejectedLinks: 0 }} onClose={() => undefined} />)
    const page2 = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.rr-pages button')).find(button => button.textContent === '2')!
    await act(async () => { page2.click() })
    for (let i = 0; i < 4; i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const paths = hubPoll.mock.calls.map(call => String(call[0]))
    expect(paths.some(path => path.includes('page=2'))).toBe(true)
    expect(paths[paths.length - 1]).toContain('page=1')
    expect(document.body.querySelector('.md-tbl tbody tr')).not.toBeNull()
  })
})
