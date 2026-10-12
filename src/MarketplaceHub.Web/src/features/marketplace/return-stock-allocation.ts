export type StockSourceLine = { id: string; quantity: number }
export type StockLineRequest = { returnLineId: string; quantity: number }

// A product can be returned as several lines. "Add N units to stock" is spread over its lines in
// order, so stock never goes up by more than the customer actually sent back.
export function allocateStock(lines: readonly StockSourceLine[], units: number): StockLineRequest[] {
  const requests: StockLineRequest[] = []
  let remaining = Math.max(0, Math.floor(units))
  for (const line of lines) {
    if (remaining <= 0) break
    const quantity = Math.min(remaining, Math.max(0, line.quantity))
    if (quantity <= 0) continue
    requests.push({ returnLineId: line.id, quantity })
    remaining -= quantity
  }
  return requests
}

export const totalUnits = (lines: readonly StockSourceLine[]) => lines.reduce((sum, line) => sum + Math.max(0, line.quantity), 0)
