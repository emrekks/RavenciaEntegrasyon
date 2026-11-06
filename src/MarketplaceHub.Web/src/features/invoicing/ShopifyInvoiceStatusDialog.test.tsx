import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ShopifyInvoiceStatusDialog, shopifyInvoiceStatusHint } from './ShopifyInvoiceStatusDialog'
import { ReturnInvoiceRing } from '../marketplace/shared'

const sent = vi.hoisted(() => [] as Array<{ path: string; body: unknown }>)
vi.mock('../../shared/api', async importOriginal => ({
  ...await importOriginal<typeof import('../../shared/api')>(),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    sent.push({ path, body: init?.body ? JSON.parse(String(init.body)) : null })
    return { updatedCount: 1 }
  })
}))

// node:fs without @types/node, the way orders-sade-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const source = (path: string) => fs.readFileSync(new URL(path, testFileUrl), 'utf8')

let container: HTMLDivElement
let root: Root
const feedback: Array<[string, string]> = []
const render = (props: { invoiceStatus: string; manualInvoiceStatus?: string | null }) => act(() => root.render(<QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
  <ShopifyInvoiceStatusDialog orderNumber="#1001" {...props} resolvePackageId={() => 'shopify-package-1'} onClose={() => {}} onFeedback={(message, kind) => feedback.push([message, kind])} />
</QueryClientProvider>))
const select = () => document.querySelector<HTMLSelectElement>('select[aria-label="Yeni fatura durumu"]')!
const save = () => Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Durumu Kaydet')!
const choose = (value: string) => act(() => {
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set?.call(select(), value)
  select().dispatchEvent(new Event('change', { bubbles: true }))
})

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  sent.length = 0
  feedback.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(() => { act(() => root.unmount()); container.remove() })

// Audit W13 (owner, 6 Nov 2026): one Shopify invoice status window with one rule on the invoice page and the orders
// page. "Faturası Yüklendi" is allowed without a document (the seller uploads it in Shopify) and is marked by hand.
describe('ShopifyInvoiceStatusDialog', () => {
  it('saves "Faturası Yüklendi" by hand for an order without any invoice document', async () => {
    render({ invoiceStatus: 'FATURA_BEKLIYOR' })
    choose('UPLOADED')
    expect(save().disabled).toBe(false)
    expect(document.body.textContent).toContain('belge eklemeden kaydedebilirsiniz')

    await act(async () => { save().click() })

    expect(sent).toEqual([{ path: '/invoice-workspace/manual-status', body: { packageIds: ['shopify-package-1'], status: 'UPLOADED' } }])
    expect(feedback).toEqual([['Shopify fatura durumu “Faturası yüklendi” olarak elle işaretlendi.', 'success']])
  })

  it('says the status only records a hand mark and issues or sends no invoice', () => {
    render({ invoiceStatus: 'FATURA_BEKLIYOR' })
    expect(shopifyInvoiceStatusHint).toContain('elle işaretlenir')
    expect(shopifyInvoiceStatusHint).toContain('Fatura kesilmez')
    expect(document.body.textContent).toContain('Fatura Durumunu Değiştir')
  })

  it('opens on the status set by hand and says it was set by hand', () => {
    render({ invoiceStatus: 'FATURA_KONTROLDE', manualInvoiceStatus: 'UPLOADED' })
    expect(select().value).toBe('UPLOADED')
    expect(document.body.textContent).toContain('Elle İşaretlendi')
  })

  it('is the one window both the invoice page and the orders page open, with no document rule of their own', () => {
    for (const path of ['./pages.tsx', '../marketplace/orders/OrdersPage.tsx']) {
      const code = source(path)
      expect(code, path).toContain('<ShopifyInvoiceStatusDialog ')
      expect(code, path).not.toContain('function ShopifyInvoiceStatusModal')
    }
    // The orders page's bulk window follows the same rule: no document needed for "Faturası Yüklendi".
    expect(source('../marketplace/orders/OrdersPage.tsx')).not.toContain("status === 'UPLOADED' && !item.invoiceDocumentUrl")
  })
})

describe('ReturnInvoiceRing', () => {
  it('notes a status set by hand under its label', () => {
    act(() => root.render(<ReturnInvoiceRing status="FATURA_KONTROLDE" isMicroExport={false} setByHand />))
    expect(container.querySelector('.rr-ring small')?.textContent).toBe('Elle İşaretlendi')
    act(() => root.render(<ReturnInvoiceRing status="FATURA_KONTROLDE" isMicroExport={false} />))
    expect(container.querySelector('.rr-ring small')).toBeNull()
  })
})
