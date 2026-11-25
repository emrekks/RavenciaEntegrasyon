import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { resetAnsweredIdempotencyKeys } from '../../shared/idempotency'
import { sendInvoiceDocumentUpload } from './invoice-copy'

const file = { name: 'ornek-fatura.pdf', size: 2048, lastModified: 1_700_000_000_000 }

describe('invoice document upload key', () => {
  afterEach(() => resetAnsweredIdempotencyKeys())

  it('sends a retry after a refusal under a new key, once per press', async () => {
    const send = vi.fn<(key: string) => Promise<string>>()
      .mockRejectedValueOnce(new ApiRequestError('Belge okunamadı.', 422, 'DOCUMENT_INVALID'))
      .mockResolvedValueOnce('ok')
    await expect(sendInvoiceDocumentUpload('inv-1', file, send)).rejects.toThrow('Belge okunamadı.')
    await expect(sendInvoiceDocumentUpload('inv-1', file, send)).resolves.toBe('ok')
    expect(send).toHaveBeenCalledTimes(2)
    expect(send.mock.calls[1][0]).not.toBe(send.mock.calls[0][0])
  })

  it('keeps the key after a lost answer, so the first upload is replayed rather than stored twice', async () => {
    const send = vi.fn<(key: string) => Promise<string>>()
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce('ok')
    await expect(sendInvoiceDocumentUpload('inv-2', file, send)).rejects.toThrow()
    await sendInvoiceDocumentUpload('inv-2', file, send)
    expect(send.mock.calls[1][0]).toBe(send.mock.calls[0][0])
  })
})
