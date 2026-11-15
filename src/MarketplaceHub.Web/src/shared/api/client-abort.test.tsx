import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { act, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ErrorToast } from '../components/NoticeToast'
import { failedWithHttpAnswer, sendWithVersionedIdempotencyKey, resetAnsweredIdempotencyKeys } from '../idempotency'
import { isAbortedRequest } from './abort'
import { ApiRequestError, clientTimeoutCode, defaultReadTimeoutMs, hubApi, loadAllPages } from './client'

// A fetch that never answers on its own: it settles only when its signal is aborted (as a browser's fetch does).
function hangingFetch() {
  return vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
    const signal = init?.signal
    if (signal?.aborted) reject(signal.reason)
    signal?.addEventListener('abort', () => reject(signal.reason ?? new DOMException('Aborted', 'AbortError')))
  }))
}
const ok = (body: unknown) => vi.fn(async (_url: string, _init?: RequestInit) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }))

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

function render(element: ReactNode) {
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  act(() => root.render(element))
  return { container, unmount: () => { act(() => root.unmount()); container.remove() } }
}

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers() })

describe('a caller’s signal', () => {
  it('reaches fetch, and aborting it stops the request with an abort, not an HTTP error', async () => {
    const fetch = hangingFetch()
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    const request = hubApi('/orders', { signal: controller.signal })
    controller.abort()
    const error = await request.catch((reason: unknown) => reason)
    expect(isAbortedRequest(error)).toBe(true)
    expect(error).not.toBeInstanceOf(ApiRequestError)
    expect((fetch.mock.calls[0][1] as RequestInit).signal?.aborted).toBe(true)
  })

  it('is passed to every page loadAllPages reads', async () => {
    // The first page answers; the second hangs until the query's signal is aborted.
    const fetch = hangingFetch()
    fetch.mockImplementationOnce(async () => new Response(JSON.stringify({ items: [1], nextCursor: 'c1', hasMore: true }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    const reading = loadAllPages('/connections', { signal: controller.signal }).catch((reason: unknown) => reason)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
    controller.abort()
    expect(isAbortedRequest(await reading)).toBe(true)
    expect((fetch.mock.calls[1][1] as RequestInit).signal?.aborted).toBe(true)
    expect(String(fetch.mock.calls[0][0])).toContain('limit=200')
    expect(String(fetch.mock.calls[1][0])).toContain('after=c1')
    const plain = ok({ items: [], nextCursor: null, hasMore: false })
    vi.stubGlobal('fetch', plain)
    await loadAllPages('/connections', 100)
    expect(String(plain.mock.calls[0][0])).toContain('limit=100')
  })
})

describe('a read with no answer', () => {
  it('gives up after the read limit with CLIENT_TIMEOUT, which is not an HTTP answer', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', hangingFetch())
    const request = hubApi('/reports/sales').catch((reason: unknown) => reason)
    await vi.advanceTimersByTimeAsync(defaultReadTimeoutMs - 1)
    let settled = false
    void request.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    const error = await request
    expect(error).toBeInstanceOf(ApiRequestError)
    expect(error).toMatchObject({ status: 0, code: clientTimeoutCode })
    expect(isAbortedRequest(error)).toBe(false)
    expect(failedWithHttpAnswer(error)).toBe(false)
  })

  it('takes its own limit, or none', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', hangingFetch())
    const short = hubApi('/x', { timeoutMs: 1000 }).catch((reason: unknown) => reason)
    await vi.advanceTimersByTimeAsync(1000)
    expect(await short).toMatchObject({ code: clientTimeoutCode })
    const fetch = hangingFetch()
    vi.stubGlobal('fetch', fetch)
    void hubApi('/x', { timeoutMs: 0 }).catch(() => undefined)
    await vi.advanceTimersByTimeAsync(defaultReadTimeoutMs * 2)
    expect((fetch.mock.calls[0][1] as RequestInit).signal?.aborted ?? false).toBe(false)
    expect(fetch.mock.calls[0][1]).not.toHaveProperty('timeoutMs')
  })

  it('never cuts a write short: a slow POST waits for its answer', async () => {
    vi.useFakeTimers()
    let answer: (response: Response) => void = () => {}
    const fetch = vi.fn((_url: string, _init?: RequestInit) => new Promise<Response>(resolve => { answer = resolve }))
    vi.stubGlobal('fetch', fetch)
    // The CSRF token read comes first for a write.
    fetch.mockImplementationOnce(async () => new Response(JSON.stringify({ token: 't' }), { status: 200 }))
    const write = hubApi<{ id: string }>('/products', { method: 'POST', body: '{}', headers: { 'Idempotency-Key': 'k' } })
    await vi.advanceTimersByTimeAsync(defaultReadTimeoutMs * 3)
    const sent = fetch.mock.calls.at(-1)![1] as RequestInit
    expect(sent.signal ?? null).toBeNull()
    answer(new Response(JSON.stringify({ id: 'p1' }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    await expect(write).resolves.toEqual({ id: 'p1' })
  })

  it('keeps a versioned write’s key after an abort, as after any request with no answer', async () => {
    resetAnsweredIdempotencyKeys()
    const keys: string[] = []
    const send = (key: string) => { keys.push(key); return Promise.reject(new DOMException('Aborted', 'AbortError')) }
    await expect(sendWithVersionedIdempotencyKey('scope:id:v1', send)).rejects.toBeTruthy()
    await expect(sendWithVersionedIdempotencyKey('scope:id:v1', send)).rejects.toBeTruthy()
    expect(keys).toEqual(['scope:id:v1', 'scope:id:v1'])
  })
})

describe('a query that leaves the screen', () => {
  it('aborts its request and shows no error', async () => {
    const fetch = hangingFetch()
    vi.stubGlobal('fetch', fetch)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    function Reader() {
      const query = useQuery({ queryKey: ['abort-test'], queryFn: ({ signal }) => hubApi('/orders', { signal }) })
      return <>{query.error ? <ErrorToast error={query.error} title="Okunamadı" /> : 'okunuyor'}</>
    }
    const view = render(<QueryClientProvider client={client}><Reader /></QueryClientProvider>)
    expect(view.container.textContent).toBe('okunuyor')
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    expect(fetch).toHaveBeenCalledTimes(1)
    view.unmount()
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    expect((fetch.mock.calls[0][1] as RequestInit).signal?.aborted).toBe(true)
    expect(client.getQueryState(['abort-test'])?.status).not.toBe('error')
  })

  it('an aborted request given to ErrorToast shows nothing', () => {
    const view = render(<ErrorToast error={new DOMException('Aborted', 'AbortError')} title="Okunamadı" />)
    expect(view.container.textContent).toBe('')
    expect(document.body.textContent).not.toContain('Okunamadı')
    view.unmount()
  })
})
