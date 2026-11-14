// A bulk invoice press ("Toplu Fatura Kes") answers while some invoices are still going (owner, 14 Nov 2026: "2 siparişin
// faturası işlendi: 1 başarılı, 0 başarısız, 1 devam ediyor" and the second one never said how it ended). The pending
// ones are followed by reading their status only; nothing is submitted again, and the title never claims "Kesildi"
// while any is open.

export type BulkInvoiceState = 'done' | 'failed' | 'pending'
export type BulkInvoiceFollow = { packageId: string; label: string; invoiceId: string | null; jobId: string | null }
export type BulkInvoiceRead = { state: BulkInvoiceState; reason?: string }

const failedStatuses = new Set(['FAILED', 'BLOCKED', 'VALIDATION_FAILED', 'PREVIEW_STALE', 'CONFLICT', 'REJECTED', 'MARKETPLACE_FAILED', 'MANUAL_REVIEW', 'CANCELLED', 'CANCELLED_LOCAL'])

/** An invoice read: COMPLETED is done, a failure status is failed, anything else is still going. */
export function invoiceReadState(status: string | null | undefined, reason?: string | null): BulkInvoiceRead {
  const code = status?.trim().toUpperCase() ?? ''
  if (code === 'COMPLETED') return { state: 'done' }
  if (failedStatuses.has(code)) return { state: 'failed', reason: reason?.trim() || undefined }
  return { state: 'pending' }
}

/** A background job read (a queued invoice): SUCCEEDED is done, a stopped job is failed, the rest is still going. */
export function jobReadState(status: string | null | undefined, reason?: string | null): BulkInvoiceRead {
  const code = status?.trim().toUpperCase() ?? ''
  if (code === 'SUCCEEDED') return { state: 'done' }
  if (['BLOCKED', 'DEAD', 'CANCELLED', 'MANUAL_REVIEW'].includes(code)) return { state: 'failed', reason: reason?.trim() || undefined }
  return { state: 'pending' }
}

export type BulkInvoiceNotice = { title: string; text: string; kind: 'success' | 'error' | 'info' | 'pending' }

/** The one notice of a bulk press by what is known: still open is pending (stays until final), then the final result. */
export function bulkInvoiceNotice(total: number, succeeded: number, failures: ReadonlyArray<{ label: string; reason?: string }>, pending: number): BulkInvoiceNotice {
  if (pending > 0) {
    const parts = [succeeded ? `${succeeded} kesildi` : '', failures.length ? `${failures.length} kesilemedi` : '', `${pending} devam ediyor`].filter(Boolean)
    return { title: 'Toplu Fatura Gönderiliyor', text: `${total} siparişin faturası: ${parts.join(', ')}; sonuç gelince burada güncellenecek.`, kind: 'pending' }
  }
  if (!failures.length) return { title: 'Toplu Fatura Kesildi', text: `${total} siparişin faturası kesildi.`, kind: 'success' }
  const reasons = [...new Set(failures.map(item => item.reason).filter((reason): reason is string => Boolean(reason)))].slice(0, 2).join('; ')
  const names = failures.slice(0, 3).map(item => item.label).join(', ') + (failures.length > 3 ? ` ve ${failures.length - 3} sipariş daha` : '')
  const detail = `${names}${reasons ? `: ${reasons}` : ''}`
  return {
    title: succeeded ? 'Toplu Fatura Kısmen Kesildi' : 'Toplu Fatura Kesilemedi',
    text: succeeded ? `${succeeded} kesildi, ${failures.length} kesilemedi — ${detail}` : `${failures.length} siparişin faturası kesilemedi — ${detail}`,
    kind: 'error'
  }
}

export type FollowInvoiceOptions = { pollMs?: number; slowPollMs?: number; fastForMs?: number; maxMs?: number; sleep?: (ms: number) => Promise<void>; now?: () => number }

/**
 * Reads the pending invoices until each is final, quickly at first and then slowly, up to maxMs. Calls onSettled once per
 * invoice as it ends; what is still open at the end comes back (unknown stays unknown).
 */
export async function followPendingInvoices(
  pending: readonly BulkInvoiceFollow[],
  read: (item: BulkInvoiceFollow) => Promise<BulkInvoiceRead>,
  onSettled: (item: BulkInvoiceFollow, result: BulkInvoiceRead) => void,
  options: FollowInvoiceOptions = {}
) {
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms)))
  const now = options.now ?? Date.now
  const started = now()
  const open = new Map(pending.map(item => [item.packageId, item]))
  while (open.size) {
    const elapsed = now() - started
    if (elapsed >= (options.maxMs ?? 10 * 60_000)) break
    await sleep(elapsed >= (options.fastForMs ?? 60_000) ? options.slowPollMs ?? 10_000 : options.pollMs ?? 2_000)
    await Promise.all([...open.values()].map(async item => {
      // A failed read counts as not known yet.
      const result = await read(item).catch((): BulkInvoiceRead => ({ state: 'pending' }))
      if (result.state === 'pending') return
      open.delete(item.packageId)
      onSettled(item, result)
    }))
  }
  return [...open.values()]
}
