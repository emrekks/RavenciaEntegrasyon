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

  it('give every tab except Tüm İşlemler at least one type', () => {
    for (const tab of jobCategoryTabs.filter(item => item.key !== 'ALL')) expect(jobTypesForCategoryTab(tab.key)!.length).toBeGreaterThan(0)
  })
})
