// Hakediş (PayoutContracts.cs): what the marketplaces will pay and have paid, from their accounting records.
// Expected is the balance (every record not paid yet); unknown, its overdue part, is never "unpaid".
// Region NONE: a marketplace that pays without Trendyol's regions.
export type PayoutRegion = 'TR_GULF' | 'EUROPE' | 'AZ' | 'UNKNOWN' | 'NONE'
export type PayoutStatus = 'PAID' | 'EXPECTED' | 'UNKNOWN'
// PAYOUT_DAY: an estimated payout day; DUE_DAY: a due day; OVERDUE: past its due day, no payout day known; PAYOUT: paid.
export type PayoutKind = 'PAYOUT_DAY' | 'DUE_DAY' | 'OVERDUE' | 'PAYOUT'
export type PayoutGroup = { key: string; kind: PayoutKind; connectionId: string; platformCode: string; storeName: string; region: PayoutRegion; regionName: string | null
  day: string; amount: number; linesAmount: number | null; records: number; status: PayoutStatus }
export type PayoutRegionSummary = { region: PayoutRegion; expected: number; records: number; nextDay: string | null; nextAmount: number | null; unknown: number }
export type PayoutCoverage = { connectionId: string; platformCode: string; storeName: string; state: 'READ' | 'NOT_READ' | 'NOT_SUPPORTED'; lastSyncedAt: string | null }
export type Payouts = { today: string; daysBack: number; expected: number; expectedRecords: number; next: PayoutGroup | null; lastPaid: PayoutGroup | null
  unknown: number; unknownRecords: number; earlyPaymentsTaken: number; earlyPayments: number; regions: PayoutRegionSummary[]
  upcoming: PayoutGroup[]; past: PayoutGroup[]; coverage: PayoutCoverage[] }
export type PayoutLine = { externalId: string; connectionId: string; platformCode: string; transactionType: string | null; category: string; transactionDate: string
  dueDay: string | null; orderNumber: string | null; invoiceNumber: string | null; country: string | null; region: PayoutRegion; gross: number; commission: number | null
  net: number; status: PayoutStatus; paymentOrderId: string | null; barcode: string | null }
export type PayoutLines = { items: PayoutLine[]; totalCount: number; page: number; pageSize: number; net: number }

export const regionLabels: Record<PayoutRegion, string> = { TR_GULF: 'Türkiye, Körfez', EUROPE: 'Avrupa', AZ: 'Azerbaycan', UNKNOWN: 'Bilinmeyen Bölge', NONE: '' }
export const statusLabels: Record<PayoutStatus, string> = { PAID: 'Ödendi', EXPECTED: 'Tahmini', UNKNOWN: 'Vadesi Geçti' }

// The region a group is shown under: Trendyol's by its code, else the name Trendyol gave it; none for the others.
export const regionLabel = (group: Pick<PayoutGroup, 'region' | 'regionName'>) =>
  group.region === 'UNKNOWN' && group.regionName ? group.regionName : regionLabels[group.region] ?? ''

export function payoutsQuery(platforms: string[] | null) {
  const query = new URLSearchParams()
  if (platforms?.length) query.set('platformCodes', platforms.join(','))
  return query.toString()
}

// The records of a group (its store and payout, or its store, day, region and kind) or of a search.
export function payoutLinesQuery(filters: { platforms: string[] | null; group?: PayoutGroup; search?: string; page?: number }) {
  const query = new URLSearchParams(payoutsQuery(filters.platforms))
  const search = filters.search?.trim()
  if (search) query.set('q', search)
  else if (filters.group) {
    query.set('connectionId', filters.group.connectionId)
    if (filters.group.kind === 'PAYOUT') query.set('payoutId', filters.group.key.split(':').slice(2).join(':'))
    else { query.set('day', filters.group.day); query.set('region', filters.group.region); query.set('kind', filters.group.kind) }
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

export type PayoutDay = { day: string; total: number; groups: PayoutGroup[] }

// Groups by day, in the given order of days; the day's total is its groups' sum.
export function byDay(groups: PayoutGroup[]): PayoutDay[] {
  const days: PayoutDay[] = []
  for (const group of groups) {
    const last = days.at(-1)
    if (last?.day === group.day) { last.groups.push(group); last.total += group.amount }
    else days.push({ day: group.day, total: group.amount, groups: [group] })
  }
  return days
}

const months = ['Oca', 'Şub', 'Mar', 'Nis', 'May', 'Haz', 'Tem', 'Ağu', 'Eyl', 'Eki', 'Kas', 'Ara']
const weekdays = ['Paz', 'Pzt', 'Sal', 'Çar', 'Per', 'Cum', 'Cmt']

// A calendar day (yyyy-MM-dd) as "05 Kas 2026 Per"; without the year and weekday, "05 Kas".
export function shortDay(day: string, full = true) {
  const [year, month, date] = day.split('-').map(Number)
  if (!year || !month || !date) return day
  const text = `${String(date).padStart(2, '0')} ${months[month - 1]}`
  return full ? `${text} ${year} ${weekdays[new Date(Date.UTC(year, month - 1, date)).getUTCDay()]}` : text
}
