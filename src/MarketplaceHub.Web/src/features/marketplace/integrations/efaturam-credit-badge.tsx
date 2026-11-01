// The E-Faturam card's remaining-credit (kontör) badge. The provider documents a credit endpoint only for partner
// accounts, so the API answers { available: false, reason } for everyone else; then, while loading and on any error the
// badge is absent. It never shows a placeholder such as "0" or "—".
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'

export type EFaturamRemainingCredit = { available: boolean; remainingCredit?: number | null; reason?: string | null }

export const efaturamCreditLowThreshold = 50
const creditFormat = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 })

export function efaturamRemainingCreditQueryKey(connectionId: string) {
  return ['efaturam-remaining-credit', connectionId] as const
}

// The value to show, or null when there is none to show.
export function efaturamCreditValue(credit: EFaturamRemainingCredit | undefined): number | null {
  if (!credit || credit.available !== true) return null
  const value = credit.remainingCredit
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null
}

export function EFaturamCreditBadgeView({ credit }: { credit: EFaturamRemainingCredit | undefined }) {
  const value = efaturamCreditValue(credit)
  if (value === null) return null
  const text = creditFormat.format(value)
  const low = value < efaturamCreditLowThreshold
  return <span className={`efaturam-credit-badge${low ? ' is-low' : ''}`} title={low ? `Kontör Azaldı: ${text} Kaldı` : `Kalan Kontör: ${text}`}>{text} Kontör</span>
}

export function EFaturamCreditBadge({ connectionId, enabled }: { connectionId: string; enabled: boolean }) {
  // The API caches the provider's answer (10 minutes, a day for "not a partner"); the page does not ask more often.
  const credit = useQuery({
    queryKey: efaturamRemainingCreditQueryKey(connectionId),
    queryFn: ({ signal }) => hubApi<EFaturamRemainingCredit>(`/connections/${connectionId}/efaturam-remaining-credit`, { signal }),
    enabled,
    staleTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  })
  if (!enabled || credit.isError) return null
  return <EFaturamCreditBadgeView credit={credit.data} />
}
