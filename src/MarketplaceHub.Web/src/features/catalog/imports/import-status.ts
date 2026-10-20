// İçe aktarım oturumu durumları (ImportSessionStatus, sunucu `CatalogModelConfiguration.ImportStatusWire`).
export type ImportSessionStatus = 'CREATED' | 'FETCHING' | 'MATCHING' | 'REVIEW_REQUIRED' | 'READY_TO_APPLY' | 'APPLYING' | 'COMPLETED' | 'PARTIALLY_COMPLETED' | 'FAILED' | 'CANCELLED'

/** Finished imports: nothing changes on the server any more. */
export const finishedImportStatuses: ReadonlySet<string> = new Set(['COMPLETED', 'PARTIALLY_COMPLETED', 'FAILED', 'CANCELLED'])

// States the worker moves on by itself. READY_TO_APPLY is included because a queued apply job leaves the session in
// that state until the worker picks the job up and moves it to APPLYING.
const workerStatuses: ReadonlySet<string> = new Set(['FETCHING', 'MATCHING', 'READY_TO_APPLY', 'APPLYING'])

export const importSessionPollMs = 4000

/**
 * How often the import detail page re-reads its session: only while the worker is moving it. A finished import and
 * one that waits for the person (a file to upload, decisions to make) is not polled (W32).
 */
export function importSessionPollInterval(status: string | undefined): number | false {
  if (!status || finishedImportStatuses.has(status)) return false
  return workerStatuses.has(status) ? importSessionPollMs : false
}
