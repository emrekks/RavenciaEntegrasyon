export type ProductSyncProgress = {
  progressReceived: number
  progressTotal: number | null
  progressPercent: number | null
  progressProcessed: number
  progressSkipped: number
  progressFailed: number
}

// Share of the job that is finished, 0-100, or null while the total is not known (the bar then runs indeterminate).
// The server's percent wins; without it the finished records (processed + skipped + failed) are set against the total.
export function productSyncPercent(job: ProductSyncProgress): number | null {
  if (job.progressPercent != null) return Math.min(100, Math.max(0, Math.floor(job.progressPercent)))
  const received = Math.max(0, job.progressReceived)
  const total = job.progressTotal != null && job.progressTotal >= received ? job.progressTotal : null
  if (total == null || total <= 0) return null
  const done = Math.max(0, job.progressProcessed) + Math.max(0, job.progressSkipped) + Math.max(0, job.progressFailed)
  return Math.min(100, Math.floor(done * 100 / total))
}

// The server writes its counts with English grouping ("1,018") and leads with the received count the line below repeats.
export function productSyncLabel(label: string): string {
  return label
    .replace(/^\s*[\d.,]+(?:\s*\/\s*[\d.,]+)?\s*·\s*/, '')
    .replace(/\d{1,3}(?:,\d{3})+/g, group => group.replace(/,/g, '.'))
}
