import { describe, expect, it } from 'vitest'
import { bulkInvoiceNotice, followPendingInvoices, invoiceReadState, jobReadState, type BulkInvoiceFollow, type BulkInvoiceRead } from './invoice-bulk-outcome'

describe('bulk invoice notice', () => {
  it('does not claim Kesildi while one is still going', () => {
    expect(bulkInvoiceNotice(2, 1, [], 1)).toEqual({ title: 'Toplu Fatura Gönderiliyor', text: '2 siparişin faturası: 1 kesildi, 1 devam ediyor; sonuç gelince burada güncellenecek.', kind: 'pending' })
  })

  it('says the final result once all ended', () => {
    expect(bulkInvoiceNotice(2, 2, [], 0)).toEqual({ title: 'Toplu Fatura Kesildi', text: '2 siparişin faturası kesildi.', kind: 'success' })
    expect(bulkInvoiceNotice(2, 1, [{ label: '#123', reason: 'Vergi no eksik.' }], 0)).toMatchObject({ title: 'Toplu Fatura Kısmen Kesildi', text: '1 kesildi, 1 kesilemedi — #123: Vergi no eksik.', kind: 'error' })
    expect(bulkInvoiceNotice(2, 0, [{ label: '#1' }, { label: '#2' }], 0)).toMatchObject({ title: 'Toplu Fatura Kesilemedi', kind: 'error' })
  })
})

describe('reading the invoice and job states', () => {
  it('maps statuses', () => {
    expect(invoiceReadState('Completed').state).toBe('done')
    expect(invoiceReadState('SUBMITTING').state).toBe('pending')
    expect(invoiceReadState('REJECTED', 'Reddedildi.')).toEqual({ state: 'failed', reason: 'Reddedildi.' })
    expect(jobReadState('SUCCEEDED').state).toBe('done')
    expect(jobReadState('LEASED').state).toBe('pending')
    expect(jobReadState('BLOCKED', 'Neden').state).toBe('failed')
  })
})

describe('following the pending invoices', () => {
  const item = (id: string): BulkInvoiceFollow => ({ packageId: id, label: `#${id}`, invoiceId: `inv-${id}`, jobId: null })

  it('reads again until each is final and reports each once', async () => {
    const reads: string[] = []
    let round = 0
    const settled: Array<[string, string]> = []
    const left = await followPendingInvoices([item('a'), item('b')], async pending => {
      reads.push(pending.packageId)
      round++
      return pending.packageId === 'a' ? { state: 'done' } : round > 3 ? { state: 'failed', reason: 'Neden' } : { state: 'pending' }
    }, (pending, result) => settled.push([pending.packageId, result.state]), { sleep: async () => undefined })
    expect(left).toEqual([])
    expect(settled).toEqual([['a', 'done'], ['b', 'failed']])
    expect(reads.filter(id => id === 'a')).toHaveLength(1)
  })

  it('gives up after maxMs and returns what is still open', async () => {
    let time = 0
    const left = await followPendingInvoices([item('a')], async (): Promise<BulkInvoiceRead> => ({ state: 'pending' }), () => undefined, { sleep: async ms => { time += ms }, now: () => time, maxMs: 30_000 })
    expect(left.map(entry => entry.packageId)).toEqual(['a'])
  })
})
