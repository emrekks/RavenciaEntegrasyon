import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Another card ("Kadın Siyah Bluz") holds RYP003T30 as its stock code and RYP003T31 as its barcode.
const hubApi = vi.hoisted(() => vi.fn(async (path: string, init?: { body?: string }) => {
  if (path !== '/catalog/variant-codes/taken') throw new Error(`Test API fixture does not handle ${path}`)
  const { codes } = JSON.parse(init?.body ?? '{}') as { codes: string[] }
  const held = [{ code: 'RYP003T30', field: 'sku', productTitle: 'Kadın Siyah Bluz' }, { code: 'RYP003T31', field: 'barcode', productTitle: 'Kadın Siyah Bluz' }]
  return { items: held.filter(item => codes.some(code => code.trim().toUpperCase() === item.code)) }
}))

vi.mock('../../shared/api', async importOriginal => ({ ...await importOriginal<typeof import('../../shared/api')>(), hubApi }))

import { variantCodeKey } from './product-editor-state'
import { takenVariantCodeHolders, useTakenVariantCodes } from './variant-codes-taken'

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  hubApi.mockClear()
})

afterEach(() => {
  act(() => root.unmount())
  client.clear()
  container.remove()
  vi.useRealTimers()
})

type Rows = { barcode: string; sku: string }[]
function Probe({ rows, own }: { rows: Rows; own: string[] }) {
  const held = useTakenVariantCodes(rows, own)
  return <span data-testid="held">{JSON.stringify(held)}</span>
}

async function renderProbe(rows: Rows, own: string[] = []) {
  await act(async () => { root.render(<QueryClientProvider client={client}><Probe rows={rows} own={own} /></QueryClientProvider>) })
}

async function settle() {
  await act(async () => { vi.advanceTimersByTime(500) })
  await act(async () => { await vi.runAllTimersAsync() })
}

const held = () => JSON.parse(container.querySelector('[data-testid="held"]')?.textContent ?? '{}') as Record<string, string>

describe('codes another product holds, while they are typed', () => {
  it('marks a stock code another card has as its stock code and a barcode it has as its barcode', () => {
    const rows = [{ barcode: 'ryp003t31', sku: 'RYP003T30 ' }, { barcode: 'RYP003T30', sku: 'RYP003T31' }]
    expect(takenVariantCodeHolders(rows, [{ code: 'RYP003T30', field: 'sku', productTitle: 'Kadın Siyah Bluz' }, { code: 'RYP003T31', field: 'barcode', productTitle: null }])).toEqual({
      [variantCodeKey('sku', 'RYP003T30')]: 'Kadın Siyah Bluz',
      [variantCodeKey('barcode', 'ryp003t31')]: 'başka bir ürün'
    })
  })

  it('asks once the typing settles, leaving the table’s own saved variants out', async () => {
    vi.useFakeTimers()
    // The opened table is checked at once; a typed code after the typing settles.
    await renderProbe([{ barcode: 'RYP003T2', sku: 'RYP003T2' }], ['v-1'])
    await settle()
    expect(hubApi).toHaveBeenCalledTimes(1)
    expect(held()).toEqual({})
    await renderProbe([{ barcode: 'RYP003T2', sku: 'RYP003T3' }], ['v-1'])
    await renderProbe([{ barcode: 'RYP003T2', sku: 'RYP003T30' }], ['v-1'])
    expect(hubApi).toHaveBeenCalledTimes(1)
    await settle()
    expect(hubApi).toHaveBeenCalledTimes(2)
    expect(JSON.parse(hubApi.mock.calls[1][1]!.body!)).toEqual({ codes: ['RYP003T2', 'RYP003T30'], excludeVariantIds: ['v-1'] })
    expect(held()).toEqual({ [variantCodeKey('sku', 'RYP003T30')]: 'Kadın Siyah Bluz' })
  })

  it('asks nothing for an empty table', async () => {
    vi.useFakeTimers()
    await renderProbe([{ barcode: '', sku: ' ' }])
    await settle()
    expect(hubApi).not.toHaveBeenCalled()
    expect(held()).toEqual({})
  })
})
