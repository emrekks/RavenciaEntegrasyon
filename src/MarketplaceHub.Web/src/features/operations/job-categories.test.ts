import { describe, expect, it } from 'vitest'
import { jobCategoryTabs, jobTypesForCategoryTab, knownJobTypes } from './job-categories'

// The server source sits outside Vite's allowed root, so it is read with Node's fs (the web build has no Node types).
// The edge image build copies only the web folder; there the check is skipped (CI's frontend job runs it).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { existsSync(path: URL): boolean; readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const serverFile = (file: string) => new URL('../../../../MarketplaceHub.Application/' + file, testFileUrl)
const serverSourceAvailable = fs.existsSync(serverFile('MarketplaceContracts.cs')) && fs.existsSync(serverFile('InvoicingContracts.cs'))

function constantsOf(file: string, className: string) {
  const source = fs.readFileSync(serverFile(file), 'utf8')
  const start = source.indexOf(`public static class ${className}`)
  const end = source.indexOf('\n}', start)
  return [...source.slice(start, end).matchAll(/public const string \w+ = "([A-Z0-9_]+)";/g)].map(match => match[1])
}

describe('job category tabs (G22)', () => {
  it.skipIf(!serverSourceAvailable)('know every job type the server defines', () => {
    const serverTypes = [...constantsOf('MarketplaceContracts.cs', 'MarketplaceJobTypes'), ...constantsOf('InvoicingContracts.cs', 'InvoicingJobTypes')]
    expect(serverTypes.length).toBeGreaterThan(50)
    expect(serverTypes.filter(type => !knownJobTypes.includes(type))).toEqual([])
  })

  it('derive each tab’s types from the fixed catalogue, the same list on every call', () => {
    expect(jobTypesForCategoryTab('ALL')).toBeNull()
    const orders = jobTypesForCategoryTab('ORDERS')!
    expect(orders).toContain('TRENDYOL_ORDER_SYNC')
    expect(orders).toContain('SHIPMENT_ACTION')
    expect(orders).not.toContain('PRICE_INVENTORY_SYNC')
    expect(jobTypesForCategoryTab('ORDERS')).toBe(orders)
    expect(jobTypesForCategoryTab('INVOICES')).toContain('INVOICE_SUBMIT')
    expect(jobTypesForCategoryTab('CATALOG')).toContain('IMPORT_PREVIEW')
  })

  it('show every known type in at least one tab besides Tüm İşlemler', () => {
    const tabs = jobCategoryTabs.filter(item => item.key !== 'ALL')
    expect(knownJobTypes.filter(type => !tabs.some(tab => tab.match(type)))).toEqual([])
  })

  it('put reference reads in Ürün/Yayın, notifications in Sipariş/Kargo, questions and finance in a tab of their own kind', () => {
    for (const type of ['TRENDYOL_REFERENCE_SYNC', 'HEPSIBURADA_REFERENCE_SYNC', 'N11_REFERENCE_SYNC', 'PAZARAMA_REFERENCE_SYNC', 'PTTAVM_REFERENCE_SYNC', 'MARKETPLACE_QUESTION_SYNC'])
      expect(jobTypesForCategoryTab('CATALOG')).toContain(type)
    for (const type of ['TRENDYOL_WEBHOOK_INGEST', 'SHOPIFY_WEBHOOK_INGEST', 'HEPSIBURADA_WEBHOOK_INGEST'])
      expect(jobTypesForCategoryTab('ORDERS')).toContain(type)
    expect(jobTypesForCategoryTab('INVOICES')).toContain('TRENDYOL_FINANCE_SYNC')
    expect(jobTypesForCategoryTab('INVOICES')).toContain('PAZARAMA_FINANCE_SYNC')
    expect(jobTypesForCategoryTab('SYSTEM')).toContain('EXTERNAL_EFFECT_RECONCILE')
  })

  it('count the invoice checks under Faturalar only', () => {
    for (const type of ['ORDER_INVOICE_RECONCILIATION', 'SHOPIFY_ORDER_INVOICE_RECONCILIATION', 'HEPSIBURADA_ORDER_INVOICE_RECONCILIATION', 'MANUAL_ORDER_INVOICE_DELIVERY', 'INVOICE_DUE_SCAN']) {
      expect(jobTypesForCategoryTab('INVOICES')).toContain(type)
      expect(jobTypesForCategoryTab('ORDERS')).not.toContain(type)
      expect(jobTypesForCategoryTab('SYSTEM')).not.toContain(type)
    }
  })

  it('give every tab except Tüm İşlemler at least one type', () => {
    for (const tab of jobCategoryTabs.filter(item => item.key !== 'ALL')) expect(jobTypesForCategoryTab(tab.key)!.length).toBeGreaterThan(0)
  })
})
