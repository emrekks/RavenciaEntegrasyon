import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../../test/render'

vi.mock('../../../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../../../shared/api')>()),
  hubApi: vi.fn(() => new Promise(() => {}))
}))

import { defaultShippingLabelSettings, markShippingLabelPrinted } from '../../../shipping'
import type { Order } from '../../shared'
import { ShippingLabelBatchModal } from './OrderShippingModals'

afterEach(() => { cleanup(); localStorage.clear() })

const order = (id: string, labelPrintedAt: string | null) => ({
  id, labelPrintedAt, orderNumber: `N-${id}`, platformCode: 'TRENDYOL', customerName: 'Alıcı', cargoProviderName: 'Aras', cargoTrackingNumber: `T-${id}`,
  packages: [], lines: [], shipmentAddress: null
}) as unknown as Order

// WEB-ALTYAPI-5: the bulk print summary marks an order printed from the server's labelPrintedAt (every computer sees
// it), and from this device's list for a print whose server call failed; neither: waiting.
describe('the bulk label window', () => {
  it('marks printed orders by the server, then by this device', async () => {
    markShippingLabelPrinted('local', 'sticker')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { host } = render(<QueryClientProvider client={client}><ShippingLabelBatchModal items={[order('server', '2026-11-15T10:00:00Z'), order('local', null), order('none', null)]}
      settings={defaultShippingLabelSettings} format="sticker" onClose={() => undefined} onPrinted={() => undefined} /></QueryClientProvider>)
    const rows = await waitFor(() => { const found = [...(host.ownerDocument.querySelectorAll<HTMLElement>('.rr-sum > div'))]; return found.length === 3 ? found : null })
    expect(rows.map(row => row.textContent?.includes('Yazdırıldı'))).toEqual([true, true, false])
    expect(rows[2].textContent).toContain('Bekliyor')
  })
})
