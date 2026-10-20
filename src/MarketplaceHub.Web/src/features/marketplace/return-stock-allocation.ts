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

// The return as an approval answers it: each line with the units already decided for stock.
export type ApprovedReturn = { status?: string; lines?: Array<{ id: string; disposedQuantity?: number }> | null }

// Units put back in stock on the given return lines, read from the server's answer (not the units
// that were asked for).
export function stockedUnits(result: ApprovedReturn | null | undefined, lineIds: readonly string[]) {
  const wanted = new Set(lineIds)
  return (result?.lines ?? []).filter(line => wanted.has(line.id)).reduce((sum, line) => sum + (line.disposedQuantity ?? 0), 0)
}
