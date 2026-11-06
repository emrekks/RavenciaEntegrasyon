import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const virtualPackage = 'order:PTTEM-U43FJA722-181026'
vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async () => [{
    orderId: 'order-1', packageId: 'pkg-1', providerConnectionId: 'provider-1', orderNumber: 'PTTEM-U43FJA722-181026', platformCode: 'PTTAVM', platformName: 'PttAVM', environment: 'PRODUCTION',
    customerType: 'CORPORATE', customerName: 'PTTEM Teknoloji', taxIdentityNumber: '7330638410', invoiceAddressJson: '{"address":"Beytepe Mahallesi","city":"ANKARA"}', invoiceType: 'EARCHIVE', currency: 'TRY',
    taxExclusiveTotal: 635.45, discountTotal: 0, taxTotal: 63.55, payableTotal: 699, lines: [], canConfirm: true, blockedReason: null,
    previewDigest: 'digest', existingInvoiceId: null, existingInvoiceStatus: null, nextAction: 'CREATE_AND_SUBMIT', includeInternetSalesInfo: false,
    targetPackageNumbers: [virtualPackage], deliveryCarrierName: null, deliverySentAt: null, deliveryPackageNumber: virtualPackage,
  }]),
}))

import { InvoiceWorkspacePreviewModal } from './InvoiceWorkspacePreviewModal'

let container: HTMLDivElement
let root: Root

async function open(role = 'ADMINISTRATOR') {
  const client = new QueryClient()
  client.setQueryData(['me'], { id: 'user-1', email: 'u@example.invalid', displayName: 'U', role, state: 'ACTIVE', tenantId: 'tenant-1' })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><InvoiceWorkspacePreviewModal targets={[{ orderId: 'order-1', packageId: 'pkg-1', providerConnectionId: 'provider-1' }]} onClose={() => {}} onComplete={() => {}} onNotice={() => {}} /></QueryClientProvider>)
  })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

function confirmButton() {
  return Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Onayla ve Faturayı Kes')!
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.appendChild(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('InvoiceWorkspacePreviewModal', () => {
  it('shows a virtual package by its code, without the order: prefix', async () => {
    await open()

    const chip = document.body.querySelector('.invoice-workspace-preview-package')
    const packageFact = document.body.querySelector('.invoice-workspace-preview-facts .is-package dd')
    expect(chip?.textContent).toBe('PTTEM-U43FJA722-181026')
    expect(packageFact?.textContent).toBe('PTTEM-U43FJA722-181026')
    expect(document.body.textContent).not.toContain('order:')
  })

  // Issuing the previewed invoices is for the owner and administrators; the preview itself stays open to the others.
  it('lets an administrator issue the previewed invoice', async () => {
    await open('ADMINISTRATOR')
    expect(confirmButton().disabled).toBe(false)
  })

  it.each(['OPERATIONS', 'ACCOUNTING'])('shows %s the preview but keeps issuing closed with the reason', async role => {
    await open(role)
    expect(document.body.querySelector('.invoice-workspace-preview-package')?.textContent).toBe('PTTEM-U43FJA722-181026')
    expect(confirmButton().disabled).toBe(true)
    expect(document.body.textContent).toContain('yalnız sahip ve yöneticiler içindir')
  })
})
