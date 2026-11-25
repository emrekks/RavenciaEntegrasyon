import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../../shared/api'

const calls = vi.hoisted(() => [] as string[])

function minutesAgo(minutes: number) {
  return new Date(Date.now() - minutes * 60_000).toISOString()
}

function job(id: string, status: string, createdAt: string, externalId: string | null = null) {
  return {
    id, connectionId: 'hb', jobType: 'PRODUCT_CREATE', status, attemptCount: 1, maxAttempts: 10, availableAt: createdAt, lastErrorCode: status === 'DEAD' ? 'HB_REJECTED' : null,
    lastErrorSummary: null, correlationId: 'send-1', createdAt, startedAt: createdAt, completedAt: createdAt, marketplace: 'Hepsiburada', externalId, firstFailedAt: null,
    lastFailedAt: null, nextRetryAt: null, batchCount: 3, progressCurrent: 0, progressTotal: null, progressPercent: null, progressLabel: null, progressReceived: 0,
    progressProcessed: 0, progressSkipped: 0, progressFailed: 0, lastAttemptStartedAt: createdAt, heartbeatAt: null
  }
}

const cards = vi.hoisted(() => [
  { id: 'card-siyah', colour: 'Siyah', status: 'SUCCEEDED', minutes: 3 },
  { id: 'card-beyaz', colour: 'Beyaz', status: 'DEAD', minutes: 2 },
  { id: 'card-mavi', colour: 'Mavi', status: 'SUCCEEDED', minutes: 1 }
])

vi.mock('../../shared/api', () => {
  const hubApi = vi.fn(async (path: string) => {
    calls.push(path)
    if (path.startsWith('/jobs/counts')) return [{ status: 'SUCCEEDED', jobType: 'PRODUCT_CREATE', count: 2 }, { status: 'DEAD', jobType: 'PRODUCT_CREATE', count: 1 }]
    if (path.startsWith('/jobs/page?')) {
      const members = cards.map(card => ({ job: job(card.id, card.status, minutesAgo(card.minutes), `track-${card.id}`), productId: `product-${card.id}`, title: 'Örnek Elbise', optionSignature: `Renk: ${card.colour} | Beden: M`, variantCount: 3 }))
      const newest = members[members.length - 1].job
      return { items: [{ ...newest, modelGroup: { key: 'hb|PRODUCT_CREATE|send-1|model:b:MZ900', modelCode: 'MZ900', title: 'Örnek Elbise', cardCount: 3, variantCount: 9, cards: members } }], nextCursor: null, hasMore: false }
    }
    if (path.startsWith('/jobs/card-beyaz')) return { job: job('card-beyaz', 'DEAD', minutesAgo(2)), attempts: [], order: null, change: null, relatedOrders: [] }
    throw new Error(`Test API fixture does not handle ${path}`)
  })
  return {
    hubApi, hubPoll: hubApi, backgroundPollHeaders: {},
    loadAllPages: vi.fn(async () => ({ items: [{ id: 'hb', platformCode: 'HEPSIBURADA', status: 'ACTIVE' }], nextCursor: null, hasMore: false }))
  }
})

import { JobsPage } from './pages'

const me = { id: 'user-1', email: 'ops@example.test', displayName: 'Operasyon', role: 'OWNER', state: 'ACTIVE', tenantId: 'tenant-1' } as unknown as Me
let container: HTMLDivElement
let root: Root

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

beforeEach(async () => {
  calls.length = 0
  container = document.createElement('div')
  document.body.append(container)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/jobs?kategori=CATALOG']}><JobsPage me={me} /></MemoryRouter></QueryClientProvider>) })
  await settle()
  await settle()
  // The model row's code loads lazily (JobModelGroupRows); until then its newest job shows as a plain row.
  for (let tries = 0; tries < 100 && !container.querySelector('.jobs-model-group-row'); tries++) await settle()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('İş Takibi model rows', () => {
  it('asks the server for model rows and shows a model’s cards of one send as one row whose failure shows', () => {
    const page = calls.find(path => path.startsWith('/jobs/page?'))!
    expect(new URLSearchParams(page.slice(page.indexOf('?') + 1)).get('groupBy')).toBe('model')
    const rows = container.querySelectorAll('.jobs-reference-row')
    expect(rows).toHaveLength(1)
    const row = rows[0]
    expect(row.textContent).toContain('Hepsiburada Ürün Oluşturma')
    expect(row.textContent).toContain('MZ900 · 3 Renk · 9 Varyant · Örnek Elbise')
    // One failed card: the row is the failure, with the counts; never the green "Başarılı".
    expect(row.querySelector('.jobs-reference-status')!.className).toContain('error')
    expect(row.querySelector('.jobs-reference-status')!.textContent).toBe('Deneme Limiti Doldu')
    expect(row.textContent).toContain('2\u00a0Başarılı · 1\u00a0Başarısız')
    // The marketplace tracking ids stay inside.
    expect(row.textContent).not.toContain('track-')
    // The footer counts rows and says how many jobs they are.
    expect(container.querySelector('.jobs-reference-pagination')!.textContent).toContain('Toplam 1 kayıt (3 iş)')
  })

  it('Tümünü Aç lists the colour cards, each opening its own job detail', async () => {
    const toggle = container.querySelector<HTMLButtonElement>('.jobs-model-group-toggle')!
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(toggle.title).toBe('Tümünü Aç')
    await act(async () => { toggle.click() })
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(toggle.title).toBe('Tümünü Kapat')
    const cardRows = Array.from(container.querySelectorAll('.jobs-model-card-row'))
    expect(cardRows.map(row => row.querySelector('strong')!.textContent)).toEqual(['Siyah', 'Beyaz', 'Mavi'])
    expect(cardRows[1].textContent).toContain('3 Varyant · track-card-beyaz')
    expect(cardRows[1].textContent).toContain('Deneme Limiti Doldu')
    await act(async () => { (cardRows[1] as HTMLElement).click() })
    await settle()
    expect(calls).toContain('/jobs/card-beyaz')
    expect(document.querySelector('.job-detail-drawer')).not.toBeNull()
    // Closing the row hides the cards again.
    await act(async () => { toggle.click() })
    expect(container.querySelectorAll('.jobs-model-card-row')).toHaveLength(0)
  })
})
