import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; headers: Record<string, string>; body: unknown }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, headers: Object.fromEntries(new Headers(init?.headers).entries()), body: init?.body })
    if (path === '/products/costs/summary') return { variantCount: 3738, missingCostCount: 3666 }
    if (path === '/products/costs/import') return {
      fileName: 'maliyet.xlsx', totalRows: 5, updates: 2, clears: 0, unchanged: 1, skipped: 1, unmatched: 1, ambiguous: 0, errors: 0, rowsTruncated: false,
      rows: [
        { rowNumber: 6, sku: 'YOK-1', barcode: null, variantId: null, productTitle: null, variantLabel: null, oldCost: null, newCost: 10, outcome: 'UNMATCHED', message: 'Stok kodu katalogda yok.' },
        { rowNumber: 2, sku: 'ABC-01', barcode: '869', variantId: 'v1', productTitle: 'Gömlek', variantLabel: 'Beden: M', oldCost: null, newCost: 123.45, outcome: 'UPDATE', message: null },
        { rowNumber: 3, sku: 'ABC-02', barcode: null, variantId: 'v2', productTitle: 'Gömlek', variantLabel: 'Beden: L', oldCost: 5, newCost: 0, outcome: 'UPDATE', message: null }
      ],
      changes: [{ variantId: 'v1', version: 2, cost: 123.45 }, { variantId: 'v2', version: 4, cost: 0 }]
    }
    if (path === '/products/costs/apply') return { updated: 2, cleared: 0, products: 1 }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { ProductCostImportDialog, productCostTemplateHref } from './ProductCostImportDialog'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

async function renderDialog(onClose = vi.fn()) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><ProductCostImportDialog onClose={onClose} /></QueryClientProvider>) })
  await settle()
}

const button = (text: string) => Array.from(document.body.querySelectorAll('button')).find(item => item.textContent?.includes(text))
const templateLink = () => Array.from(document.body.querySelectorAll('a')).find(item => item.textContent?.includes('Şablonu İndir'))!

async function chooseFile(name: string) {
  const input = document.body.querySelector<HTMLInputElement>('input[type="file"]')!
  Object.defineProperty(input, 'files', { configurable: true, value: [new File(['x'], name)] })
  await act(async () => { input.dispatchEvent(new Event('change', { bubbles: true })) })
  await settle()
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

describe('ProductCostImportDialog', () => {
  it('builds the template link with and without the missing-only filter', () => {
    expect(productCostTemplateHref(false)).toBe('/api/v1/products/costs/template.xlsx')
    expect(productCostTemplateHref(true)).toBe('/api/v1/products/costs/template.xlsx?missingOnly=true')
  })

  it('shows how many variants lack a cost and offers the missing-only template first', async () => {
    await renderDialog()

    expect(document.body.textContent).toContain('Maliyeti eksik 3.666 varyant')
    expect(templateLink().getAttribute('href')).toBe('/api/v1/products/costs/template.xlsx?missingOnly=true')
    const toggle = document.body.querySelector<HTMLInputElement>('.pci-check input')!
    await act(async () => { toggle.click() })
    expect(templateLink().getAttribute('href')).toBe('/api/v1/products/costs/template.xlsx')
  })

  it('refuses a file that is neither .xlsx nor .csv without uploading it', async () => {
    await renderDialog()
    await chooseFile('maliyet.pdf')

    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain('.xlsx ya da .csv')
    expect(apiState.calls.some(call => call.path === '/products/costs/import')).toBe(false)
  })

  it('previews the upload, then saves exactly the previewed changes with an idempotency key', async () => {
    await renderDialog()
    await chooseFile('maliyet.xlsx')

    const upload = apiState.calls.find(call => call.path === '/products/costs/import')!
    expect(upload.method).toBe('POST')
    expect(upload.body).toBeInstanceOf(FormData)
    const rows = Array.from(document.body.querySelectorAll('.pci-tbl tbody tr'))
    expect(rows).toHaveLength(3)
    expect(rows[0].textContent).toContain('Eşleşmedi')
    expect(rows[1].textContent).toContain('Güncellenecek')
    expect(rows[2].textContent).toMatch(/₺0,00/)
    expect(document.body.textContent).toContain('kaydedilmez')

    const save = button('Maliyetleri Kaydet (2)')!
    expect(save.disabled).toBe(false)
    await act(async () => { save.click() })
    await settle()

    const applied = apiState.calls.find(call => call.path === '/products/costs/apply')!
    expect(applied.headers['idempotency-key']).toBeTruthy()
    expect(JSON.parse(applied.body as string)).toEqual({ changes: [{ variantId: 'v1', version: 2, cost: 123.45 }, { variantId: 'v2', version: 4, cost: 0 }] })
    expect(document.body.textContent).toContain('Maliyetler kaydedildi')
    expect(document.body.textContent).toContain('2 varyantın maliyeti güncellendi')
  })
})
