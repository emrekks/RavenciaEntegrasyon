import { statusLabel } from '../../shared/status-labels'

// What a read job pulled (JobReadRecorder on the server): counts that always add up and up to 100 records, problems
// first. r: order, package or claim number; s: the second number (a claim's order, an order's packages); st: status.
export type JobReadRecord = { r: string; s?: string | null; st?: string | null; o: string; why?: string | null }
export type JobReadCounts = { received: number; inserted: number; updated: number; unchanged: number; skipped: number; failed: number }
export type JobReadSummary = { kind: string; counts: JobReadCounts; omitted: number; records: JobReadRecord[]; sentence: string }

export type ReadTone = 'success' | 'warning' | 'danger' | 'neutral' | 'info'

const outcomes: Record<string, { label: string; tone: ReadTone }> = {
  NEW: { label: 'Yeni', tone: 'success' },
  UPDATED: { label: 'Güncellendi', tone: 'info' },
  UNCHANGED: { label: 'Değişmedi', tone: 'neutral' },
  SKIPPED: { label: 'Atlandı', tone: 'warning' },
  FAILED: { label: 'Hatalı', tone: 'danger' }
}

export const readOutcome = (outcome: string) => outcomes[outcome] ?? { label: outcome, tone: 'info' as const }

export const isReadProblem = (record: JobReadRecord) => record.o === 'FAILED' || record.o === 'SKIPPED'

const headings: Record<string, { reference: string; secondary: string }> = {
  ORDER: { reference: 'Sipariş No', secondary: 'Paket No' },
  RETURN: { reference: 'Talep No', secondary: 'Sipariş No' },
  INVOICE: { reference: 'Sipariş / Paket No', secondary: 'Paket / Fatura No' },
  PRODUCT: { reference: 'Model Kodu', secondary: 'Barkod' },
  QUESTION: { reference: 'Soru No', secondary: 'Ürün Kodu' },
  FINANCE: { reference: 'Hareket / Fatura No', secondary: 'Sipariş No' }
}
export const readHeadings = (kind: string) => headings[kind] ?? { reference: 'Kayıt', secondary: 'İlgili No' }

// A marketplace word ("WaitingInAction", "Picking") or a panel code ("SHIPPED"), in Turkish where known.
const invoiceStatuses: Record<string, string> = { INVOICED: 'Faturalı', NOTINVOICED: 'Faturasız', UNKNOWN: 'Bilinmiyor' }
export const readStatusLabel = (status: string | null | undefined) => {
  if (!status) return '—'
  const parts = status.split(', ')
  if (parts.every(part => invoiceStatuses[part.replace(/_/g, '').toUpperCase()])) return parts.map(part => invoiceStatuses[part.replace(/_/g, '').toUpperCase()]).join(', ')
  // A count ("3 varyant") or a marketplace's own word is shown as it came when it has no Turkish label.
  return /^[0-9]/.test(status) ? status : statusLabel(status)
}

export type ProgressCounts = { received: number; inserted: number | null; updated: number | null; processed: number; unchanged: number; skipped: number; failed: number }

// The detail's counters, which always add up: Okunan = Yeni + Güncellenen + Değişmeyen + Atlanan + Hatalı. From the
// record summary when the job stored one; otherwise from the job's counters, where the records that were neither
// processed, skipped nor failed are the unchanged ones (they used to vanish from the sum).
export function readProgressCounts(job: { progressReceived: number; progressCurrent: number; progressProcessed: number; progressSkipped: number; progressFailed: number }, read: JobReadSummary | null | undefined): ProgressCounts {
  if (read) {
    const c = read.counts
    return { received: c.received, inserted: c.inserted, updated: c.updated, processed: c.inserted + c.updated, unchanged: c.unchanged, skipped: c.skipped, failed: c.failed }
  }
  const received = Math.max(job.progressReceived, job.progressCurrent)
  const accounted = job.progressProcessed + job.progressSkipped + job.progressFailed
  return { received: Math.max(received, accounted), inserted: null, updated: null, processed: job.progressProcessed, unchanged: Math.max(0, received - accounted), skipped: job.progressSkipped, failed: job.progressFailed }
}

// A finished job is complete whatever its counters say; a running one shows its own percent or its share of the total.
export function jobProgressPercent(job: { status: string; progressPercent: number | null; progressTotal: number | null; progressCurrent: number }) {
  if (job.status === 'SUCCEEDED') return 100
  if (job.progressPercent !== null) return Math.max(0, Math.min(100, job.progressPercent))
  if (job.progressTotal && job.progressTotal > 0) return Math.max(0, Math.min(100, Math.round((job.progressCurrent / job.progressTotal) * 100)))
  return null
}

// The reads that keep their records (MarketplaceJobProcessor.RecordsReadProgress): order and return reads of every
// marketplace. An older job of these types ran before they kept them.
const recordedReads = /^(TRENDYOL_|SHOPIFY_|HEPSIBURADA_|N11_|PAZARAMA_|PTTAVM_)?(ORDER_SYNC|ORDER_RECOVERY_SYNC|ORDER_STATUS_SYNC|ORDER_RECONCILIATION|ORDER_INVOICE_RECONCILIATION|PRODUCT_SYNC|FINANCE_SYNC|SETTLEMENT_SYNC)$|^TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION$|^(HEPSIBURADA_)?RETURN_SYNC$|^RETURN_STATUS_SYNC$|^RETURN_RECONCILIATION$|^MARKETPLACE_QUESTION_SYNC$/
export const keepsReadRecords = (jobType: string) => recordedReads.test(jobType.trim().toUpperCase())
