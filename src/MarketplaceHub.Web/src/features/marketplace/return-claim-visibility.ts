// Cancelled claims and the split of a claim into its waiting and accepted cards are decided by the
// server (GET /returns); a line without a known status still waits for the seller.
export function isActionableReturnLine(line: { status?: string | null }) {
  return !line.status || line.status.toUpperCase() === 'ACTION_REQUIRED'
}
