import { describe, expect, it } from 'vitest'
import { needsOrderNumberSearch } from './invoice-order-number-search'

describe('needsOrderNumberSearch', () => {
  it('asks for an order-number search only where E-Faturam keeps its own reference', () => {
    expect(needsOrderNumberSearch({ invoiceErrorCode: 'EFATURAM_UUID_NOT_HONOURED' })).toBe(true)
    expect(needsOrderNumberSearch({ invoiceErrorCode: 'EFATURAM_INVOICE_NOT_FOUND_BY_UUID' })).toBe(false)
    expect(needsOrderNumberSearch({ invoiceErrorCode: null })).toBe(false)
  })
})
