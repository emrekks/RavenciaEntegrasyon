export function visibleReturnClaims<T extends { status: string }>(claims: readonly T[]): T[] {
  return claims.filter(claim => !['CANCELLED', 'CANCELED'].includes(claim.status.trim().toUpperCase()))
}

type SplittableClaim = {
  id: string
  status: string
  rawStatus: string
  productCount: number
  lines: { id: string }[] | null
  lineStatuses?: Record<string, string> | null
  rowKey?: string
}

const settledLineStatuses = new Set(['APPROVED', 'COMPLETED'])

// A claim whose lines are partly accepted and partly still waiting for the seller is shown as two
// cards: the waiting lines under "Aksiyon bekleyen", the accepted ones under "Onaylanan". Once every
// line is accepted the claim is a single approved card again.
export function splitMixedReturnClaims<T extends SplittableClaim>(claims: readonly T[]): T[] {
  return claims.flatMap(claim => {
    const statuses = claim.lineStatuses
    const lines = claim.lines
    if (!statuses || !lines || lines.length < 2) return [claim]
    const accepted = lines.filter(line => settledLineStatuses.has((statuses[line.id] ?? '').toUpperCase()))
    const waiting = lines.filter(line => !statuses[line.id] || statuses[line.id].toUpperCase() === 'ACTION_REQUIRED')
    if (!accepted.length || !waiting.length) return [claim]
    return [
      { ...claim, lines: waiting, productCount: waiting.length, rowKey: `${claim.id}:waiting` },
      { ...claim, status: 'APPROVED', rawStatus: 'Accepted', lines: accepted, productCount: accepted.length, rowKey: `${claim.id}:approved` }
    ]
  })
}

export function isActionableReturnLine(line: { status?: string | null }) {
  return !line.status || line.status.toUpperCase() === 'ACTION_REQUIRED'
}
