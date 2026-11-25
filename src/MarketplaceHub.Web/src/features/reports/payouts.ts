import type { ApiAnswer } from '../../shared/api/api-answer'
import { cursorPageParams } from '../../shared/api/paging'
import { formatMoney } from '../../shared/format-money'
import { parseTrNumber } from '../../shared/tr-number'
// Hakediş (PayoutContracts.cs): what the marketplaces will pay and have paid, from their accounting records.
// Expected is the balance (every record not paid yet); unknown, its overdue part, is never "unpaid".
// Region NONE: a marketplace that pays without Trendyol's regions.
type PayoutsAnswer = ApiAnswer<'getReportsPayouts'>
type GroupAnswer = NonNullable<PayoutsAnswer['next']>
type EstimateAnswer = NonNullable<GroupAnswer['estimates']>[number]
type LinesAnswer = ApiAnswer<'getReportsPayoutsLines'>
export type PayoutRegion = 'TR_GULF' | 'EUROPE' | 'AZ' | 'UNKNOWN' | 'NONE'
export type PayoutStatus = 'PAID' | 'EXPECTED' | 'UNKNOWN'
// PAYOUT_DAY: an estimated payout day; DUE_DAY: a due day; OVERDUE: past its due day, no payout day known; UNDATED: unpaid
// with no due day read; PAID_DAY: a paid day of a marketplace without payout ids; PAYOUT: a payout.
export type PayoutKind = 'PAYOUT_DAY' | 'DUE_DAY' | 'OVERDUE' | 'UNDATED' | 'PAID_DAY' | 'PAYOUT'
// The open groups that wait apart, in no payout day's estimate.
export const isWaitingPayout = (group: Pick<PayoutGroup, 'kind'>) => group.kind === 'OVERDUE' || group.kind === 'UNDATED'
// estimates: on a Trendyol payout day to come, the deductions Trendyol bills only the evening before it, estimated as its
// own Ödeme Özeti does; amount is then the records (linesAmount) less them.
export type PayoutEstimateKind = 'CARGO' | 'RETURN_CARGO' | 'PLATFORM_FEE' | 'INTERNATIONAL_FEE' | 'STOPPAGE'
export type PayoutEstimate = Omit<EstimateAnswer, 'kind'> & { kind: PayoutEstimateKind }
export type PayoutGroup = Omit<GroupAnswer, 'kind' | 'region' | 'status' | 'estimates'> & { kind: PayoutKind; region: PayoutRegion; status: PayoutStatus; estimates: PayoutEstimate[] | null }
// pendingDelivery: the orders accepted or shipped but not delivered yet (not in the balance; they join a later payout).
export type PayoutRegionSummary = Omit<PayoutsAnswer['regions'][number], 'region'> & { region: PayoutRegion }
// One marketplace's balance, next payout and overdue part; the marketplaces' balances add up to Payouts.expected.
export type PayoutPlatformSummary = PayoutsAnswer['platforms'][number]
export type PayoutCoverage = Omit<PayoutsAnswer['coverage'][number], 'state'> & { state: 'READ' | 'NOT_READ' | 'NOT_SUPPORTED' }
// GET /reports/payouts and /reports/payouts/lines: the contract's answers (PayoutView, PayoutLinesPage) with the codes it
// declares as strings narrowed (region, status, kind, an estimate's kind, a coverage state: the constants of
// PayoutContracts / PayoutBuilder) by readPayouts and readPayoutLines at the fetch.
export type Payouts = Omit<PayoutsAnswer, 'next' | 'lastPaid' | 'regions' | 'upcoming' | 'past' | 'coverage'> & {
  next: PayoutGroup | null; lastPaid: PayoutGroup | null; regions: PayoutRegionSummary[]; upcoming: PayoutGroup[]; past: PayoutGroup[]; coverage: PayoutCoverage[]
}
export type PayoutCurrency = NonNullable<PayoutsAnswer['otherCurrencies']>[number]
export type PayoutLine = Omit<LinesAnswer['items'][number], 'region' | 'status'> & { region: PayoutRegion; status: PayoutStatus }
// A line's currency: the lines' total adds only the lira ones.
export type PayoutLines = Omit<LinesAnswer, 'items'> & { items: PayoutLine[] }
// (Payouts.paidUnread: Trendyol sales and returns a payout paid whose records are not read yet, out of expected;
// otherCurrencies: the records not paid yet in another currency than the lira, never in the lira figures; a group's
// carried: on the next payout, what the store's earlier days that netted to nothing or less take off it.)

const regions: ReadonlySet<string> = new Set<PayoutRegion>(['TR_GULF', 'EUROPE', 'AZ', 'UNKNOWN', 'NONE'])
const statuses: ReadonlySet<string> = new Set<PayoutStatus>(['PAID', 'EXPECTED', 'UNKNOWN'])
const kinds: ReadonlySet<string> = new Set<PayoutKind>(['PAYOUT_DAY', 'DUE_DAY', 'OVERDUE', 'UNDATED', 'PAID_DAY', 'PAYOUT'])
const estimateKinds: ReadonlySet<string> = new Set<PayoutEstimateKind>(['CARGO', 'RETURN_CARGO', 'PLATFORM_FEE', 'INTERNATIONAL_FEE', 'STOPPAGE'])
const coverageStates: ReadonlySet<string> = new Set<PayoutCoverage['state']>(['READ', 'NOT_READ', 'NOT_SUPPORTED'])
const isRegion = (code: string): code is PayoutRegion => regions.has(code)
const isStatus = (code: string): code is PayoutStatus => statuses.has(code)
const isKind = (code: string): code is PayoutKind => kinds.has(code)
const isEstimateKind = (code: string): code is PayoutEstimateKind => estimateKinds.has(code)
const isCoverageState = (code: string): code is PayoutCoverage['state'] => coverageStates.has(code)

// Codes the panel does not know, each handled without changing a figure: a region reads as UNKNOWN (shown under the
// name the marketplace gave it); a status as EXPECTED (neither paid nor overdue); a group kind as DUE_DAY (an open
// day, not waiting apart); an estimate of an unknown deduction is left out of the breakdown (the group's amount, the
// server's, already holds it); a coverage state as NOT_READ (never claimed read).
const readRegion = (code: string): PayoutRegion => isRegion(code) ? code : 'UNKNOWN'
const readStatus = (code: string): PayoutStatus => isStatus(code) ? code : 'EXPECTED'
function readGroup(group: GroupAnswer): PayoutGroup {
  return {
    ...group, kind: isKind(group.kind) ? group.kind : 'DUE_DAY', region: readRegion(group.region), status: readStatus(group.status),
    estimates: group.estimates === null ? null : group.estimates.flatMap(item => isEstimateKind(item.kind) ? [{ ...item, kind: item.kind }] : [])
  }
}

/** GET /reports/payouts as the Hakediş tab reads it (the codes narrowed; every figure as the server wrote it). */
export function readPayouts(raw: PayoutsAnswer): Payouts {
  return {
    ...raw,
    next: raw.next ? readGroup(raw.next) : null, lastPaid: raw.lastPaid ? readGroup(raw.lastPaid) : null,
    regions: raw.regions.map(item => ({ ...item, region: readRegion(item.region) })),
    upcoming: raw.upcoming.map(readGroup), past: raw.past.map(readGroup),
    coverage: raw.coverage.map(item => ({ ...item, state: isCoverageState(item.state) ? item.state : 'NOT_READ' }))
  }
}

/** GET /reports/payouts/lines as the record list reads it. */
export function readPayoutLines(raw: LinesAnswer): PayoutLines {
  return { ...raw, items: raw.items.map(item => ({ ...item, region: readRegion(item.region), status: readStatus(item.status) })) }
}

// The next payout the "Sıradaki Ödeme" card shows: the server's own figure, overall or for the picked Trendyol region
// (PayoutBuilder: the first day to come whose estimate is above zero; a day netting to nothing or less pays nothing).
export function nextPayout(data: Pick<Payouts, 'next' | 'regions'> | undefined, region: PayoutRegion | 'ALL'): { day: string | null; amount: number | null } {
  if (!data) return { day: null, amount: null }
  if (region === 'ALL') return { day: data.next?.day ?? null, amount: data.next?.amount ?? null }
  const picked = data.regions.find(item => item.region === region)
  return { day: picked?.nextDay ?? null, amount: picked?.nextDay ? picked.nextAmount : null }
}

export const regionLabels: Record<PayoutRegion, string> = { TR_GULF: 'Türkiye, Körfez', EUROPE: 'Avrupa', AZ: 'Azerbaycan', UNKNOWN: 'Bilinmeyen Bölge', NONE: '' }
export const statusLabels: Record<PayoutStatus, string> = { PAID: 'Ödendi', EXPECTED: 'Tahmini', UNKNOWN: 'Vadesi Geçti' }

// The region a group is shown under: Trendyol's by its code, else the name Trendyol gave it; none for the others.
export const regionLabel = (group: Pick<PayoutGroup, 'region' | 'regionName'>) =>
  group.region === 'UNKNOWN' && group.regionName ? group.regionName : regionLabels[group.region] ?? ''

export function payoutsQuery(platforms: string[] | null) {
  const query = new URLSearchParams()
  if (platforms?.length) query.set('platforms', platforms.join(','))
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
  // The cursor form (API-GUVENLIK-8): a hundred records, after the offset cursor of the page asked for.
  cursorPageParams(query, filters.page ?? 0, 100)
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

// The Ödeme Özeti filters. Platforms go to the server (the figures follow them); the region, state, days and amounts
// narrow the list, and the region the figures too (PayoutsReport); the search asks for records instead of payments.
export type PayoutState = 'ALL' | 'EXPECTED' | 'PAID' | 'OVERDUE' | 'DEDUCTION'
export type PayoutSort = 'date' | 'amount'
export type PayoutFilters = { region: PayoutRegion | 'ALL'; state: PayoutState; from: string; to: string; min: string; max: string; sort: PayoutSort }
export const payoutStateLabels: Record<PayoutState, string> = { ALL: 'Tümü', EXPECTED: 'Tahmini', PAID: 'Ödendi', OVERDUE: 'Vadesi Geçti', DEDUCTION: 'Bekleyen Kesinti' }
export const emptyPayoutFilters: PayoutFilters = { region: 'ALL', state: 'ALL', from: '', to: '', min: '', max: '', sort: 'date' }

// A group's state as the filter names it: paid, waiting past its due day, deductions waiting (below zero), or expected.
export function payoutState(group: PayoutGroup): Exclude<PayoutState, 'ALL'> {
  if (group.status === 'PAID') return 'PAID'
  if (group.kind === 'OVERDUE') return 'OVERDUE'
  return group.amount < 0 ? 'DEDUCTION' : 'EXPECTED'
}

// A bound typed the Turkish way, read by the panel's one Turkish number reader ("1.250" = 1250, "12,5" and "12.5" =
// 12,5). A bound it cannot read ("1,250.50", "0.125") filters nothing rather than turning into another number.
const amount = (text: string) => parseTrNumber(text)

// The groups the filters keep, in date order (as given) or by amount, largest first.
export function filterGroups(groups: PayoutGroup[], filters: PayoutFilters) {
  const min = amount(filters.min)
  const max = amount(filters.max)
  const kept = groups.filter(group => (filters.region === 'ALL' || group.region === filters.region)
    && (filters.state === 'ALL' || payoutState(group) === filters.state)
    && (!filters.from || group.day >= filters.from) && (!filters.to || group.day <= filters.to)
    && (min === null || group.amount >= min) && (max === null || group.amount <= max))
  return filters.sort === 'amount' ? [...kept].sort((a, b) => b.amount - a.amount) : kept
}

// Trendyol and Hepsiburada pay on fixed days, so a record falls due on its own day but is paid on the next payout day
// (PAYOUT_DAY groups): the rule stays, and the group says so in a short note.
// A record past its due day waits apart under Vadesi Geçmiş Bekleyen, never on a payout day; only a Trendyol sale whose
// payout day passed before its due day moves to the next payout day.
export const rolledPayoutNote = 'Ödeme günü geçmiş, vadesi henüz dolmamış satışlar sonraki ödeme gününe aktarıldı.'
export function isRolledToPayoutDay(group: Pick<PayoutGroup, 'kind' | 'platformCode'>) {
  return group.kind === 'PAYOUT_DAY' && group.platformCode.trim().toUpperCase() === 'TRENDYOL'
}

// When each marketplace pays, as read from its paid records (Oct-Nov 2026), for the next payout's explanation.
export const payoutRuleText: Record<string, string> = {
  TRENDYOL: 'Trendyol Türkiye-Körfez ve Azerbaycan pazartesi o haftanın pazartesi–çarşamba vadelerini, perşembe perşembe–pazar vadelerini öder (resmi tatil kaydırır); Avrupa ayın 4. pazartesi günü (22–28 arası) o güne kadar vadesi gelenleri öder. Ödemeden önceki akşam kesilen kargo, platform hizmet bedeli, uluslararası hizmet bedeli ve stopaj, Trendyol\'un Ödeme Özeti\'nde olduğu gibi tahminen düşülür.',
  HEPSIBURADA: 'Hepsiburada her salı öder: kayıt, vadesinden sonraki ilk salı (vade salıysa o gün) ödenir.',
  N11: 'N11 havale tarihinde öder.',
  PAZARAMA: 'Pazarama aktarım tarihinde öder.'
}

const estimateLabels: Record<PayoutEstimateKind, string> = {
  CARGO: 'Kargo', RETURN_CARGO: 'İade kargosu', PLATFORM_FEE: 'Platform hizmet bedeli', INTERNATIONAL_FEE: 'Uluslararası hizmet bedeli', STOPPAGE: 'Stopaj'
}
const money = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

// The balance in other currencies, said apart from the lira balance; empty when every record is in lira.
export function otherCurrenciesText(list: PayoutCurrency[] | null | undefined) {
  if (!list?.length) return ''
  return `Diğer para birimleri (bakiyeye eklenmedi): ${list.map(item => `${formatMoney(item.expected, item.currency)} (${item.records} kayıt)`).join(', ')}`
}

// A record's amount in its own currency.
export const lineMoney = (value: number, currency: string | undefined) => formatMoney(value, currency ?? 'TRY')

// How a Trendyol payout day's estimate is made: its records less the deductions billed only the evening before the payout.
// Null when the group has none. Trendyol knows each parcel's desi and fee tier, so its figure can differ by a few lira a
// package (a heavier parcel costs 106.76-128.64 TL instead of 98.34; a few packages pay a 5.99 TL platform fee).
export function estimateNote(group: Pick<PayoutGroup, 'estimates' | 'linesAmount'>): string | null {
  if (!group.estimates?.length || group.linesAmount === null) return null
  const parts = group.estimates.map(item => {
    const what = estimateLabels[item.kind] ?? item.kind
    const perPackage = item.kind === 'CARGO' || item.kind === 'RETURN_CARGO' || item.kind === 'PLATFORM_FEE'
    if (perPackage && item.rate === null) return `${what}: ${item.count} paket, tarife bilinmediği için düşülmedi`
    if (perPackage) return `${what} ${item.count} × ${money(item.rate ?? 0)} = ${money(item.amount)} ₺`
    return `${what} ${money(item.amount)} ₺${item.missing > 0 ? ` (KDV oranı okunmamış ${item.missing} kayıt hariç)` : ''}`
  })
  const total = group.estimates.reduce((sum, item) => sum + item.amount, 0)
  return `Kayıtların toplamı ${money(group.linesAmount)} ₺. Trendyol bu kesintileri ödemeden önceki akşam faturalar, Ödeme Özeti'nde ise şimdiden düşer; burada da tahminen düşüldü (${money(total)} ₺): ${parts.join('; ')}. `
    + "Trendyol her paketin desisini ve hizmet bedeli kademesini bildiği için tutar paket başına birkaç lira farklı olabilir; iade kargosu kesilmezse ya da Trendyol'a henüz okunmamış bir iade düştüyse fark büyür. Faturalar kesilince tahminin yerini gerçek tutarlar alır."
}
