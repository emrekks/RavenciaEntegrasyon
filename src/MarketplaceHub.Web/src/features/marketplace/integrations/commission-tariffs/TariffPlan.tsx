import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { hubApi, hubFile } from '../../../../shared/api'
import { Busy, ErrorBox, StickyActionBar, TitleHint, UiIcon } from '../../../../shared/components'
import { newIdempotencyKey } from '../../../../shared/idempotency'
import { compareOptionValues } from '../../../catalog/shared'
import { parseTrNumber } from '../../../../shared/tr-number'
import { kurus, onCost, ProfitBadge, unitProfit, type UnitProfitCosts } from '../unit-profit'
import { useMediaQuery } from '../../../../shared/hooks/useMediaQuery'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { loadRowPage, type RowPage } from '../commission-tariff-rows'
import { EmptyState } from '../../../../shared/components/EmptyState'
import { queryKeys } from '../../../../shared/api/query-keys'
import { type CommissionTariff, periodDays, periodName, weekTabLabel, type TariffBulk, ProductImage, tiers, type TariffFilter, type TariffSort } from './tariff-helpers'
import { PhoneSearch, TariffFilters, SortHeader, BulkPicker } from './TariffFilters'

// One formatter per format, made once: toLocaleString with options builds a new Intl.NumberFormat on every call, and
// the tariff table formats thousands of amounts per render.
const twoDecimals = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

export type CommissionBand = { tier: number; min: number | null; max: number | null; rate: number }

// What a price leaves over the periods together, each period weighted by its days (3/7 and 4/7 of a week); unknown
// when a period's is.
export function weekAverage(parts: Array<{ days: number; value: number | null | undefined }>): number | null {
  if (!parts.length || parts.some(part => part.value == null)) return null
  const days = parts.reduce((sum, part) => sum + part.days, 0)
  return kurus(parts.reduce((sum, part) => sum + part.value! * part.days, 0) / days)
}
export type CommissionTariffRow = {
  id: string; barcode: string; sellerSku: string | null; modelCode: string | null; productName: string | null; size: string | null
  basisPrice: number | null; currentSalePrice: number | null; currentRate: number | null; currentTier: number | null; currentBandRate: number | null
  bands: CommissionBand[]; nextLowerBand: CommissionBand | null; matchesPanel: boolean
  // The product cost entered on the panel, each band as a price to pick, the band that leaves the most and the
  // panel product's picture (absent in an older server's answer).
  costPrice?: number | null; choices?: CommissionChoice[] | null; recommendedTier?: number | null; imageUrl?: string | null
  // The panel variant's colour ("Bej"); a model's rows are grouped by it.
  color?: string | null
  // The stock the tariff file gives (absent in a tariff uploaded before it was read).
  stock?: number | null
}
// A band as a price the seller can pick: its commission and the profit the price leaves after it and its other charges
// (costs: the product cost, cargo, service fee, withholding tax and net VAT; absent in an older server's answer).
export type CommissionChoice = { tier: number; price: number; rate: number; commission: number; remaining: number | null; costs?: UnitProfitCosts | null }

// A row's own pick: a band, a price of the seller's own, or neither (the row left out of the Excel).
export type TariffPick = { tier: number | null; price: number | null }

export const noBulk: TariffBulk = { kind: '', tier: 1, minRate: null, minAmount: null, maxRate: null, maxAmount: null }

// A tariff's picks as the server keeps them (GET and PUT …/selection): the pick for every row and each row's own pick.
// CONTRACT: the API declares a choice's tier and price as "number or string": CommissionTariffChoice is the PUT body's
// too, which reads either, but an answer writes numbers (int? Tier, decimal? Price). It is also this screen's PUT body.
export type TariffSelection = {
  bulk: string | null; tier: number | null; minRate: number | null; minAmount: number | null; maxRate: number | null; maxAmount: number | null
  choices: Array<{ rowId: string; tier: number | null; price: number | null }>; updatedAt?: string | null
}

// The page's picks as the server keeps them, rows in id order so the same picks always read the same.
export function selectionBody(picks: Map<string, TariffPick>, bulk: TariffBulk): TariffSelection {
  const on = bulk.kind !== ''
  return {
    bulk: on ? bulk.kind : null, tier: bulk.kind === 'tier' ? bulk.tier : null,
    minRate: on ? bulk.minRate : null, minAmount: on ? bulk.minAmount : null, maxRate: on ? bulk.maxRate ?? null : null, maxAmount: on ? bulk.maxAmount ?? null : null,
    choices: [...picks].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([rowId, pick]) => ({ rowId, tier: pick.price !== null ? null : pick.tier, price: pick.price }))
  }
}

// The saved picks as the page holds them; nothing saved is no pick at all.
export function selectionState(saved: TariffSelection | undefined): { picks: Map<string, TariffPick>; bulk: TariffBulk } {
  const kind = saved?.bulk === 'tier' || saved?.bulk === 'amount' || saved?.bulk === 'rate' ? saved.bulk : ''
  const bulk: TariffBulk = kind === '' ? noBulk : { kind, tier: kind === 'tier' && saved?.tier ? saved.tier : 1, minRate: saved?.minRate ?? null, minAmount: saved?.minAmount ?? null, maxRate: saved?.maxRate ?? null, maxAmount: saved?.maxAmount ?? null }
  return { picks: new Map((saved?.choices ?? []).map(choice => [choice.rowId, { tier: choice.price !== null ? null : choice.tier, price: choice.price }])), bulk }
}

// What the download says: how many products the Trendyol file carries a price for, when the file had to be written
// again from the stored rows (a tariff uploaded before files were kept), and how many products were picked at different
// prices in the file's two periods (the file holds one price per product: the open period's was written).
export function exportMessage(rows: number, rebuilt: boolean, conflicts = 0, period?: string) {
  const count = Number.isFinite(rows) ? `${rows.toLocaleString('tr-TR')} ürünün` : 'Seçilen ürünlerin'
  let text = `${count} fiyatı Trendyol tarife dosyasına yazıldı (YENİ TSF ve Tarife Seçimi sütunları). Dosyayı Trendyol'da Ürün Komisyon Tarifeleri sayfasından yükleyin.`
  if (Number.isFinite(conflicts) && conflicts > 0)
    text += ` ${conflicts.toLocaleString('tr-TR')} üründe iki dönem için farklı fiyat seçili; dosya ürün başına tek fiyat taşıdığından bu ürünlere ${period ? `${period} döneminin` : 'açık sekmenin'} fiyatı yazıldı.`
  return rebuilt ? `${text} Bu tarifenin yüklenen dosyası saklanmadığı için dosya kayıtlı satırlardan yeniden yazıldı; Trendyol'un kendi dosyası için aynı tarife dosyasını bir kez daha yükleyin.` : text
}

// The profit as a share of the cost, per cent (Kâr Oranı, the server's CommissionTariffChoices.OnCost).
const share = (choice: CommissionChoice) => choice.remaining === null ? null : onCost(choice.remaining, choice.costs)

// The pick for every row in one row, worked out as the server's CommissionTariffChoices.Pick does.
export function bulkChoice(choices: CommissionChoice[], bulk: TariffBulk): CommissionChoice | null {
  const known = choices.filter(choice => choice.remaining !== null)
  const choice = bulk.kind === 'tier' ? choices.find(item => item.tier === bulk.tier) ?? null
    : bulk.kind === 'amount' ? [...known].sort((a, b) => (b.remaining ?? 0) - (a.remaining ?? 0) || a.rate - b.rate)[0] ?? null
    : bulk.kind === 'rate' ? [...known].filter(item => share(item) !== null).sort((a, b) => (share(b) ?? 0) - (share(a) ?? 0) || a.rate - b.rate)[0] ?? null
    : null
  if (!choice) return null
  if (bulk.minRate !== null && (share(choice) === null || (share(choice) ?? 0) < bulk.minRate)) return null
  if (bulk.minAmount !== null && (choice.remaining === null || choice.remaining < bulk.minAmount)) return null
  if (bulk.maxRate != null && (share(choice) === null || (share(choice) ?? 0) > bulk.maxRate)) return null
  if (bulk.maxAmount != null && (choice.remaining === null || choice.remaining > bulk.maxAmount)) return null
  return choice
}

// The band a row counts as picked: its own pick, else the pick for every row; 'own' for a price of the seller's own.
export function pickedTier(row: Pick<CommissionTariffRow, 'choices'>, pick: TariffPick | undefined, bulk: TariffBulk): number | 'own' | null {
  if (pick) return pick.price !== null ? 'own' : pick.tier
  return bulkChoice(row.choices ?? [], bulk)?.tier ?? null
}

// A price of the seller's own, worked out as the server does: the band it falls in, its commission and its profit, with
// the cargo, fee and VAT rate the server priced the row's bands at (unknown when it priced none).
export function ownChoice(row: Pick<CommissionTariffRow, 'bands' | 'costPrice' | 'choices'>, price: number): CommissionChoice | null {
  const rounded = kurus(price)
  const band = row.bands.find(item => (item.min === null || rounded >= item.min) && (item.max === null || rounded <= item.max))
  if (!band || rounded <= 0) return null
  const commission = kurus(rounded * band.rate / 100)
  const basis = row.choices?.find(choice => choice.costs)?.costs
  const known = row.costPrice && basis ? unitProfit(rounded, commission, row.costPrice, basis) : null
  return { tier: band.tier, price: rounded, rate: band.rate, commission, remaining: known?.profit ?? null, costs: known?.costs ?? null }
}

// The page's rows by model and colour, in their order: a model's sizes of one colour make a group, and a row without
// a model code is a group of its own.
export function groupByModel(rows: CommissionTariffRow[]): Array<[string, CommissionTariffRow[]]> {
  const groups = new Map<string, CommissionTariffRow[]>()
  for (const row of rows) {
    const model = row.modelCode?.trim()
    const key = model ? `${model}|${row.color?.trim() || ''}` : row.barcode
    groups.set(key, [...(groups.get(key) ?? []), row])
  }
  // Inside a colour its sizes run in size order (S, M, L, XL, 2XL …), not in the file's order.
  return [...groups].map(([key, rows]) => [key, [...rows].sort((a, b) => compareOptionValues('Beden', a.size ?? '', b.size ?? ''))] as [string, CommissionTariffRow[]])
}

// A stock as the file gives it; unknown in a tariff uploaded before stock was read.
export const stockText = (stock: number | null | undefined) => stock == null ? '—' : stock.toLocaleString('tr-TR', { maximumFractionDigits: 0 })

const amount = (value: number) => twoDecimals.format(value)
// "752,64 – 791,01 ₺", or one price when all are the same.
export function priceRange(values: number[]) {
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? money(low) : `${amount(low)} – ${money(high)}`
}
// A band as Trendyol's own panel heads it: the top band from its lower limit up ("464,63 ₺ ve üstü"), every other band
// up to its upper limit ("464,62 ₺ ve altı"); its exact range (both limits included) is in the hint. Over a model's
// variants with different limits, those limits from the highest down.
export function bandLimitText(bands: Array<CommissionBand | undefined>) {
  const found = bands.filter((band): band is CommissionBand => band != null)
  if (!found.length) return null
  const mins = found.flatMap(band => band.min === null ? [] : [band.min])
  const maxes = found.flatMap(band => band.max === null ? [] : [band.max])
  const downward = (values: number[]) => { const low = Math.min(...values), high = Math.max(...values); return low === high ? money(high) : `${amount(high)} – ${money(low)}` }
  if (found[0].max === null) return mins.length ? `${downward(mins)} ve üstü` : 'Tüm fiyatlar'
  return maxes.length ? `${downward(maxes)} ve altı` : null
}
// The same band in words, for its hint: "464,62 ₺ ile 432,27 ₺ arası (ikisi de dahil)".
export function bandLimitHint(bands: Array<CommissionBand | undefined>) {
  const found = bands.filter((band): band is CommissionBand => band != null)
  const texts = [...new Set(found.map(band => band.min !== null && band.max !== null ? `${money(band.max)} ile ${money(band.min)} arası (ikisi de dahil)`
    : band.min !== null ? `${money(band.min)} ve üstü (${money(band.min)} dahil)` : band.max !== null ? `${money(band.max)} ve altı (${money(band.max)} dahil)` : 'Tüm fiyatlar'))]
  return texts.length === 1 ? texts[0] : texts.length ? `Bedenlere göre: ${texts.join('; ')}` : undefined
}
// What a model's variants leave, with its sign: "266,09 – 375,71 ₺".
export function amountRange(values: number[]) {
  const signed = (value: number) => `${value < 0 ? '− ' : ''}${amount(Math.abs(value))}`
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? `${signed(low)} ₺` : `${signed(low)} – ${signed(high)} ₺`
}
// The bands leaving the most (every one of them on a tie), by their profit; a band whose profit is unknown never is.
export function bestTiers(profits: Array<[number, number | null]>) {
  const known = profits.filter((entry): entry is [number, number] => entry[1] !== null)
  if (!known.length) return new Set<number>()
  const most = Math.max(...known.map(([, value]) => value))
  return new Set(known.filter(([, value]) => value === most).map(([tier]) => tier))
}
// The variant leaving the most at a band's price (the card's headline limit): the one a model's badge shows, with its
// own profit on cost, and the one the star compares; a variant whose profit is unknown never is. Null when none is known.
export function mostProfit(choices: CommissionChoice[]) {
  return choices.filter(choice => choice.remaining !== null).sort((a, b) => b.remaining! - a.remaining!)[0] ?? null
}

const productImage = (row: CommissionTariffRow) =>
  <ProductImage url={row.imageUrl} title={row.productName ?? row.barcode} modelCode={row.modelCode} color={row.color} initial={(row.productName ?? row.barcode).slice(0, 1)} />

const money = (value: number) => `${twoDecimals.format(value)} ₺`
const upToTwoDecimals = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 })
export const rateText = (rate: number) => `%${upToTwoDecimals.format(rate)}`

// The marketplace's own "Güncel Komisyon" is shown beside the band the panel computes; a difference is flagged, never hidden.
export function rateDisagrees(row: Pick<CommissionTariffRow, 'currentRate' | 'currentBandRate'>) {
  return row.currentRate !== null && row.currentBandRate !== null && Math.abs(row.currentRate - row.currentBandRate) > 0.0001
}

type PanelMessage = { kind: 'success' | 'error' | 'info' | 'warning'; text: string; warnings?: string[] }

// Model rows drawn at once in the tariff table (see TariffPlanBody).
export const modelsPerView = 25

// Phones: the filter card and the Toplu Seçim popover open as bottom sheets over a dimmed page (the scrim closes them),
// a grab handle and their title on top; on wider screens the scrim and the head are not shown.
export const phoneQuery = '(max-width: 760px)'

const bulkLabel = (bulk: TariffBulk) => bulk.kind === 'tier' ? `${bulk.tier}. Fiyat Aralığı` : bulk.kind === 'amount' ? 'En Yüksek Kâr Tutarı' : 'En Yüksek Kâr Oranı'

// Keeps a tariff's picks saved on the server: a change is sent 600 ms after the last one, one save at a time and the
// latest state winning; flush() sends what is waiting and resolves once the server holds it. Leaving the tariff sends
// what is still waiting.
// The save still on its way per tariff (connection/tariff). Leaving a tab sends the waiting picks as the plan unmounts;
// coming straight back reads the picks again, and that read waits for the save so it does not bring the older picks.
const pendingSelectionSaves = new Map<string, Promise<void>>()
export const selectionSaveKey = (connectionId: string, tariffId: string) => `${connectionId}/${tariffId}`
export function selectionSaveSettled(key: string) {
  return (pendingSelectionSaves.get(key) ?? Promise.resolve()).catch(() => undefined)
}

export function useSavedSelection(save: (body: string) => Promise<unknown>, serialized: string, key: string | null) {
  const latest = useRef(serialized)
  const saved = useRef(serialized)
  const queue = useRef<Promise<void>>(Promise.resolve())
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const [status, setStatus] = useState<'saved' | 'saving' | 'error'>('saved')
  const flush = useCallback(() => {
    clearTimeout(timer.current)
    const next = queue.current.catch(() => undefined).then(async () => {
      const sending = latest.current
      if (sending === saved.current) return
      setStatus('saving')
      try {
        await save(sending)
        saved.current = sending
        setStatus(latest.current === sending ? 'saved' : 'saving')
      } catch (reason) {
        setStatus('error')
        throw reason
      }
    })
    queue.current = next
    if (key) {
      pendingSelectionSaves.set(key, next)
      void next.catch(() => undefined).then(() => { if (pendingSelectionSaves.get(key) === next) pendingSelectionSaves.delete(key) })
    }
    return next
  }, [save, key])
  useEffect(() => {
    latest.current = serialized
    if (serialized === saved.current) return
    clearTimeout(timer.current)
    timer.current = setTimeout(() => { void flush().catch(() => undefined) }, 600)
  }, [serialized, flush])
  useEffect(() => () => { void flush().catch(() => undefined) }, [flush])
  return { status, flush }
}

// One period's products with each band as a price to pick. The picks are saved on the server, so they are there again
// when the tariff is opened later; the plan waits for them before it shows.
export function TariffPlan(props: TariffPlanProps) {
  const selection = useQuery({
    queryKey: queryKeys.commissionTariffSelection(props.connectionId, props.tariff.id),
    queryFn: async ({ signal }) => { await selectionSaveSettled(selectionSaveKey(props.connectionId, props.tariff.id)); return hubApi<TariffSelection>(`/connections/${props.connectionId}/commission-tariffs/${props.tariff.id}/selection`, { signal }) },
    gcTime: 0, refetchOnWindowFocus: false
  })
  // The 7-day view keeps the second period's picks too.
  const partnerSelection = useQuery({
    queryKey: queryKeys.commissionTariffSelection(props.connectionId, props.partner?.id),
    queryFn: async ({ signal }) => { await selectionSaveSettled(selectionSaveKey(props.connectionId, props.partner!.id)); return hubApi<TariffSelection>(`/connections/${props.connectionId}/commission-tariffs/${props.partner!.id}/selection`, { signal }) },
    gcTime: 0, refetchOnWindowFocus: false, enabled: props.partner != null
  })
  if (selection.isPending || (props.partner && partnerSelection.isPending)) return <Busy />
  if (selection.isError) return <ErrorBox error={selection.error} onRetry={() => void selection.refetch()} />
  if (props.partner && partnerSelection.isError) return <ErrorBox error={partnerSelection.error} onRetry={() => void partnerSelection.refetch()} />
  return <TariffPlanBody {...props} saved={selection.data} partnerSaved={props.partner ? partnerSelection.data : undefined} />
}

type TariffPlanProps = {
  connectionId: string; tariff: CommissionTariff; partner?: CommissionTariff; periods: CommissionTariff[]; rows: RowPage | undefined; loading: boolean; error: unknown; onRetry: () => void
  filter: TariffFilter; onFilter: (filter: TariffFilter) => void
  page: number; pageCount: number; onPage: (page: number) => void; onMessage: (message: PanelMessage | null) => void
}

// One period's products with each band as a price to pick: the band's price, its commission and what the price leaves
// after the commission and the product cost. A pick is made row by row (a band, or a price of the seller's own) or for
// every row at once; the picks are saved as they change and go out as Trendyol's own tariff file with the picked prices
// filled in. Picks are kept across pages, searches and visits.
const tableZoomSteps = [100, 90, 80, 70]
const tableZoomKey = 'commission-table-zoom'
function readTableZoom(): number {
  try { const value = Number(localStorage.getItem(tableZoomKey)); return tableZoomSteps.includes(value) ? value : 100 } catch { return 100 }
}
function writeTableZoom(value: number) { try { localStorage.setItem(tableZoomKey, String(value)) } catch { /* private mode: the size is not kept */ } }

function TariffPlanBody({ connectionId, tariff, partner, periods, rows, loading, error, onRetry, filter, onFilter, page, pageCount, onPage, onMessage, saved, partnerSaved }: TariffPlanProps & { saved: TariffSelection; partnerSaved?: TariffSelection }) {
  const [picks, setPicks] = useState<Map<string, TariffPick>>(() => selectionState(saved).picks)
  // Phones show each product as a card (its bands a row of cards that slides sideways) instead of the wide table; the
  // picks, the filter and the saves are the same state either way.
  const phone = useMediaQuery(phoneQuery)
  // The table's size (100 % to 70 %), kept for this browser, so more of the wide band table fits the screen.
  const [tableZoom, setTableZoom] = useState(readTableZoom)
  // The pick for every row, set in the Toplu Seçim popover.
  const [bulk, setBulk] = useState<TariffBulk>(() => selectionState(saved).bulk)
  const applied = bulk
  const save = useCallback((body: string) => hubApi<TariffSelection>(`/connections/${connectionId}/commission-tariffs/${tariff.id}/selection`, {
    method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-selection') }, body
  }), [connectionId, tariff.id])
  const saving = useSavedSelection(save, useMemo(() => JSON.stringify(selectionBody(picks, bulk)), [picks, bulk]), selectionSaveKey(connectionId, tariff.id))
  // The 7-day view: the second period's rows of the products on the page (by barcode; its rows have ids of their own)
  // and its picks, which every pick made here changes as it changes the first period's.
  const pageBarcodes = useMemo(() => [...new Set(rows?.items.map(row => row.barcode) ?? [])].sort(), [rows])
  const partnerRows = useQuery({
    queryKey: queryKeys.commissionTariffRows(connectionId, partner?.id, 'barcodes', pageBarcodes),
    queryFn: ({ signal }) => loadRowPage(connectionId, partner!.id, { limit: '200', barcodes: pageBarcodes.join(',') }, signal),
    enabled: partner != null && pageBarcodes.length > 0
  })
  const partnerBy = useMemo(() => new Map((partnerRows.data?.items ?? []).map(row => [row.barcode, row])), [partnerRows.data])
  const [partnerPicks, setPartnerPicks] = useState<Map<string, TariffPick>>(() => selectionState(partnerSaved).picks)
  const [partnerBulk, setPartnerBulk] = useState<TariffBulk>(() => selectionState(partnerSaved).bulk)
  const partnerId = partner?.id
  const savePartner = useCallback((body: string) => hubApi<TariffSelection>(`/connections/${connectionId}/commission-tariffs/${partnerId}/selection`, {
    method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-selection') }, body
  }), [connectionId, partnerId])
  const partnerSaving = useSavedSelection(savePartner, useMemo(() => JSON.stringify(selectionBody(partnerPicks, partnerBulk)), [partnerPicks, partnerBulk]), partnerId ? selectionSaveKey(connectionId, partnerId) : null)
  const savingStatus = saving.status === 'error' || partnerSaving.status === 'error' ? 'error' : saving.status === 'saving' || partnerSaving.status === 'saving' ? 'saving' : 'saved'
  const applyBulk = (next: TariffBulk) => { setBulk(next); if (partner) setPartnerBulk(next) }
  // A column's picks taken back: the pick for every row when it is that column, and the rows picked for that band.
  const clearColumn = (column: string) => {
    const tier = /^[1-4]$/.test(column) ? Number(column) : null
    const clear = (current: TariffBulk, set: (value: TariffBulk) => void, setRows: typeof setPicks) => {
      if (tier === null ? current.kind === column : current.kind === 'tier' && current.tier === tier) set(noBulk)
      if (tier !== null) setRows(rowPicks => new Map([...rowPicks].filter(([, pick]) => pick.tier !== tier)))
    }
    clear(bulk, setBulk, setPicks)
    if (partner) clear(partnerBulk, setPartnerBulk, setPartnerPicks)
  }
  const clearAll = () => { applyBulk(noBulk); setPicks(new Map()); if (partner) setPartnerPicks(new Map()) }
  const bulkOn = applied.kind !== ''
  // A pick for rows (null takes theirs back), in the 7-day view for the same products in the second period too.
  const setRowPicks = (targets: CommissionTariffRow[], pick: TariffPick | null) => {
    const apply = (current: Map<string, TariffPick>, idOf: (row: CommissionTariffRow) => string | undefined) => {
      const next = new Map(current)
      for (const row of targets) {
        const id = idOf(row)
        if (id === undefined) continue
        if (pick) next.set(id, pick); else next.delete(id)
      }
      return next
    }
    setPicks(current => apply(current, row => row.id))
    if (partner) setPartnerPicks(current => apply(current, row => partnerBy.get(row.barcode)?.id))
  }
  // A click on the picked band takes it back (under a pick for every row, the row is then left out on purpose).
  const toggle = (row: CommissionTariffRow, tier: number, picked: boolean) => setRowPicks([row], picked ? (bulkOn ? { tier: null, price: null } : null) : { tier, price: null })
  // A model's switch picks the band for all its variants that have it, or takes it back from all of them.
  const pickModel = (owners: CommissionTariffRow[], tier: number, all: boolean) => setRowPicks(owners, !all ? { tier, price: null } : bulkOn ? { tier: null, price: null } : null)
  // What a band leaves for these rows, the most of them (in the 7-day view each row's profit over the week); unknown
  // profits are left out, and null when none is known.
  const tierProfit = (owners: CommissionTariffRow[], tier: number) => {
    const values = owners.map(row => {
      const own = row.choices?.find(item => item.tier === tier)?.remaining
      return partner ? weekAverage([{ days: periodDays(tariff), value: own }, { days: periodDays(partner), value: partnerBy.get(row.barcode)?.choices?.find(item => item.tier === tier)?.remaining }]) : own ?? null
    }).filter((value): value is number => value !== null)
    return values.length ? Math.max(...values) : null
  }
  // The 7-day lines of a band: each period's commission and profit, and what it leaves over the week.
  const weekLines = (owners: CommissionTariffRow[], tier: number, title: string) => {
    if (!partner) return null
    const days = periodDays(tariff), partnerDays = periodDays(partner)
    const sides = [{ period: tariff, choices: owners.flatMap(row => row.choices?.filter(item => item.tier === tier) ?? []) },
      { period: partner, choices: owners.flatMap(row => partnerBy.get(row.barcode)?.choices?.filter(item => item.tier === tier) ?? []) }]
    const averages = owners.map(row => weekAverage([
      { days, value: row.choices?.find(item => item.tier === tier)?.remaining },
      { days: partnerDays, value: partnerBy.get(row.barcode)?.choices?.find(item => item.tier === tier)?.remaining }]))
    const known = averages.filter((value): value is number => value !== null)
    return <>
      {sides.map(({ period, choices }) => {
        const name = periodName(period)
        if (!choices.length) return <span key={period.id} className="commission-week-line"><em>{name}</em><small>{partnerRows.isPending ? 'Yükleniyor…' : 'Bu dönemde yok'}</small></span>
        const rates = [...new Set(choices.map(item => item.rate))]
        return <span key={period.id} className="commission-week-line">
          <em>{name} · {rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`}</em>
          <ProfitBadge price={mostProfit(choices) ?? choices[0]} title={title} tag={`${tier}. Fiyat Aralığı · ${name}`} missing="Maliyet girilmemiş" />
        </span>
      })}
      <small className="commission-week-average">{weekTabLabel([tariff, partner]).split(' (')[0]} ortalaması: {known.length ? `${amountRange([Math.max(...known)])} ${Math.max(...known) < 0 ? 'Zarar' : 'Kâr'}` : 'hesaplanamadı'}</small>
    </>
  }
  // The models whose variants are shown: a model starts folded, its row alone, so a page of 50 models (each colour with
  // all its sizes and their band cards) does not draw thousands of cards at once.
  const [opened, setOpened] = useState<Set<string>>(() => new Set())
  // A server page holds up to 50 model codes, which with their colours came to ~140 model rows, each with four band cards
  // and switches (~1,400 buttons, ~7,400 nodes): drawing them took ~260 ms and every click re-drew them all. The model
  // rows are shown 25 at a time; what each row shows is unchanged.
  const modelGroups = useMemo(() => rows ? groupByModel(rows.items) : [], [rows])
  const groupPageCount = Math.max(1, Math.ceil(modelGroups.length / modelsPerView))
  const [groupPageOf, setGroupPageOf] = useState<{ rows: RowPage | undefined; page: number }>({ rows, page: 0 })
  const groupPage = groupPageOf.rows === rows ? Math.min(groupPageOf.page, groupPageCount - 1) : 0
  const shownGroups = modelGroups.slice(groupPage * modelsPerView, (groupPage + 1) * modelsPerView)
  const tableTop = useRef<HTMLDivElement>(null)
  const showGroupPage = (next: number) => {
    setGroupPageOf({ rows, page: next })
    tableTop.current?.scrollIntoView({ block: 'start', behavior: 'smooth' })
  }
  const facets = useQuery({ queryKey: queryKeys.commissionTariffFacets(connectionId, tariff.id), queryFn: ({ signal }) => hubApi<ApiAnswer<'getConnectionsByConnectionIdCommissionTariffsByTariffIdFacets'>>(`/connections/${connectionId}/commission-tariffs/${tariff.id}/facets`, { signal }) })
  const ownPicks = [...picks.values()].filter(pick => pick.tier !== null || pick.price !== null).length
  // A file with more than one period: the picks are the open period's, and the Excel carries every period's.
  const manyPeriods = periods.length > 1
  const openName = tariff.periodLabel?.trim() || tariff.periodText
  const download = useMutation({
    // The picks are saved first; the server writes the saved picks, so the file holds what the page shows.
    mutationFn: async () => {
      await Promise.all([saving.flush(), partnerSaving.flush()])
      return hubFile(`/connections/${connectionId}/commission-tariffs/${tariff.id}/export`, {
        method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-export') }, body: '{}'
      })
    },
    onSuccess: ({ blob, fileName, headers }) => {
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = fileName ?? 'komisyon-tarifesi-secimler.xlsx'
      link.click()
      setTimeout(() => URL.revokeObjectURL(url), 0)
      const rebuilt = headers.get('X-Tariff-File') === 'rebuilt'
      const conflicts = Number(headers.get('X-Price-Conflicts') ?? 0)
      onMessage({ kind: rebuilt || conflicts > 0 ? 'info' : 'success', text: exportMessage(Number(headers.get('X-Picked-Rows') ?? Number.NaN), rebuilt, conflicts, openName) })
    },
    onError: reason => onMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Excel hazırlanamadı.' })
  })
  const conditionText = applied.minRate !== null ? `, kâr oranı %${applied.minRate.toLocaleString('tr-TR')} üstü` : applied.minAmount !== null ? `, kâr ${money(applied.minAmount)} üstü`
    : applied.maxRate != null ? `, kâr oranı %${applied.maxRate.toLocaleString('tr-TR')} altı` : applied.maxAmount != null ? `, kâr ${money(applied.maxAmount)} altı` : ''
  const summary = bulkOn ? `${bulkLabel(applied)} tüm ürünlerde${conditionText}${picks.size ? `; ${picks.size.toLocaleString('tr-TR')} ürün elle değişti` : ''}` : `${ownPicks.toLocaleString('tr-TR')} ürün seçildi`
  const savingNote = savingStatus === 'saving' ? <small> · Kaydediliyor…</small> : savingStatus === 'error' ? <small className="commission-warn"> · Seçimler kaydedilemedi</small> : null
  const downloadButton = <button type="button" className="rv-button-primary commission-plan-download" disabled={(!bulkOn && ownPicks === 0 && !manyPeriods) || download.isPending} onClick={() => download.mutate()}>
    <UiIcon name="download" size={16} />{download.isPending ? 'Hazırlanıyor…' : 'Excel İndir'}
  </button>

  // What a variant shows, in the table and on a phone alike: its own pick, the band it counts as picked, a note when the
  // 7-day view's second period is picked otherwise, and the bands leaving the most.
  const rowState = (row: CommissionTariffRow) => {
    const pick = picks.get(row.id)
    const chosen = pickedTier(row, pick, applied)
    // In the 7-day view, a product picked differently in the second period (on its own tab) says so.
    const other = partner ? partnerBy.get(row.barcode) : undefined
    const otherChosen = other ? pickedTier(other, partnerPicks.get(other.id), partnerBulk) : chosen
    const differs = partner && other && otherChosen !== chosen
      ? <small className="commission-warn">{periodName(partner)} seçimi farklı: {otherChosen === null ? 'seçilmedi' : otherChosen === 'own' ? 'kendi fiyatı' : `${otherChosen}. fiyat aralığı`}</small> : null
    const best = bestTiers(tiers.map(tier => [tier, tierProfit([row], tier)]))
    return { pick, chosen, differs, best }
  }
  // Over a model, a band's profit is the most its variants leave in it (the top of the badge's range).
  const modelBest = (group: CommissionTariffRow[]) => bestTiers(tiers.map(tier => [tier, tierProfit(group.filter(row => row.choices?.some(item => item.tier === tier)), tier)]))
  // On a phone a band's card is headed by its number, and the band the price is in now is tagged Şu An.
  const phoneHead = (tier: number, current: boolean) => <>
    <span className="commission-seg-n">{tier}. Aralık</span>
    {current && <em className="commission-now-tag">Şu An</em>}
  </>
  // A variant's band as a card to pick: a table cell's, or one of a phone card's row. Null when the row has no such band.
  const variantChoice = (row: CommissionTariffRow, tier: number, chosen: number | 'own' | null, best: Set<number>, onPhone = false) => {
    const choice = row.choices?.find(item => item.tier === tier)
    if (!choice) return null
    const band = row.bands.find(item => item.tier === tier)
    const picked = chosen === tier
    const current = onPhone && row.currentTier === tier
    return <div key={tier} className={`commission-choice${picked ? ' is-picked' : ''}${choice.remaining === null ? ' is-unknown' : choice.remaining < 0 ? ' is-loss' : ''}${current ? ' is-current' : ''}`}>
      {onPhone && phoneHead(tier, current)}
      {best.has(tier) && <i className="commission-best" role="img" title="En kârlı aralık" aria-label="En kârlı aralık">★</i>}
      {/* The card's headline is its band limit; the price the row would get stays in the badge's breakdown. */}
      <strong title={bandLimitHint([band])}>{bandLimitText([band]) ?? money(choice.price)}</strong>
      {partner ? weekLines([row], tier, `${row.productName ?? row.barcode}${row.size ? ` · ${row.size}` : ''}`) : <>
        <span>{rateText(choice.rate)} komisyon</span>
        <ProfitBadge price={choice} compact={!onPhone} title={`${row.productName ?? row.barcode}${row.size ? ` · ${row.size}` : ''}`} tag={`${tier}. Fiyat Aralığı`} missing={row.costPrice ? 'Kâr hesaplanamadı' : 'Maliyet girilmemiş'} />
      </>}
      <button type="button" className="commission-switch" aria-pressed={picked} aria-label={onPhone ? `${tier}. Fiyat Aralığı ${picked ? 'seçili' : 'seç'}` : undefined} onClick={() => toggle(row, tier, picked)}><span />{picked ? 'Seçili' : 'Seç'}</button>
    </div>
  }
  // A model's band over all its variants: picking it picks the band for every variant that has it (Tüm Varyantlarını Seç).
  const modelChoice = (group: CommissionTariffRow[], first: CommissionTariffRow, tier: number, best: Set<number>, onPhone = false) => {
    const choices = group.flatMap(row => row.choices?.filter(item => item.tier === tier) ?? [])
    if (!choices.length) return null
    const owners = group.filter(row => row.choices?.some(item => item.tier === tier))
    const all = owners.every(row => pickedTier(row, picks.get(row.id), applied) === tier)
    const rates = [...new Set(choices.map(item => item.rate))]
    const lefts = choices.flatMap(item => item.remaining === null ? [] : [item.remaining])
    // The badge shows and opens the variant leaving the most; when the variants differ, its breakdown names it.
    const most = mostProfit(choices)
    const mostRow = most && group.find(row => row.choices?.includes(most))
    const ranged = lefts.length > 0 && Math.min(...lefts) !== Math.max(...lefts)
    const current = onPhone && group.some(row => row.currentTier === tier)
    return <div key={tier} className={`commission-choice is-model${all ? ' is-picked' : ''}${!most ? ' is-unknown' : most.remaining! < 0 ? ' is-loss' : ''}${current ? ' is-current' : ''}`}>
      {onPhone && phoneHead(tier, current)}
      {best.has(tier) && <i className="commission-best" role="img" title="En kârlı aralık (bedenlerin en yüksek kârına göre)" aria-label="En kârlı aralık">★</i>}
      <strong title={bandLimitHint(owners.map(row => row.bands.find(item => item.tier === tier)))}>{bandLimitText(owners.map(row => row.bands.find(item => item.tier === tier))) ?? priceRange(choices.map(item => item.price))}</strong>
      {partner ? weekLines(owners, tier, first.productName ?? first.barcode) : <>
        <span>{rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`} komisyon</span>
        <ProfitBadge price={most ?? choices[0]} compact={!onPhone} title={first.productName ?? first.barcode} tag={`${tier}. Fiyat Aralığı`}
          aside={ranged ? `En çok kâr bırakan beden: ${mostRow?.size ?? mostRow?.barcode ?? '—'} · bedenlerde ${amountRange(lefts)}` : undefined} missing="Maliyet girilmemiş" />
      </>}
      {lefts.length > 0 && lefts.length < choices.length && <small>{(choices.length - lefts.length).toLocaleString('tr-TR')} varyantın kârı hesaplanamadı</small>}
      <button type="button" className="commission-switch" aria-pressed={all} aria-label={`${tier}. Fiyat Aralığı: ${all ? 'tüm varyantlar seçili' : 'tüm varyantlarını seç'}`} title={all ? undefined : 'Tüm Varyantlarını Seç'} onClick={() => pickModel(owners, tier, all)}><span />{all ? 'Tümü Seçili' : onPhone ? 'Tüm Varyantlarını Seç' : 'Tüm Varyantlar'}</button>
    </div>
  }
  // Kendi Fiyatın as the last card of a phone card's row.
  const ownCard = (content: ReactNode) => <div className="commission-choice commission-own-card"><span className="commission-seg-n">Kendi Fiyatın</span>{content}</div>
  const unmatchedNote = <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>
  const costText = (row: CommissionTariffRow) => row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'
  const nowRate = (rows: CommissionTariffRow[]) => {
    const rates = [...new Set(rows.flatMap(row => { const rate = row.currentBandRate ?? row.currentRate; return rate === null ? [] : [rate] }))]
    return rates.length === 0 ? null : rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`
  }
  // A product on a phone: its picture, name, codes and stock, its price now with the rate it pays now, its bands as a row
  // of cards that slides sideways (picking one is the table's pick), Kendi Fiyatın last; a model opens onto its sizes,
  // each with its own price, stock and row of bands.
  const phoneCard = ([key, group]: [string, CommissionTariffRow[]]) => {
    const single = group.length === 1
    const first = single ? group[0] : group.find(row => row.imageUrl) ?? group[0]
    const open = opened.has(key)
    const prices = group.flatMap(row => row.basisPrice === null ? [] : [row.basisPrice])
    const stock = group.some(row => row.stock != null) ? group.reduce((sum, row) => sum + (row.stock ?? 0), 0) : null
    const meta = [group[0].modelCode, first.color, single ? first.size : null, `Stok ${stockText(stock)}`].filter(Boolean).join(' · ')
    const rate = nowRate(group)
    const name = first.productName ?? first.barcode
    const state = single ? rowState(first) : null
    const unit = first.color ? 'beden' : 'varyant'
    return <article key={key} className={`commission-card${(state ? state.chosen !== null : group.some(row => pickedTier(row, picks.get(row.id), applied) !== null)) ? ' is-picked' : ''}`}>
      <div className="commission-card-head">
        {productImage(first)}
        <span>
          <span className="commission-card-name">{name}</span>
          <small>{meta}</small>
          {single && <small className="technical-text">{first.barcode}{first.sellerSku && first.sellerSku !== first.barcode ? ` · ${first.sellerSku}` : ''} · {costText(first)}</small>}
          {single && !first.matchesPanel && unmatchedNote}
          {state?.differs}
        </span>
      </div>
      <div className="commission-card-now">
        <span>Güncel <b>{prices.length ? priceRange(prices) : '—'}</b></span>
        {rate && <em>Şu an {rate}</em>}
      </div>
      {single && rateDisagrees(first) && <small className="commission-warn">Dosyadaki güncel komisyon {rateText(first.currentRate!)}, fiyatın bandından farklı.</small>}
      <div className="commission-ruler" role="group" aria-label={`${name} fiyat aralıkları`}>
        {state ? tiers.map(tier => variantChoice(first, tier, state.chosen, state.best, true)) : tiers.map(tier => modelChoice(group, first, tier, modelBest(group), true))}
        {ownCard(state
          ? <OwnPrice row={first} pick={state.pick} picked={state.chosen === 'own'} onPick={price => setRowPicks([first], price === null ? null : { tier: null, price })} />
          : <ModelOwnPrice rows={group} picks={picks} onPick={price => setRowPicks(group, price === null ? null : { tier: null, price })} />)}
      </div>
      {!single && open && <div className="commission-sizes">
        {group.map(row => {
          const { pick, chosen, differs, best } = rowState(row)
          return <div key={row.id} className="commission-size">
            <span className="commission-size-head"><b>{row.size ?? row.productName ?? row.barcode}</b><span>{row.basisPrice === null ? '—' : money(row.basisPrice)} · Stok {stockText(row.stock)}</span></span>
            <small className="technical-text">{row.barcode} · {costText(row)}</small>
            {!row.matchesPanel && unmatchedNote}
            {differs}
            <div className="commission-ruler" role="group" aria-label={`${row.size ?? row.barcode} fiyat aralıkları`}>
              {tiers.map(tier => variantChoice(row, tier, chosen, best, true))}
              {ownCard(<OwnPrice row={row} pick={pick} picked={chosen === 'own'} onPick={price => setRowPicks([row], price === null ? null : { tier: null, price })} />)}
            </div>
          </div>
        })}
      </div>}
      {!single && <button type="button" className="secondary commission-sizes-toggle" aria-expanded={open}
        onClick={() => setOpened(current => { const next = new Set(current); if (open) next.delete(key); else next.add(key); return next })}>
        {open ? (first.color ? 'Bedenleri Gizle' : 'Varyantları Gizle') : `${group.length.toLocaleString('tr-TR')} ${unit === 'beden' ? 'Bedeni' : 'Varyantı'} Ayrı Seç`}
      </button>}
    </article>
  }

  return <div className="commission-rows">
    <div className="commission-rows-head">
      <strong>{partner ? `${weekTabLabel([tariff, partner])} · ${periodName(tariff)} ve ${periodName(partner)} Bantları` : `${tariff.periodLabel?.trim() ? `${tariff.periodLabel.trim()} · ` : ''}${tariff.periodText} Bantları`}{partner ? <TitleHint label="Bantlar">Bant sınırları iki dönemde aynı, oranlar farklı; kartlarda her dönemin komisyonu ve kârı ile gün sayısına göre ağırlıklı ({periodDays(tariff)}/{periodDays(tariff) + periodDays(partner)} ve {periodDays(partner)}/{periodDays(tariff) + periodDays(partner)}) ortalama kâr görünür. Seçim iki döneme birden kaydedilir; Excel İndir bu ürünlere “{periodDays(tariff) + periodDays(partner)} Günlük Fiyat” yazar. Toplu Seçim’in kâr koşulu her dönemde ayrı uygulanır.</TitleHint> : manyPeriods ? <TitleHint label="Bantlar">Seçimler her dönem için ayrı kaydedilir; Excel İndir hepsini Trendyol dosyasına yazar (iki dönemde aynı fiyat “7 Günlük Fiyat”, tek dönemde o dönemin fiyatı). Farklı fiyat seçtiyseniz dosya ürün başına tek fiyat taşıdığından indirdiğiniz sekmenin ({openName}) fiyatı yazılır.</TitleHint> : null}</strong>
    </div>
    {/* Phones: the search over the whole width, Filtre and Toplu Seçim under it, each opening as a bottom sheet. */}
    {phone ? <div className="commission-phone-toolbar">
      <PhoneSearch value={filter.search ?? ''} onSearch={search => onFilter({ ...filter, search })} />
      <TariffFilters key={JSON.stringify(filter)} value={filter} facets={facets.data} onApply={onFilter} />
      <BulkPicker bulk={bulk} onApply={applyBulk} onClearColumn={clearColumn} onClearAll={clearAll} />
    </div> : <>
    {/* Which products the list shows. */}
    <TariffFilters key={JSON.stringify(filter)} value={filter} facets={facets.data} onApply={onFilter} />
    {/* The pick for every product: a band, or the best band by money or by share; with a condition on what it leaves. */}
    <div className="commission-plan-bar">
      <span className="commission-plan-count" role="status">{summary}{savingNote}</span>
      <div className="commission-zoom"><span>Tablo Boyutu</span><PanelSelect ariaLabel="Tablo boyutu" width={96} value={String(tableZoom)} options={tableZoomSteps.map(step => ({ value: String(step), label: `%${step}` }))} onChange={value => { const next = Number(value); setTableZoom(next); writeTableZoom(next) }} /></div>
      <BulkPicker bulk={bulk} onApply={applyBulk} onClearColumn={clearColumn} onClearAll={clearAll} />
      {downloadButton}
    </div>
    </>}
    {loading && <Busy />}
    {error !== null && <ErrorBox error={error} onRetry={onRetry} />}
    {rows && rows.items.length === 0 && <EmptyState icon="products" title="Ürün Bulunamadı">Filtreye uyan ürün yok.</EmptyState>}
    {phone && rows && rows.items.length > 0 && <div ref={tableTop} className="commission-cards">{shownGroups.map(phoneCard)}</div>}
    {/* Phones: the count and Excel İndir in a bar on the screen's bottom edge, joining the last card at the list's end. */}
    {phone && <StickyActionBar label="Seçimler" className="commission-phone-bar">
      <span className="commission-plan-count" role="status">{summary}{savingNote}</span>
      {downloadButton}
    </StickyActionBar>}
    {!phone && rows && rows.items.length > 0 && <div ref={tableTop} className="commission-table-wrap" style={tableZoom === 100 ? undefined : { zoom: tableZoom / 100 }}><table className={`commission-table is-plan${partner ? ' is-week' : ''}`}>
      <thead><tr><th scope="col" className="commission-expand" aria-label="Bedenler" /><SortHeader label="Ürün" sort="name" filter={filter} onFilter={onFilter} /><SortHeader label="Güncel Fiyat" sort="price" filter={filter} onFilter={onFilter} className="commission-now-col" />{tiers.map(tier => <SortHeader key={tier} label={`${tier}. Fiyat Aralığı`} sort={`tier${tier}` as TariffSort} filter={filter} onFilter={onFilter} />)}<th scope="col">Kendi Fiyatın</th></tr></thead>
      <tbody>{shownGroups.map(([key, group]) => {
        // A variant's row; under its model it shows its size, codes and cost, the model's name and picture being above.
        const variantRow = (row: CommissionTariffRow, nested: boolean) => {
          const { pick, chosen, differs, best } = rowState(row)
          return <tr key={row.id} className={`${nested ? 'is-variant' : ''}${chosen !== null ? ' is-picked' : ''}`.trim() || undefined}>
            <td className="commission-expand" />
            <th scope="row">{nested
              ? <span className="commission-variant-cell">
                <span className="commission-product">{row.size ?? row.productName ?? row.barcode}</span>
                <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                <small className="commission-stock-line">Stok {stockText(row.stock)}</small>
                {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
                {differs}
              </span>
              : <span className="commission-product-cell">
                {productImage(row)}
                <span>
                  <span className="commission-product">{row.productName ?? row.barcode}{row.size ? ` · ${row.size}` : ''}</span>
                  <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                  <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                  <small className="commission-stock-line">Stok {stockText(row.stock)}</small>
                  {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
                  {differs}
                </span>
              </span>}</th>
            <td className="commission-now-col">
              <span className="commission-price">{row.basisPrice === null ? '—' : money(row.basisPrice)}</span>
              {rateDisagrees(row) && <small className="commission-warn" title="Pazaryerinin dosyadaki güncel komisyonu, fiyatın bandından farklı.">Dosyada {rateText(row.currentRate!)}</small>}
            </td>
            {tiers.map(tier => {
              const card = variantChoice(row, tier, chosen, best)
              return card ? <td key={tier}>{card}</td> : <td key={tier} className="commission-choice-none">—</td>
            })}
            <td><OwnPrice row={row} pick={pick} picked={chosen === 'own'} onPick={price => setRowPicks([row], price === null ? null : { tier: null, price })} /></td>
          </tr>
        }
        if (group.length === 1) return variantRow(group[0], false)
        const first = group.find(row => row.imageUrl) ?? group[0]
        const open = opened.has(key)
        const prices = group.flatMap(row => row.basisPrice === null ? [] : [row.basisPrice])
        const best = modelBest(group)
        // The model's row: its picture, name and code, how many variants it has, and each band over all of them.
        return <Fragment key={key}>
          <tr className="is-model">
            <td className="commission-expand"><button type="button" className="commission-model-toggle" aria-expanded={open} aria-label={`${group.length.toLocaleString('tr-TR')} ${first.color ? 'beden' : 'varyant'} ${open ? 'gizle' : 'göster'}`}
              onClick={() => setOpened(current => { const next = new Set(current); if (open) next.delete(key); else next.add(key); return next })}><UiIcon name="chevronRight" size={16} /></button></td>
            <th scope="row"><span className="commission-product-cell">
              {productImage(first)}
              <span>
                <span className="commission-product">{first.productName ?? first.barcode}</span>
                <small className="technical-text">{group[0].modelCode}{first.color ? ` · ${first.color}` : ''}</small>
                <small className="commission-stock-line">Stok {stockText(group.some(row => row.stock != null) ? group.reduce((sum, row) => sum + (row.stock ?? 0), 0) : null)}</small>
                <small className="commission-model-count">{group.length.toLocaleString('tr-TR')} {first.color ? 'beden' : 'varyant'}</small>
              </span>
            </span></th>
            <td className="commission-now-col"><span className="commission-price">{prices.length ? priceRange(prices) : '—'}</span></td>
            {tiers.map(tier => {
              const card = modelChoice(group, first, tier, best)
              return card ? <td key={tier}>{card}</td> : <td key={tier} className="commission-choice-none">—</td>
            })}
            <td><ModelOwnPrice rows={group} picks={picks} onPick={price => setRowPicks(group, price === null ? null : { tier: null, price })} /></td>
          </tr>
          {open && group.map(row => variantRow(row, true))}
        </Fragment>
      })}</tbody>
    </table></div>}
    {groupPageCount > 1 && <div className="commission-pager" role="group" aria-label="Model sayfaları"><button type="button" className="secondary" disabled={groupPage === 0} onClick={() => showGroupPage(groupPage - 1)}>Önceki</button><span>{(groupPage * modelsPerView + 1).toLocaleString('tr-TR')}–{Math.min(modelGroups.length, (groupPage + 1) * modelsPerView).toLocaleString('tr-TR')} / {modelGroups.length.toLocaleString('tr-TR')} Model</span><button type="button" className="secondary" disabled={groupPage + 1 >= groupPageCount} onClick={() => showGroupPage(groupPage + 1)}>Sonraki</button></div>}
    {rows && pageCount > 1 && <div className="commission-pager"><button type="button" className="secondary" disabled={page === 0} onClick={() => onPage(page - 1)}>Önceki</button><span>{(page + 1).toLocaleString('tr-TR')} / {pageCount.toLocaleString('tr-TR')}</span><button type="button" className="secondary" disabled={page + 1 >= pageCount} onClick={() => onPage(page + 1)}>Sonraki</button></div>}
  </div>
}

// A price of the seller's own as typed in Turkish ("1.250" is 1250, "1.250,50" is 1250.5), or null when it is not a
// price above zero.
export function ownPriceValue(text: string): number | null {
  const number = parseTrNumber(text)
  return number !== null && number > 0 ? number : null
}

// A price of the seller's own for all of a model's sizes at once: each size takes it as its own pick (and its own field
// shows it when opened); what it leaves is the range over the sizes, and the sizes it puts in no band are counted.
function ModelOwnPrice({ rows, picks, onPick }: { rows: CommissionTariffRow[]; picks: Map<string, TariffPick>; onPick: (price: number | null) => void }) {
  const shared = rows.map(row => picks.get(row.id)?.price ?? null)
  const [text, setText] = useState(() => shared[0] !== null && shared.every(price => price === shared[0]) ? String(shared[0]).replace('.', ',') : '')
  const parse = ownPriceValue
  const price = parse(text)
  const choices = price === null ? [] : rows.map(row => ownChoice(row, price))
  const banded = choices.filter((choice): choice is CommissionChoice => choice !== null)
  const tiersHit = [...new Set(banded.map(choice => choice.tier))].sort()
  const lefts = banded.flatMap(choice => choice.remaining === null ? [] : [choice.remaining])
  return <div className={`commission-own${price !== null && shared.every(value => value === price) ? ' is-picked' : ''}`}>
    <input type="text" inputMode="decimal" placeholder="Fiyat Gir" value={text} aria-label={`${rows[0]?.productName ?? 'Model'} için tüm bedenlere kendi fiyatın`}
      onChange={event => { setText(event.target.value); onPick(parse(event.target.value)) }} />
    {text.trim() !== '' && (banded.length ? <small>{tiersHit.map(tier => `${tier}.`).join(', ')} fiyat aralığı · {rows.length} beden<b>{lefts.length ? `${amountRange(lefts)} Kâr` : 'Maliyet girilmemiş'}</b>{banded.length < rows.length && <span className="commission-warn">{rows.length - banded.length} beden hiçbir fiyat aralığına düşmüyor</span>}</small> : <small className="commission-warn">Hiçbir fiyat aralığına düşmüyor</small>)}
  </div>
}

// A price of the seller's own for one product: typed with a comma or a point, it shows the band it falls in and what
// it leaves, and becomes the row's pick.
function OwnPrice({ row, pick, picked, onPick }: { row: CommissionTariffRow; pick: TariffPick | undefined; picked: boolean; onPick: (price: number | null) => void }) {
  const [text, setText] = useState(() => pick?.price ? String(pick.price).replace('.', ',') : '')
  const parse = ownPriceValue
  const price = parse(text)
  const choice = price === null ? null : ownChoice(row, price)
  return <div className={`commission-own${picked ? ' is-picked' : ''}`}>
    <input type="text" inputMode="decimal" placeholder="Fiyat girin" value={text} aria-label={`${row.productName ?? row.barcode} için kendi fiyatın`}
      onChange={event => { setText(event.target.value); onPick(parse(event.target.value)) }} />
    {text.trim() !== '' && (choice ? <small>{choice.tier}. fiyat aralığı · {rateText(choice.rate)}<ProfitBadge price={choice} compact title={row.productName ?? row.barcode} tag="Kendi Fiyatın" missing={row.costPrice ? 'Kâr hesaplanamadı' : 'Maliyet girilmemiş'} /></small> : <small className="commission-warn">Hiçbir fiyat aralığına düşmüyor</small>)}
  </div>
}
