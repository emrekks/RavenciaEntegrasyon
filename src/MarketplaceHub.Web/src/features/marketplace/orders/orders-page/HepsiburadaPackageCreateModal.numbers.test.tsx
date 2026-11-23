import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ path: string; body: Record<string, unknown> }> }))
vi.mock('../../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string, init?: RequestInit) => { api.calls.push({ path, body: JSON.parse(String(init?.body ?? '{}')) }); return { packageNumber: 'PKG-1' } }) }
})

import { cleanup, render, waitFor } from '../../../../test/render'
import { HepsiburadaPackageCreateModal } from './OrderPlatformModals'
import type { Order } from '../../shared'

// The Desi and Paket Adedi boxes were type="number" read with Number(): "1.250" typed in a tr-TR browser became 1.25
// desi and "1e3" 1000 packages. Now Turkish text boxes: the number typed, or nothing sent with the reason.
const order = { id: 'o1', version: 4, orderNumber: 'HB-1', platformCode: 'HEPSIBURADA', customerName: 'Ayşe Yılmaz', orderedAt: '2026-11-20T10:00:00Z', packages: [], lines: [{ id: 'l1', title: 'Gömlek', orderedQuantity: 2, cancelledQuantity: 0, allocatedQuantity: 0, quantity: 2 }] } as unknown as Order

beforeEach(() => { Element.prototype.scrollIntoView = () => undefined })
afterEach(() => { cleanup(); api.calls.length = 0 })

async function sendWith(values: Record<string, string>) {
  const { host } = render(<HepsiburadaPackageCreateModal item={order} onClose={() => undefined} onCreated={() => undefined} />)
  const form = await waitFor(() => document.body.querySelector<HTMLFormElement>('.rr-modal form, form'))
  for (const [name, value] of Object.entries(values)) { const input = form.querySelector<HTMLInputElement>(`input[name="${name}"]`)!; expect(input.type).toBe('text'); input.value = value }
  await act(async () => { form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  return host
}

describe('Hepsiburada package numbers', () => {
  it('sends "1.250" desi as 1250 and the package count typed', async () => {
    await sendWith({ deci: '1.250', parcelQuantity: '2' })
    expect(api.calls.map(call => [call.body.deci, call.body.parcelQuantity])).toEqual([[1250, 2]])
  })
  it.each([[{ deci: '1.25' }, 'Desi okunamadı'], [{ parcelQuantity: '1e3' }, 'Paket Adedi okunamadı'], [{ parcelQuantity: '2,5' }, 'Paket Adedi okunamadı']])('sends nothing for %o', async (values, reason) => {
    await sendWith(values as Record<string, string>)
    expect(api.calls).toEqual([])
    expect(document.body.textContent).toContain(reason)
  })
})
