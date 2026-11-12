// Ayarlar › Veri › Pazaryerleri: several marketplace connections get their Tam Sıfırlama one after the other, with the
// page's own "Verileri sil" confirmation as the gate. The server runs each reset as a job; this follows the job.
import { connectionFullResetRunning, type ConnectionFullResetView } from '../marketplace/connection-data-reset'

export type BulkResetConnection = { id: string; displayName: string; version: number }
export type BulkResetProgress =
  | { state: 'waiting' }
  | { state: 'running'; label: string | null; percent: number | null }
  | { state: 'done' }
  | { state: 'failed'; message: string }

// What a connection's full reset removes: the counts of its view; product links name products that are already
// counted (removed) or kept, so they are not added.
export function marketplaceResetCount(view: Pick<ConnectionFullResetView, 'items'> | undefined) {
  return view ? view.items.filter(entry => entry.scope !== 'PRODUCT_LINKS').reduce((sum, entry) => sum + entry.count, 0) : undefined
}

// The total of the receipt: the catalog counts and every chosen connection's count; unknown while one is still loading.
export function marketplaceResetTotal(chosen: string[], counts: Record<string, number | undefined>) {
  let total = 0
  for (const id of chosen) {
    const value = counts[id]
    if (value === undefined) return undefined
    total += value
  }
  return total
}

export function progressText(progress: BulkResetProgress | undefined) {
  if (!progress) return null
  if (progress.state === 'waiting') return 'Sırada bekliyor'
  if (progress.state === 'running') return `${progress.label ?? 'Sıfırlama başlatıldı'}${progress.percent !== null ? ` · %${progress.percent}` : ''}`
  if (progress.state === 'done') return 'Tamamlandı'
  return progress.message
}

export type BulkResetDeps = {
  request: (connection: BulkResetConnection) => Promise<ConnectionFullResetView>
  poll: (connection: BulkResetConnection) => Promise<ConnectionFullResetView>
  sleep: (ms: number) => Promise<void>
  onProgress: (connectionId: string, progress: BulkResetProgress) => void
  isCancelled?: () => boolean
  pollMs?: number
}

// One request at a time, each followed until its job ends. A connection whose reset fails stops the run: whatever
// would follow (the other connections, the catalog cleanup) is left alone. Returns whether every reset succeeded.
export async function runMarketplaceResets(connections: BulkResetConnection[], deps: BulkResetDeps) {
  for (const connection of connections) deps.onProgress(connection.id, { state: 'waiting' })
  for (const connection of connections) {
    try {
      let view = await deps.request(connection)
      const jobId = view.job?.jobId
      if (!jobId) throw new Error('Sıfırlama işi başlatılamadı.')
      for (;;) {
        const job = view.job
        if (job && job.jobId === jobId) {
          if (!connectionFullResetRunning(job.status)) {
            if (job.status === 'SUCCEEDED') { deps.onProgress(connection.id, { state: 'done' }); break }
            throw new Error(job.status === 'CANCELLED' ? 'Sıfırlama iptal edildi.' : `Sıfırlama tamamlanamadı${job.progressLabel ? `: ${job.progressLabel}` : '.'}`)
          }
          deps.onProgress(connection.id, { state: 'running', label: job.progressLabel, percent: job.progressPercent })
        } else deps.onProgress(connection.id, { state: 'running', label: 'Sırada bekliyor', percent: null })
        if (deps.isCancelled?.()) return false
        await deps.sleep(deps.pollMs ?? 2500)
        if (deps.isCancelled?.()) return false
        view = await deps.poll(connection)
      }
    } catch (reason) {
      deps.onProgress(connection.id, { state: 'failed', message: reason instanceof Error ? reason.message : 'Sıfırlama tamamlanamadı.' })
      return false
    }
  }
  return true
}
