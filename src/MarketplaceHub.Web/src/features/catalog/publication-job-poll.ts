// How often the product editor re-reads a publication job. A job the worker will not move on by itself (finished,
// waiting for a person in manual review, blocked) or one the server no longer has (404, or any read error, since the
// query does not retry) is not polled again; it used to be re-read every 2.5 s forever.
const settledJobStatuses: ReadonlySet<string> = new Set(['SUCCEEDED', 'DEAD', 'CANCELLED', 'MANUAL_REVIEW', 'BLOCKED'])

export const publicationJobPollMs = 2500

export function publicationJobPollInterval(status: string | undefined, failed: boolean): number | false {
  if (failed) return false
  const normalized = status?.trim().toUpperCase()
  return normalized && settledJobStatuses.has(normalized) ? false : publicationJobPollMs
}
