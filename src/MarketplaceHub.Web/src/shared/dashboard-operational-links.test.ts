import { describe, expect, it } from 'vitest'
import { dashboardOperationalLinks, resolveInvoiceTab, resolveReturnStatus } from './dashboard-operational-links'

describe('dashboard operational metric destinations', () => {
  it('opens pending orders and action-required returns in their matching views', () => {
    expect(dashboardOperationalLinks.pendingOrders).toBe('/orders?status=PENDING')
    expect(dashboardOperationalLinks.pendingReturns).toBe('/returns?status=ACTION_REQUIRED')
    expect(resolveReturnStatus(new URL(dashboardOperationalLinks.pendingReturns, 'https://panel.ravencia.com').searchParams.get('status'))).toBe('ACTION_REQUIRED')
  })

  it('opens invoice metrics in the matching invoice tab', () => {
    expect(dashboardOperationalLinks.pendingInvoices).toBe('/invoices?tab=UNINVOICED')
    expect(dashboardOperationalLinks.dueInvoices).toBe('/invoices?tab=DUE_SOON')
    expect(resolveInvoiceTab(new URL(dashboardOperationalLinks.dueInvoices, 'https://panel.ravencia.com').searchParams.get('tab'))).toBe('DUE_SOON')
  })

  it('falls back safely for unsupported view parameters', () => {
    expect(resolveReturnStatus('UNKNOWN')).toBe('ALL')
    expect(resolveInvoiceTab('UNKNOWN')).toBe('UNINVOICED')
  })
})
