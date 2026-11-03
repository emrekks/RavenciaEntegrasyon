// Hakediş (PayoutContracts.cs): what the marketplaces will pay and have paid, from their accounting records.
// Expected is an estimate until it matches the marketplace's own screen; unknown is never "unpaid".
export type PayoutRegion = 'TR_GULF' | 'EUROPE' | 'AZ' | 'UNKNOWN'
export type PayoutStatus = 'PAID' | 'EXPECTED' | 'UNKNOWN'
export type PayoutGroup = { key: string; kind: 'DUE' | 'PAYOUT'; connectionId: string; platformCode: string; storeName: string; region: PayoutRegion; regionName: string | null
  day: string; amount: number; linesAmount: number | null; records: number; status: PayoutStatus }
export type PayoutRegionSummary = { region: PayoutRegion; expected: number; records: number; nextDay: string | null; nextAmount: number | null; unknown: number }
export type PayoutCoverage = { connectionId: string; platformCode: string; storeName: string; state: 'READ' | 'NOT_READ' | 'NOT_SUPPORTED'; lastSyncedAt: string | null }
export type Payouts = { today: string; daysBack: number; expected: number; expectedRecords: number; next: PayoutGroup | null; lastPaid: PayoutGroup | null
  unknown: number; unknownRecords: number; earlyPaymentsTaken: number; earlyPayments: number; regions: PayoutRegionSummary[]
  upcoming: PayoutGroup[]; past: PayoutGroup[]; coverage: PayoutCoverage[] }
export type PayoutLine = { externalId: string; connectionId: string; platformCode: string; transactionType: string | null; category: string; transactionDate: string
  dueDay: string | null; orderNumber: string | null; invoiceNumber: string | null; country: string | null; region: PayoutRegion; gross: number; commission: number | null
  net: number; status: PayoutStatus; paymentOrderId: string | null }
export type PayoutLines = { items: PayoutLine[]; totalCount: number; page: number; pageSize: number; net: number }

export const regionLabels: Record<PayoutRegion, string> = { TR_GULF: 'Türkiye, Körfez', EUROPE: 'Avrupa', AZ: 'Azerbaycan', UNKNOWN: 'Bilinmeyen Bölge' }
export const statusLabels: Record<PayoutStatus, string> = { PAID: 'Ödendi', EXPECTED: 'Tahmini', UNKNOWN: 'Durumu Bilinmiyor' }

export function payoutsQuery(platforms: string[] | null) {
  const query = new URLSearchParams()
  if (platforms?.length) query.set('platformCodes', platforms.join(','))
  return query.toString()
}

// The records of a group (its store and payout, or its store, due day and region) or of a search.
export function payoutLinesQuery(filters: { platforms: string[] | null; group?: PayoutGroup; search?: string; page?: number }) {
  const query = new URLSearchParams(payoutsQuery(filters.platforms))
  const search = filters.search?.trim()
  if (search) query.set('q', search)
  else if (filters.group) {
    query.set('connectionId', filters.group.connectionId)
    if (filters.group.kind === 'PAYOUT') query.set('payoutId', filters.group.key.split(':').slice(2).join(':'))
    else { query.set('day', filters.group.day); query.set('region', filters.group.region) }
  }
  if (filters.page) query.set('page', String(filters.page))
  return query.toString()
}

// What the records a payout paid add up to against the payout's own amount; null when no record was read for it yet
// or they agree to the kuruş.
export function payoutGap(group: PayoutGroup) {
  if (group.kind !== 'PAYOUT' || group.linesAmount === null) return null
  const gap = Math.round((group.amount - group.linesAmount) * 100) / 100
  return gap === 0 ? null : gap
}
