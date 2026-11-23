import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { JobReadRecords } from './JobReadRecords'
import type { JobReadSummary } from './job-read-summary'

const calls = vi.hoisted(() => [] as string[])
vi.mock('../../shared/api', () => ({
  hubPoll: async (path: string) => { calls.push(path); return { kind: 'PRODUCT', items: [{ modelCode: 'MZ056', firstBarcode: '869001', barcodeCount: 3, variantCount: 3, result: 'NEW', reason: null }], page: 1, pageSize: 25, totalCount: 224, storedCount: 100, receivedCount: 224, omittedCount: 124, counts: {} } },
  hubFile: async (path: string) => { calls.push(path); return { blob: new Blob(['x']), fileName: 'k.xlsx', headers: new Headers() } },
  ApiRequestError: class extends Error { code?: string }
}))

const read: JobReadSummary = { kind: 'PRODUCT', counts: { received: 224, inserted: 224, updated: 0, unchanged: 0, skipped: 0, failed: 0 }, omitted: 124, records: [], sentence: 'Trendyol’dan 224 ürün okundu.' }

let container: HTMLDivElement
let root: Root
beforeEach(() => { ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true; calls.length = 0; container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
afterEach(() => { act(() => root.unmount()); container.remove() })
const settle = async () => { for (let i = 0; i < 4; i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) }) }

describe('Çekilen Kayıtlar of a product read job', () => {
  it('reads 25 a page from the server with the real total and tells about the records an old job did not store', async () => {
    await act(async () => { root.render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><JobReadRecords jobId="job-9" read={read} recordsKept /></QueryClientProvider>) })
    await settle()
    expect(calls[0]).toBe('/jobs/job-9/read-records?page=1&pageSize=25')
    const text = container.textContent ?? ''
    expect(text).toContain('Toplam 224 adet')
    expect(text).toContain('MZ056')
    expect(text).toContain('3 Varyant')
    expect(text).toContain('Bu işte 224 kayıt okundu; 124 kaydın ayrıntısı saklanmadı (eski iş).')
    expect(text).not.toContain('kayıt daha')
    const only = container.querySelector<HTMLInputElement>('.publication-job-variants-tools input[type="checkbox"]')!
    await act(async () => { only.click() })
    await settle()
    expect(calls).toContain('/jobs/job-9/read-records?page=1&pageSize=25&onlyProblems=true')
  })
})
