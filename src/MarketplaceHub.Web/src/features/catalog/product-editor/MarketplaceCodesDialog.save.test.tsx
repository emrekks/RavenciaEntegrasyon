import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const codes = {
  connections: [{ connectionId: 'hb-1', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada' }],
  variants: [{ variantId: 'v-1', sku: 'KZK-S', barcode: null, codes: [] }]
}

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/products/p-1/marketplace-codes' && (init?.method ?? 'GET') === 'GET') return codes
      if (path === '/products/p-1/marketplace-codes' && init?.method === 'PUT') return { ...codes, variants: [{ ...codes.variants[0], codes: [{ connectionId: 'hb-1', externalId: 'HBCV1', externalBarcode: null, externalSku: null }] }] }
      throw new Error(`Test API fixture does not handle ${init?.method ?? 'GET'} ${path}`)
    })
  }
})

import { MarketplaceCodesDialog } from './MarketplaceCodesDialog'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

describe('MarketplaceCodesDialog save', () => {
  it('reads the product and the products list again after a code is saved, so the cards show the new link', async () => {
    client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    root = createRoot(container)
    await act(async () => { root.render(<QueryClientProvider client={client}><MarketplaceCodesDialog productId="p-1" rows={[{ variantId: 'v-1', label: 'KZK-S' }]} onClose={() => undefined} /></QueryClientProvider>) })
    await settle()

    const input = document.body.querySelector<HTMLInputElement>('input.marketplace-code-input')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'HBCV1')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    const save = Array.from(document.body.querySelectorAll('button')).find(item => item.textContent?.startsWith('Kaydet'))!
    await act(async () => { save.click() })
    await settle()

    expect(document.body.textContent).toContain('1 kod kaydedildi.')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['product', 'p-1'] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['products'] })
  })
})
