// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueueQuotaView } from './QueueQuotaView'
import { failureOutcome, failureReason, failuresPath, type JobTypeFailures, type JobTypeMetric } from './queue-quota'

const hubPoll = vi.fn()
vi.mock('../../shared/api', () => ({ hubPoll: (...args: unknown[]) => hubPoll(...args), hubApi: vi.fn() }))

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
let container: HTMLDivElement
let root: Root
beforeEach(() => { hubPoll.mockReset(); container = document.createElement('div'); document.body.appendChild(container) })
afterEach(() => { act(() => root.unmount()); container.remove(); document.body.innerHTML = '' })

const metric = (jobType: string, failed: number): JobTypeMetric =>
  ({ jobType, queued: 0, running: 0, attempts: failed + 4, failed, averageWaitSeconds: null, p95WaitSeconds: null, averageDurationSeconds: null, p95DurationSeconds: null })
const failures: JobTypeFailures = {
  jobType: 'PTTAVM_ORDER_SYNC', totalCount: 3, groupCount: 2, page: 1, pageSize: 20,
  groups: [
    { errorCode: 'PTTAVM_NETWORK_ERROR', reason: 'PttAVM bağlantısı geçici olarak kurulamadı.', count: 2, firstSeen: '2026-11-27T08:24:00Z', lastSeen: '2026-11-27T10:50:00Z', transient: true },
    { errorCode: 'PTTAVM_REQUEST_REJECTED', reason: 'PttAVM isteği reddetti.', count: 1, firstSeen: '2026-11-27T09:00:00Z', lastSeen: '2026-11-27T09:00:00Z', transient: false }
  ],
  items: [
    { jobId: 'j1', attemptId: 'a1', attemptedAt: '2026-11-27T10:50:00Z', attemptNumber: 2, storeName: 'Deneme Mağazası', platformCode: 'PTTAVM', errorCode: 'PTTAVM_NETWORK_ERROR', reason: 'PttAVM bağlantısı geçici olarak kurulamadı.', jobStatus: 'SUCCEEDED', transient: true },
    { jobId: 'j2', attemptId: 'a2', attemptedAt: '2026-11-27T09:00:00Z', attemptNumber: 6, storeName: 'Deneme Mağazası', platformCode: 'PTTAVM', errorCode: 'PTTAVM_REQUEST_REJECTED', reason: 'PttAVM isteği reddetti.', jobStatus: 'DEAD', transient: false },
    { jobId: 'j3', attemptId: 'a3', attemptedAt: '2026-11-27T08:24:00Z', attemptNumber: 1, storeName: null, platformCode: null, errorCode: 'PTTAVM_NETWORK_ERROR', reason: 'PttAVM bağlantısı geçici olarak kurulamadı.', jobStatus: 'RETRY_SCHEDULED', transient: true }
  ]
}

function answer(path: string) {
  if (path.startsWith('/jobs/metrics/failures')) return Promise.resolve(failures)
  if (path.startsWith('/jobs/metrics/lanes') || path.startsWith('/jobs/metrics/quota') || path.startsWith('/jobs/counts')) return Promise.resolve([])
  if (path.startsWith('/jobs/metrics')) return Promise.resolve([metric('PTTAVM_ORDER_SYNC', 3), metric('TRENDYOL_ORDER_SYNC', 0)])
  return Promise.reject(new Error(`unexpected ${path}`))
}

async function settle() { for (let i = 0; i < 6; i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
async function mount(onOpenJob?: (jobId: string) => void) {
  root = createRoot(container)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><QueueQuotaView onOpenJob={onOpenJob} /></MemoryRouter></QueryClientProvider>) })
  await settle()
}

describe('Kuyruk ve Kota › Hata window', () => {
  it('makes a non-zero Hata number a dialog button and keeps zero plain', async () => {
    hubPoll.mockImplementation(answer)
    await mount()
    const cells = Array.from(container.querySelectorAll<HTMLTableCellElement>('td[data-label="Hata"]'))
    expect(cells).toHaveLength(2)
    const button = cells[0].querySelector('button')!
    expect(button.textContent).toBe('3')
    expect(button.getAttribute('aria-haspopup')).toBe('dialog')
    expect(cells[1].querySelector('button')).toBeNull()
    expect(cells[1].textContent).toBe('0')
  })

  it('opens the job type\'s failures for the table\'s window: reasons by count, attempts with their outcome, a job link', async () => {
    hubPoll.mockImplementation(answer)
    const onOpenJob = vi.fn()
    await mount(onOpenJob)
    await act(async () => { container.querySelector<HTMLButtonElement>('td[data-label="Hata"] button')!.click() })
    await settle()
    expect(hubPoll.mock.calls.map(call => call[0])).toContain('/jobs/metrics/failures?jobType=PTTAVM_ORDER_SYNC&hours=24&page=1&pageSize=20')
    const dialog = document.body.querySelector('[role="dialog"]')!
    expect(dialog.querySelector('.rr-crumb')?.textContent).toBe('İşlem Takibi')
    expect(dialog.querySelector('h2')?.textContent).toMatch(/Hataları$/)
    expect(dialog.textContent).toContain('Son 24 Saat: 3 başarısız deneme')
    const groups = Array.from(dialog.querySelectorAll('.qf-groups tbody tr'))
    expect(groups).toHaveLength(2)
    expect(groups[0].textContent).toContain('PttAVM bağlantısı geçici olarak kurulamadı.')
    expect(groups[0].querySelector('.qf-num')?.textContent).toBe('2')
    expect(groups[0].textContent).toContain('Geçici hata; iş kendiliğinden yeniden denenir.')
    expect(groups[1].textContent).not.toContain('Geçici hata')
    const attempts = Array.from(dialog.querySelectorAll('.qf-attempts tbody tr'))
    expect(attempts.map(row => row.querySelector('.qf-badge')?.textContent)).toEqual(['Sonra Başarılı', 'Hâlâ Hatalı', 'Yeniden Denenecek'])
    expect(attempts[2].querySelector('.qf-store')?.textContent).toBe('—')
    await act(async () => { attempts[1].querySelector<HTMLButtonElement>('.md-act button')!.click() })
    expect(onOpenJob).toHaveBeenCalledWith('j2')
    expect(dialog.querySelector('.rv-pager, nav[aria-label*="Sayfa"]')).toBeNull()
  })

  it('shows the empty and failed states', async () => {
    hubPoll.mockImplementation((path: string) => path.startsWith('/jobs/metrics/failures') ? Promise.resolve({ ...failures, totalCount: 0, groupCount: 0, groups: [], items: [] }) : answer(path))
    await mount()
    await act(async () => { container.querySelector<HTMLButtonElement>('td[data-label="Hata"] button')!.click() })
    await settle()
    expect(document.body.querySelector('[role="dialog"]')?.textContent).toContain('Bu aralıkta başarısız deneme yok.')
    act(() => root.unmount())
    hubPoll.mockImplementation((path: string) => path.startsWith('/jobs/metrics/failures') ? Promise.reject(new Error('Sunucuya ulaşılamadı.')) : answer(path))
    await mount()
    await act(async () => { container.querySelector<HTMLButtonElement>('td[data-label="Hata"] button')!.click() })
    await settle()
    expect(document.body.querySelector('[role="dialog"] [role="alert"]')).not.toBeNull()
  })
})

describe('failure words', () => {
  it('derives the outcome only from the stored job status', () => {
    expect(failureOutcome('SUCCEEDED').label).toBe('Sonra Başarılı')
    expect(failureOutcome('DEAD').label).toBe('Hâlâ Hatalı')
    expect(failureOutcome('MANUAL_REVIEW').label).toBe('Hâlâ Hatalı')
    expect(failureOutcome('RETRY_SCHEDULED').label).toBe('Yeniden Denenecek')
    expect(failureOutcome('PENDING').label).toBe('Yeniden Denenecek')
    expect(failureOutcome('LEASED').label).toBe('Yeniden Deneniyor')
    expect(failureOutcome('CANCELLED')).toEqual({ label: 'İptal Edildi', tone: 'muted' })
  })

  it('reads the reason, else the code, else says none was stored; builds the path of the window', () => {
    expect(failureReason('Bağlantı kurulamadı.', 'X_NETWORK_ERROR')).toBe('Bağlantı kurulamadı.')
    expect(failureReason(null, 'X_NETWORK_ERROR')).toBe('X_NETWORK_ERROR')
    expect(failureReason(' ', null)).toBe('Sebep kaydedilmemiş')
    expect(failuresPath('N11_ORDER_SYNC', '168', 2)).toBe('/jobs/metrics/failures?jobType=N11_ORDER_SYNC&hours=168&page=2&pageSize=20')
  })
})
