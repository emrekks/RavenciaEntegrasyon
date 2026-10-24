import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetAnsweredIdempotencyKeys } from '../../../shared/idempotency'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; headers: Record<string, string>; body: unknown }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, headers: Object.fromEntries(new Headers(init?.headers).entries()), body: init?.body })
    if (path === '/products/transfer/summary') return { variantCount: 3718, missingCostCount: 3666, missingDesiCount: 3646 }
    if (path === '/products/transfer/import') return {
      fileName: 'urunler.xlsx', totalRows: 5, updates: 2, costUpdates: 1, costClears: 1, desiUpdates: 1, desiClears: 0, unchanged: 1, skipped: 1, unmatched: 1, ambiguous: 0, errors: 0, rowsTruncated: false,
      rows: [
        { rowNumber: 6, sku: 'YOK-1', barcode: null, variantId: null, productTitle: null, variantLabel: null, oldCost: null, newCost: 10, costChange: null, oldDesi: null, newDesi: null, desiChange: null, outcome: 'UNMATCHED', message: 'Stok kodu katalogda yok.' },
        { rowNumber: 2, sku: 'ABC-01', barcode: '869', variantId: 'v1', productTitle: 'Gömlek', variantLabel: 'Beden: M', oldCost: null, newCost: 123.45, costChange: 'SET', oldDesi: 1, newDesi: 2.5, desiChange: 'SET', outcome: 'UPDATE', message: null },
        { rowNumber: 3, sku: 'ABC-02', barcode: null, variantId: 'v2', productTitle: 'Gömlek', variantLabel: 'Beden: L', oldCost: 5, newCost: null, costChange: 'CLEAR', oldDesi: 2, newDesi: null, desiChange: null, outcome: 'UPDATE', message: null }
      ],
      changes: [{ variantId: 'v1', version: 2, setCost: true, cost: 123.45, setDesi: true, desi: 2.5 }, { variantId: 'v2', version: 4, setCost: true, cost: null, setDesi: false, desi: null }]
    }
    if (path === '/products/transfer/apply') return { variants: 2, costsUpdated: 1, costsCleared: 1, desiUpdated: 1, desiCleared: 0, products: 1 }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { ProductTransferDialog, productTransferExportHref } from './ProductTransferDialog'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

async function renderDialog(onClose = vi.fn()) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><ProductTransferDialog onClose={onClose} /></QueryClientProvider>) })
  await settle()
}

const button = (text: string) => Array.from(document.body.querySelectorAll('button')).find(item => item.textContent?.includes(text))
const exportLink = () => Array.from(document.body.querySelectorAll('a')).find(item => item.textContent?.includes("Excel'e Aktar"))!

async function chooseFile(name: string) {
  const input = document.body.querySelector<HTMLInputElement>('input[type="file"]')!
  Object.defineProperty(input, 'files', { configurable: true, value: [new File(['x'], name)] })
  await act(async () => { input.dispatchEvent(new Event('change', { bubbles: true })) })
  await settle()
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  resetAnsweredIdempotencyKeys()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

describe('ProductTransferDialog', () => {
  it('builds the export link for every variant or only those missing a cost or a desi', () => {
    expect(productTransferExportHref('all')).toBe('/api/v1/products/transfer/export.xlsx')
    expect(productTransferExportHref('cost')).toBe('/api/v1/products/transfer/export.xlsx?missing=cost')
    expect(productTransferExportHref('desi')).toBe('/api/v1/products/transfer/export.xlsx?missing=desi')
  })

  it('shows how many variants lack a cost or a desi and exports the chosen set', async () => {
    await renderDialog()

    expect(document.body.textContent).toContain('3.666 tanesinde maliyet')
    expect(document.body.textContent).toContain('3.646 tanesinde desi')
    expect(exportLink().getAttribute('href')).toBe('/api/v1/products/transfer/export.xlsx')
    const select = document.body.querySelector<HTMLSelectElement>('.pci-select')!
    await act(async () => {
      select.value = 'desi'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(exportLink().getAttribute('href')).toBe('/api/v1/products/transfer/export.xlsx?missing=desi')
  })

  it('refuses a file that is neither .xlsx nor .csv without uploading it', async () => {
    await renderDialog()
    await chooseFile('urunler.pdf')

    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain('.xlsx ya da .csv')
    expect(apiState.calls.some(call => call.path === '/products/transfer/import')).toBe(false)
  })

  it('previews the upload, then saves exactly the previewed changes with an idempotency key', async () => {
    await renderDialog()
    await chooseFile('urunler.xlsx')

    const upload = apiState.calls.find(call => call.path === '/products/transfer/import')!
    expect(upload.method).toBe('POST')
    expect(upload.body).toBeInstanceOf(FormData)
    const rows = Array.from(document.body.querySelectorAll('.pci-tbl tbody tr'))
    expect(rows).toHaveLength(3)
    expect(rows[0].textContent).toContain('Eşleşmedi')
    expect(rows[1].textContent).toContain('Güncellenecek')
    expect(rows[1].textContent).toContain('2,5')
    expect(rows[2].textContent).toContain('Silinecek')
    expect(document.body.textContent).toContain('1 varyantın maliyeti silinecek')
    expect(document.body.textContent).toContain('kaydedilmez')

    const save = button('Değişiklikleri Kaydet (2)')!
    expect(save.disabled).toBe(false)
    await act(async () => { save.click() })
    await settle()

    const applied = apiState.calls.find(call => call.path === '/products/transfer/apply')!
    expect(applied.headers['idempotency-key']).toMatch(/^product-transfer:/)
    expect(JSON.parse(applied.body as string)).toEqual({ changes: [{ variantId: 'v1', version: 2, setCost: true, cost: 123.45, setDesi: true, desi: 2.5 }, { variantId: 'v2', version: 4, setCost: true, cost: null, setDesi: false, desi: null }] })
    expect(document.body.textContent).toContain('Değişiklikler kaydedildi')
    expect(document.body.textContent).toContain('2 varyant kaydedildi (1 ürün): 1 maliyet güncellendi, 1 maliyet silindi, 1 desi güncellendi.')
  })
})
