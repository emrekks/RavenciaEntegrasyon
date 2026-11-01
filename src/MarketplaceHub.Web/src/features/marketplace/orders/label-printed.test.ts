import { describe, expect, it, vi } from 'vitest'

const hubApi = vi.fn((_path: string, _init?: unknown) => Promise.resolve())
vi.mock('../../../shared/api', () => ({ hubApi: (path: string, init?: unknown) => hubApi(path, init) }))
const markShippingLabelPrinted = vi.fn()
vi.mock('../../shipping', () => ({ markShippingLabelPrinted: (id: string, format: string) => { markShippingLabelPrinted(id, format) } }))

const { recordShippingLabelsPrinted } = await import('./label-printed')

describe('recording printed shipping labels', () => {
  it('reads the order lists again once the server kept the print', async () => {
    const invalidateQueries = vi.fn(() => Promise.resolve())
    const onPrinted = vi.fn()

    await recordShippingLabelsPrinted(['o1', 'o2'], 'sticker', onPrinted, { invalidateQueries })

    expect(markShippingLabelPrinted).toHaveBeenCalledWith('o1', 'sticker')
    expect(hubApi).toHaveBeenCalledWith('/orders/o2/label-printed', { method: 'POST' })
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['orders'] })
    expect(onPrinted).toHaveBeenCalledTimes(2)
  })
})
