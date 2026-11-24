import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { queryKeys } from '../../../shared/api/query-keys'

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async () => ({})) }
})

import { hubApi } from '../../../shared/api'
import { ReturnExchangeMarkDialog } from '../returns/ReturnExchangeMarkDialog'
import { ExchangeResendShipDialog } from './ExchangeResendShipDialog'

afterEach(() => { cleanup(); vi.mocked(hubApi).mockClear() })

// Scan O6 (23 Nov 2026): "Değişim Onaylandı" marked on İadeler raised the claim's version, but the Siparişler list kept the
// old one in exchangeResend.claimVersion, so "Kargoya Verildi" there answered 412. Each mark refreshes the other page's
// read of the same claim: the exchange mark the orders list, the ship mark the return's detail.
describe('exchange marks refresh the other page\'s claim version', () => {
  const confirm = async (title: string) => {
    const button = [...document.querySelectorAll<HTMLButtonElement>('.rr-primary')].find(candidate => candidate.textContent === title)!
    await act(async () => { button.click() })
  }

  it('the Değişim Onaylandı mark refreshes the orders list', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    render(<QueryClientProvider client={client}><ReturnExchangeMarkDialog item={{ id: 'cl3', version: 4, orderNumber: 'TEST-1' }} marked onClose={() => {}} onDone={() => {}} /></QueryClientProvider>)
    await confirm('Değişim Onaylandı Olarak İşaretle')
    expect(vi.mocked(hubApi).mock.calls.some(([path]) => path === '/returns/cl3/exchange-approved')).toBe(true)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.orders() })
  })

  it('the Kargoya Verildi mark refreshes the return\'s detail', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    render(<QueryClientProvider client={client}><ExchangeResendShipDialog claim={{ id: 'cl3', version: 5, orderNumber: 'TEST-1' }} marked onClose={() => {}} onDone={() => {}} /></QueryClientProvider>)
    await confirm('Kargoya Verildi Olarak İşaretle')
    expect(vi.mocked(hubApi).mock.calls.some(([path]) => path === '/returns/cl3/replacement-shipped')).toBe(true)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.return('cl3') })
  })
})
