import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { ReturnExchangeMarkDialog } from './ReturnExchangeMarkDialog'

const api = vi.hoisted(() => ({ fail: false }))
vi.mock('../../../shared/api', () => ({ hubApi: async () => { if (api.fail) throw new Error('412'); return {} } }))

// The mark changes the claim's version: the lists that carry it (returns, the return, the orders list with its exchange mark) are read
// again after a success and also after a refusal, so a second try goes out with the current version.
describe('ReturnExchangeMarkDialog', () => {
  async function run(fail: boolean) {
    api.fail = fail; document.body.innerHTML = ''
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidated = vi.spyOn(client, 'invalidateQueries')
    const host = document.createElement('div'); document.body.append(host)
    await act(async () => { createRoot(host).render(<QueryClientProvider client={client}><ReturnExchangeMarkDialog item={{ id: 'r1', version: 3, orderNumber: '100' }} marked onClose={() => undefined} onDone={() => undefined} /></QueryClientProvider>) })
    const save = Array.from(document.body.querySelectorAll<HTMLButtonElement>('footer button')).find(button => button.classList.contains('rr-primary'))!
    await act(async () => { save.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    return invalidated.mock.calls.map(call => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey[0]))
  }
  it('reads the orders list again after a success', async () => { expect(await run(false)).toContain('"orders"') })
  it('reads the lists again after a refusal too', async () => { const roots = await run(true); expect(roots).toContain('"orders"'); expect(roots.length).toBeGreaterThanOrEqual(3) })
})
