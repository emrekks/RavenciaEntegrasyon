import { describe, expect, it } from 'vitest'
import { QueryClient } from '@tanstack/react-query'
import { realtimeQueryKeysFor } from '../../../app/realtime-query-keys'
import { orderDetailQueryKey } from './order-query-keys'

describe('order detail query key', () => {
  it('is refreshed by the order invalidations and realtime order events', async () => {
    const client = new QueryClient()
    client.setQueryData(orderDetailQueryKey('order-1'), { invoiceStatus: 'PENDING' })
    await client.invalidateQueries({ queryKey: ['orders'] })
    expect(client.getQueryState(orderDetailQueryKey('order-1'))?.isInvalidated).toBe(true)

    client.setQueryData(orderDetailQueryKey('order-2'), { invoiceStatus: 'PENDING' })
    for (const queryKey of realtimeQueryKeysFor(['invoices'])) await client.invalidateQueries({ queryKey })
    expect(client.getQueryState(orderDetailQueryKey('order-2'))?.isInvalidated).toBe(true)
  })
})
