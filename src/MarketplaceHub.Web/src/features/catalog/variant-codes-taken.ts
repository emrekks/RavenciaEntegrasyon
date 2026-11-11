import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { variantCodeMatchKey } from './platform-code-rules'
import { variantCodeKey } from './product-editor-state'

export type TakenVariantCode = { code: string; field: string; productTitle?: string | null }
type CodeRow = { barcode: string; sku: string }

// The codes of the table another product's variant holds, as variantCodeConflicts keys them (field and code → the
// holding card's title): a barcode another card has as its barcode, a stock code another card has as its stock code,
// compared as the server compares them (variantCodeMatchKey).
export function takenVariantCodeHolders(rows: readonly CodeRow[], taken: readonly TakenVariantCode[]): Record<string, string> {
  const result: Record<string, string> = {}
  for (const item of taken) {
    const field = item.field === 'barcode' ? 'barcode' : item.field === 'sku' ? 'sku' : null
    const key = variantCodeMatchKey(item.code)
    if (!field || !key) continue
    for (const row of rows) {
      if (variantCodeMatchKey(row[field]) === key) result[variantCodeKey(field, row[field])] = item.productTitle?.trim() || 'başka bir ürün'
    }
  }
  return result
}

// The codes typed in the editor's table checked against the catalog while they are typed (owner, 11.11.2026), not only
// when Kaydet is refused: 400 ms after the last change one request asks which of them another product holds. The
// table's own saved variants (and the rows waiting to be deleted) are left out, so a row's own code is not "taken".
export function useTakenVariantCodes(rows: readonly CodeRow[], ownVariantIds: readonly string[]) {
  const codes = useMemo(() => [...new Set(rows.flatMap(row => [row.barcode.trim(), row.sku.trim()]).filter(Boolean))].sort().slice(0, 1000), [rows])
  const exclude = useMemo(() => [...new Set(ownVariantIds)].sort(), [ownVariantIds])
  const request = JSON.stringify({ codes, excludeVariantIds: exclude })
  const [settled, setSettled] = useState(request)
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(request), 400)
    return () => window.clearTimeout(timer)
  }, [request])
  const taken = useQuery({
    queryKey: ['variant-codes-taken', settled],
    queryFn: () => hubApi<{ items?: TakenVariantCode[] }>('/catalog/variant-codes/taken', { method: 'POST', body: settled }),
    enabled: codes.length > 0 && settled === request,
    staleTime: 30_000
  })
  const items = taken.data?.items
  return useMemo(() => (codes.length && items ? takenVariantCodeHolders(rows, items) : {}), [codes.length, items, rows])
}
