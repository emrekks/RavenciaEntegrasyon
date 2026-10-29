// Cancelled claims and the split of a claim into its waiting and accepted cards are decided by the
// server (GET /returns); a line without a known status still waits for the seller. A line a sent
// decision still covers waits for that decision's outcome, while the claim's other lines stay open
// for their own decision.
export function isActionableReturnLine(line: { status?: string | null; decisionPending?: boolean }) {
  return line.decisionPending !== true && (!line.status || line.status.toUpperCase() === 'ACTION_REQUIRED')
}
