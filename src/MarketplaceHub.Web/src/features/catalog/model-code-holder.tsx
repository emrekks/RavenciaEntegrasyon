import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { hubApi } from '../../shared/api'

// A model code belongs to one product: "Yeni Ürün" (and "Kopyala ile yeni ürün", a draft too) may not take one the
// catalog already holds. The editor asks the server once the typing settles and stops the create until the code is
// free; a new colour or size of that model is added from the holder's own editor. The server refuses it too (409).
export type ModelCodeHolder = { taken: boolean; productId?: string | null; productTitle?: string | null; message?: string | null }

export const modelCodeCheckDelayMs = 400
const maxModelCodeLength = 160

// The code as the server is asked about it; empty (free) or too long (the field's own rule) is not asked.
export function modelCodeToCheck(value: string) {
  const code = value.trim()
  return code && code.length <= maxModelCodeLength ? code : ''
}

// One short line under the field: the holder's name (cut when long) and its link; the field's neighbours must not move.
export function modelCodeHolderMessage(productTitle: string) {
  const title = productTitle.length > 28 ? `${productTitle.slice(0, 27).trimEnd()}…` : productTitle
  return `Kullanımda: ${title}.`
}

// The holder that blocks the create: only a taken answer for the code currently in the field.
export function blockingModelCodeHolder(answer: ModelCodeHolder | undefined) {
  return answer?.taken && answer.productId ? { productId: answer.productId, message: modelCodeHolderMessage(answer.productTitle || 'başka bir ürün') } : null
}

function useSettledValue(value: string, delayMs: number) {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), delayMs)
    return () => window.clearTimeout(timer)
  }, [delayMs, value])
  return settled
}

// enabled: on a new card only; an existing card (its family, a draft) keeps its own code.
export function useModelCodeHolder(modelCode: string, enabled: boolean) {
  const code = modelCodeToCheck(modelCode)
  const settled = useSettledValue(code, modelCodeCheckDelayMs)
  const query = useQuery({
    queryKey: ['model-code-holder', settled],
    queryFn: () => hubApi<ModelCodeHolder>(`/catalog/model-codes/holder?code=${encodeURIComponent(settled)}`),
    enabled: enabled && Boolean(settled) && settled === code,
    staleTime: 15_000
  })
  if (!enabled || !code || settled !== code || query.data === undefined) return null
  return blockingModelCodeHolder(query.data)
}

export function ModelCodeHolderNotice({ holder }: { holder: { productId: string; message: string } | null }) {
  if (!holder) return null
  return <small className="product-field-error model-code-holder-notice" role="alert" title="Yeni renk veya beden eklemek için o ürünü düzenleyin.">{holder.message} <Link to={`/products/${holder.productId}`}>Ürünü Aç</Link></small>
}
