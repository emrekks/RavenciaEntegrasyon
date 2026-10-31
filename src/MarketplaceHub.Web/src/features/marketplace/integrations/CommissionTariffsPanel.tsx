import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent, type DragEvent, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubFile } from '../../../shared/api'
import { Busy, ErrorBox, UiIcon, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { compareOptionValues, ImageLightboxModal } from '../../catalog/shared'
import { formatIstanbul } from '../../../shared/format-date'
import { thumbnailUrl } from '../../../shared/product-thumbnail'
import { kurus, onCost, profitText, ProfitBadge, unitProfit, type UnitProfitCosts } from './unit-profit'
import { useDismissable } from '../../../shared/hooks/useDismissable'
import '../commission-tariffs.css'

// The marketplaces whose commission tariff export the panel reads (CommissionTariffPlatforms on the server).
export const commissionTariffPlatforms = new Set(['TRENDYOL'])

export type CommissionBand = { tier: number; min: number | null; max: number | null; rate: number }
// One period of an uploaded file. A file with more than one period (Trendyol's "3 Gün" and "4 Gün" of a week) lists a
// tariff for each, sharing groupId (the file's first period); periodLabel is the file's name for the period. Both are
// absent in an older server's answer.
export type CommissionTariff = { id: string; platformCode: string; periodStart: string; periodEnd: string; periodText: string; fileName: string; uploadedAt: string; rowCount: number; matchedRowCount: number; isCurrent: boolean
  periodLabel?: string | null; groupId?: string | null }
// An uploaded file with its periods, the earliest first.
export type TariffGroup = { id: string; periods: CommissionTariff[] }

// The listed tariffs by file, in the list's order (the latest file first); a tariff without a group is a file of its own.
export function tariffGroups(tariffs: CommissionTariff[]): TariffGroup[] {
  const groups = new Map<string, CommissionTariff[]>()
  for (const tariff of tariffs) {
    const key = tariff.groupId ?? tariff.id
    groups.set(key, [...(groups.get(key) ?? []), tariff])
  }
  return [...groups].map(([id, periods]) => ({ id, periods: [...periods].sort((a, b) => Date.parse(a.periodStart) - Date.parse(b.periodStart)) }))
}

// The period a file opens on: the one in force now, else its first.
export const defaultPeriod = (periods: CommissionTariff[]) => periods.find(period => period.isCurrent) ?? periods[0] ?? null

// The stored end is exclusive (the minute after the file's last one); a period is shown up to its own last minute.
const lastMinute = (end: string) => new Date(Date.parse(end) - 60_000)
const shortDay = (value: string | Date, year: boolean) => formatIstanbul(value, year ? { day: 'numeric', month: 'short', year: 'numeric' } : { day: 'numeric', month: 'short' })
// The span of a file's periods: "29 Eki 2026 – 5 Kas 2026".
export function groupRangeText(periods: CommissionTariff[]) {
  if (!periods.length) return '—'
  const end = periods.reduce((last, period) => Date.parse(period.periodEnd) > Date.parse(last) ? period.periodEnd : last, periods[0].periodEnd)
  return `${shortDay(periods[0].periodStart, true)} – ${shortDay(lastMinute(end), true)}`
}
// A period's length in whole days (3 for 29 Ekim 08.00 – 1 Kasım 07.59).
export const periodDays = (period: Pick<CommissionTariff, 'periodStart' | 'periodEnd'>) => Math.max(1, Math.round((Date.parse(period.periodEnd) - Date.parse(period.periodStart)) / 86_400_000))
// A period's short name: the file's ("3 Gün"), else its length in days.
export const periodName = (period: CommissionTariff) => period.periodLabel?.trim() || `${periodDays(period)} Gün`
// A period's tab: its name (else its length in days) and its days, "3 Gün (29 Eki – 1 Kas 2026)".
export function periodTabLabel(period: CommissionTariff) {
  return `${periodName(period)} (${shortDay(period.periodStart, false)} – ${shortDay(lastMinute(period.periodEnd), true)})`
}
// The tab of a file's periods together: "7 Gün (29 Eki – 5 Kas 2026)".
export function weekTabLabel(periods: CommissionTariff[]) {
  const days = periods.reduce((sum, period) => sum + periodDays(period), 0)
  return `${days} Gün (${shortDay(periods[0].periodStart, false)} – ${shortDay(lastMinute(periods[periods.length - 1].periodEnd), true)})`
}
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
type RowPage = { items: CommissionTariffRow[]; totalCount: number; page: number; pageSize: number }

// A row's own pick: a band, a price of the seller's own, or neither (the row left out of the Excel).
export type TariffPick = { tier: number | null; price: number | null }
// The pick for every row: one band ('tier'), or the band leaving the most money ('amount') or the most on its cost
// ('rate'); with a least share of the cost (per cent) or amount (lira) a row whose pick leaves less is left out, with a
// most share or amount one whose pick leaves more.
export type TariffBulk = { kind: '' | 'tier' | 'amount' | 'rate'; tier: number; minRate: number | null; minAmount: number | null; maxRate?: number | null; maxAmount?: number | null }
export const noBulk: TariffBulk = { kind: '', tier: 1, minRate: null, minAmount: null, maxRate: null, maxAmount: null }

// A tariff's picks as the server keeps them (GET and PUT …/selection): the pick for every row and each row's own pick.
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

const amount = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
// "752,64 – 791,01 ₺", or one price when all are the same.
export function priceRange(values: number[]) {
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? money(low) : `${amount(low)} – ${money(high)}`
}
// A band's prices, both limits included as Trendyol's file gives them (a band's lower limit is a kuruş over the next
// band's upper one): the top band from its lower limit up ("464,63 ₺ ve üzeri"), the bottom band up to its upper limit
// ("388,63 ₺ ve altı"), a band between them from limit to limit ("432,27 – 464,62 ₺"). Over a model's variants with
// different limits, the prices they cover together.
export function bandLimitText(bands: Array<CommissionBand | undefined>) {
  const found = bands.filter((band): band is CommissionBand => band != null)
  if (!found.length) return null
  const mins = found.flatMap(band => band.min === null ? [] : [band.min])
  const maxes = found.flatMap(band => band.max === null ? [] : [band.max])
  if (found[0].max === null) return mins.length ? `${priceRange(mins)} ve üzeri` : 'Tüm fiyatlar'
  if (found[0].min === null || !mins.length) return maxes.length ? `${priceRange(maxes)} ve altı` : null
  return `${amount(Math.min(...mins))} – ${money(Math.max(...maxes))}`
}
// The same band in words, for its hint: "432,27 ₺ ile 464,62 ₺ arası (ikisi de dahil)".
export function bandLimitHint(bands: Array<CommissionBand | undefined>) {
  const found = bands.filter((band): band is CommissionBand => band != null)
  const texts = [...new Set(found.map(band => band.min !== null && band.max !== null ? `${money(band.min)} ile ${money(band.max)} arası (ikisi de dahil)`
    : band.min !== null ? `${money(band.min)} ve üzeri (${money(band.min)} dahil)` : band.max !== null ? `${money(band.max)} ve altı (${money(band.max)} dahil)` : 'Tüm fiyatlar'))]
  return texts.length === 1 ? texts[0] : texts.length ? `Bedenlere göre: ${texts.join('; ')}` : undefined
}
// What a model's variants leave, with its sign: "266,09 – 375,71 ₺".
export function amountRange(values: number[]) {
  const signed = (value: number) => `${value < 0 ? '− ' : ''}${amount(Math.abs(value))}`
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? `${signed(low)} ₺` : `${signed(low)} – ${signed(high)} ₺`
}

// A product's photo; a click opens it full size as the products page does (its preview, Escape or a click outside
// closes it), without reaching the row. Without a photo, the name's first letter.
export function ProductImage({ url, title, initial }: { url: string | null | undefined; title: string; initial: string }) {
  const [open, setOpen] = useState(false)
  if (!url) return <i className="commission-product-image" aria-hidden="true">{initial}</i>
  return <>
    <button type="button" className="commission-product-zoom" aria-label={`${title} görselini büyüt`} onClick={event => { event.preventDefault(); event.stopPropagation(); setOpen(true) }}>
      <img src={thumbnailUrl(url)} alt="" loading="lazy" className="commission-product-image" />
    </button>
    {open && <ImageLightboxModal image={{ url, title }} onClose={() => setOpen(false)} />}
  </>
}

const productImage = (row: CommissionTariffRow) =>
  <ProductImage url={row.imageUrl} title={`${row.productName ?? row.barcode}${row.color ? ` · ${row.color}` : ''}`} initial={(row.productName ?? row.barcode).slice(0, 1)} />

// A price's profit with its share of the cost: "100,85 ₺ Kâr (%63,03)".
export function remainingText(choice: CommissionChoice) {
  return choice.remaining === null ? 'Maliyet girilmemiş' : profitText(choice.remaining, choice.costs)
}
type UploadResult = { tariffs: CommissionTariff[]; importedRows: number; skippedRows: number; alreadyUploaded: boolean; warnings: string[] }

const money = (value: number) => `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₺`
export const rateText = (rate: number) => `%${rate.toLocaleString('tr-TR', { maximumFractionDigits: 2 })}`

// "≥ 470,64 ₺", "435,98 – 470,63 ₺" or "≤ 388,63 ₺": the price range a band covers.
export function bandRange(band: CommissionBand) {
  if (band.min !== null && band.max !== null) return `${band.min.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} – ${money(band.max)}`
  if (band.min !== null) return `≥ ${money(band.min)}`
  if (band.max !== null) return `≤ ${money(band.max)}`
  return '—'
}

// The price that earns the next lower commission, or why there is none.
export function nextBandHint(row: Pick<CommissionTariffRow, 'nextLowerBand' | 'currentTier' | 'basisPrice'>) {
  if (row.basisPrice === null) return 'Komisyona esas fiyat yok'
  if (row.currentTier === null) return 'Fiyat hiçbir banda düşmüyor'
  if (!row.nextLowerBand?.max) return 'En düşük komisyon bandında'
  return `${rateText(row.nextLowerBand.rate)} için ≤ ${money(row.nextLowerBand.max)}`
}

// The marketplace's own "Güncel Komisyon" is shown beside the band the panel computes; a difference is flagged, never hidden.
export function rateDisagrees(row: Pick<CommissionTariffRow, 'currentRate' | 'currentBandRate'>) {
  return row.currentRate !== null && row.currentBandRate !== null && Math.abs(row.currentRate - row.currentBandRate) > 0.0001
}

// A file's span from its first period's start to its last one's end; the stored end is exclusive (the minute after the
// file's last one), so the label shows the file's own last minute.
const periodLabel = (periods: CommissionTariff[]) => `${formatIstanbul(periods[0].periodStart, { day: '2-digit', month: 'long', hour: '2-digit', minute: '2-digit' })} – ${formatIstanbul(lastMinute(periods[periods.length - 1].periodEnd), { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })}`

// Weekly commission tariffs the seller downloads from the marketplace and uploads here. The panel reads a sale's
// commission from the band its price falls in when the order does not carry it, and shows which price earns a lower
// band. Nothing is sent to the marketplace.
export function CommissionTariffsPanel({ connectionId, platformName }: { connectionId: string; platformName: string }) {
  const client = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const [filter, setFilter] = useState<TariffFilter>(noFilter)
  const [page, setPage] = useState(0)
  const [message, setMessage] = useState<{ kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] } | null>(null)
  const [confirm, confirmDialog] = useConfirmDialog()
  const tariffs = useQuery({ queryKey: ['commission-tariffs', connectionId], queryFn: () => hubApi<CommissionTariff[]>(`/connections/${connectionId}/commission-tariffs`) })
  // The list shows a file once; its periods are the tabs over the table. The open period is the one picked, else the
  // period in force now of the file in force now, else the first file's.
  const groups = useMemo(() => tariffGroups(tariffs.data ?? []), [tariffs.data])
  const activeGroup = groups.find(group => group.periods.some(period => period.id === selected)) ?? groups.find(group => group.periods.some(period => period.isCurrent)) ?? groups[0] ?? null
  // The 7-day tab of a file with two periods (Trendyol's 3 Gün and 4 Gün): the first period's table with the second's
  // commission and profit beside each band, and a pick made for both.
  const [week, setWeek] = useState(false)
  const weekPeriods = week && activeGroup?.periods.length === 2 ? activeGroup.periods : null
  const active = weekPeriods ? weekPeriods[0] : activeGroup?.periods.find(period => period.id === selected) ?? (activeGroup ? defaultPeriod(activeGroup.periods) : null)
  const rows = useQuery({
    queryKey: ['commission-tariff-rows', connectionId, active?.id, filter, page],
    queryFn: () => hubApi<RowPage>(`/connections/${connectionId}/commission-tariffs/${active!.id}/rows?${new URLSearchParams({ page: String(page), limit: '50',
      ...filterQuery(filter) })}`),
    enabled: active !== null
  })
  const upload = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<UploadResult>(`/connections/${connectionId}/commission-tariffs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff') }, body })
    },
    onSuccess: async result => {
      setSelected(defaultPeriod(result.tariffs)?.id ?? null)
      setWeek(false)
      setPage(0)
      setMessage(result.alreadyUploaded
        ? { kind: 'info', text: 'Bu dosya bu dönem için daha önce yüklenmiş; mevcut tarife gösteriliyor.' }
        : { kind: result.skippedRows ? 'info' : 'success', text: `${result.importedRows.toLocaleString('tr-TR')} ürünün tarifesi yüklendi${result.skippedRows ? `; ${result.skippedRows.toLocaleString('tr-TR')} satır okunamadı` : ''}.`, warnings: result.warnings })
      await client.invalidateQueries({ queryKey: ['commission-tariffs', connectionId] })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife yüklenemedi.' })
  })
  const remove = useMutation({
    mutationFn: (tariff: CommissionTariff) => hubApi<boolean>(`/connections/${connectionId}/commission-tariffs/${tariff.id}`, { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-delete') } }),
    onSuccess: async () => {
      setSelected(null)
      setMessage({ kind: 'success', text: 'Tarife silindi.' })
      await client.invalidateQueries({ queryKey: ['commission-tariffs', connectionId] })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife silinemedi.' })
  })
  const choose = (file: File | undefined) => {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.xlsx')) { setMessage({ kind: 'error', text: 'Yalnız pazaryerinden indirilen .xlsx tarife dosyası yüklenebilir.' }); return }
    setMessage(null)
    upload.mutate(file)
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  // A file goes with all its periods (the server deletes them together).
  const askDelete = async (group: TariffGroup) => {
    const periods = group.periods.map(period => period.periodText).join(' ve ')
    const many = group.periods.length > 1
    if (!(await confirm({ title: 'Tarife Silinsin mi?', message: `${periods} ${many ? 'dönemlerine' : 'dönemine'} ait tarife, ${group.periods[0].rowCount.toLocaleString('tr-TR')} ürün satırı ve kaydedilen seçimler silinecek. ${many ? 'Bu dönemlerin' : 'Bu dönemin'} satışlarında komisyon bu tarifeden okunamaz.`, confirmLabel: 'Tarifeyi Sil', destructive: true }))) return
    remove.mutate(group.periods[0])
  }
  const pageCount = rows.data ? Math.max(1, Math.ceil(rows.data.totalCount / rows.data.pageSize)) : 1

  return <section className="panel commission-tariffs" aria-labelledby="commission-tariffs-title">
    <div className="cs-sec-head"><h3 id="commission-tariffs-title">Komisyon Tarifeleri</h3><p>{platformName}’un haftalık komisyon tarifesi dosyasını yükleyin. Siparişte komisyon bilgisi yoksa satışın tarihine ve fiyatına göre komisyon bu tarifeden bulunur; fiyatınızın hangi banda düştüğünü ve bir alt banda inmek için gereken fiyatı görürsünüz. Pazaryerine hiçbir şey gönderilmez.</p></div>
    <div className={`commission-dropzone${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
      <strong>{upload.isPending ? 'Tarife okunuyor…' : 'Tarife dosyasını buraya bırakın'}</strong>
      <span>{platformName} satıcı panelinden indirilen .xlsx dosyası · en fazla 10 MiB</span>
      <button type="button" className="secondary" disabled={upload.isPending} onClick={() => input.current?.click()}>Dosya Seç</button>
      <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
    </div>
    {message && <div className={`commission-message is-${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}><p>{message.text}</p>{message.warnings?.length ? <ul>{message.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul> : null}</div>}

    {tariffs.isLoading && <Busy />}
    {tariffs.isError && <ErrorBox error={tariffs.error} onRetry={() => void tariffs.refetch()} />}
    {tariffs.data && tariffs.data.length === 0 && <p className="cs-empty">Henüz tarife yüklenmedi. Komisyon yalnız siparişin kendi bilgisinden okunuyor.</p>}
    {groups.length > 0 && <div className="commission-periods" role="list">
      {groups.map(group => {
        const first = group.periods[0]
        const on = group.id === activeGroup?.id
        const many = group.periods.length > 1
        return <div key={group.id} role="listitem" className={`commission-period${on ? ' is-on' : ''}`}>
          <button type="button" className="commission-period-main" aria-pressed={on} onClick={() => { setSelected(defaultPeriod(group.periods)?.id ?? null); setWeek(false); setPage(0) }}>
            <span className="commission-period-title">{periodLabel(group.periods)}{group.periods.some(period => period.isCurrent) && <em className="commission-badge is-current">Geçerli</em>}</span>
            <small>{many ? `${group.periods.length.toLocaleString('tr-TR')} dönem (${group.periods.map(period => period.periodLabel?.trim() || period.periodText).join(', ')}) · ` : ''}{first.rowCount.toLocaleString('tr-TR')} ürün · {first.matchedRowCount.toLocaleString('tr-TR')} panelde eşleşti · {first.fileName} · {formatIstanbul(first.uploadedAt, { dateStyle: 'medium', timeStyle: 'short' })}</small>
          </button>
          <button type="button" className="secondary commission-period-delete" disabled={remove.isPending} onClick={() => void askDelete(group)} aria-label={`${group.periods.map(period => period.periodText).join(' ve ')} tarifesini sil`}>Sil</button>
        </div>
      })}
    </div>}

    {/* A file with more than one period: its span and a tab for each period; the table, the band cards, Toplu Seçim and
        the picks are the open period's. */}
    {activeGroup && active && activeGroup.periods.length > 1 && <div className="commission-period-switch">
      <span className="commission-period-range">Geçerli Tarih Aralığı: <strong>{groupRangeText(activeGroup.periods)}</strong></span>
      <div className="commission-period-tabs" role="group" aria-label="Tarife dönemi">
        {activeGroup.periods.map(period => <button key={period.id} type="button" aria-pressed={!weekPeriods && period.id === active.id} onClick={() => { setSelected(period.id); setWeek(false); setPage(0) }}>
          {periodTabLabel(period)}{period.isCurrent && <em>Şu An</em>}
        </button>)}
        {activeGroup.periods.length === 2 && <button type="button" aria-pressed={weekPeriods !== null} onClick={() => { setSelected(activeGroup.periods[0].id); setWeek(true); setPage(0) }}>
          {weekTabLabel(activeGroup.periods)}
        </button>}
      </div>
    </div>}

    {active && <TariffPlan key={`${active.id}${weekPeriods ? '-week' : ''}`} connectionId={connectionId} tariff={active} partner={weekPeriods?.[1]} periods={activeGroup?.periods ?? [active]} rows={rows.data} loading={rows.isLoading} error={rows.isError ? rows.error : null} onRetry={() => void rows.refetch()}
      filter={filter} onFilter={value => { setFilter(value); setPage(0) }} page={page} pageCount={pageCount} onPage={setPage} onMessage={setMessage} />}
    {confirmDialog}
  </section>
}

type PanelMessage = { kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] }
const tiers = [1, 2, 3, 4]
// Which rows the list shows: by the band their price is in now, and whether their cost is entered.
export type TariffFilter = { tier: '' | '1' | '2' | '3' | '4'; cost: '' | 'with' | 'without'; name: string; barcode: string; model: string; minStock: string; maxStock: string; category: string; brand: string; sort?: TariffSort; dir?: 'asc' | 'desc'
  // Fiyat Aralığı: a band and what its price leaves after commission and cost, in lira and as a share of the price.
  profitTier?: '' | '1' | '2' | '3' | '4'; minProfit?: string; maxProfit?: string; minShare?: string; maxShare?: string }
export const noFilter: TariffFilter = { tier: '', cost: '', name: '', barcode: '', model: '', minStock: '', maxStock: '', category: '', brand: '' }
// A header's order: by name, by the current price, or by what a band leaves.
export type TariffSort = 'name' | 'price' | 'tier1' | 'tier2' | 'tier3' | 'tier4'

// A header click goes up, then down, then back to no order.
export function nextSort(filter: TariffFilter, sort: TariffSort): TariffFilter {
  if (filter.sort !== sort) return { ...filter, sort, dir: 'asc' }
  return filter.dir === 'asc' ? { ...filter, sort, dir: 'desc' } : { ...filter, sort: undefined, dir: undefined }
}

// The rows query's parameters for a filter: empty fields left out, a stock that is not a number left out.
export function filterQuery(filter: TariffFilter): Record<string, string> {
  const query: Record<string, string> = {}
  for (const key of ['tier', 'cost', 'name', 'barcode', 'model', 'category', 'brand'] as const) if (filter[key].trim()) query[key] = filter[key].trim()
  for (const key of ['minStock', 'maxStock'] as const) {
    const value = Number(filter[key].trim().replace(',', '.'))
    if (filter[key].trim() && Number.isFinite(value) && value >= 0) query[key] = String(value)
  }
  // A profit range counts only with a band picked; a loss is typed with a minus sign.
  if (filter.profitTier) {
    let ranged = false
    for (const key of ['minProfit', 'maxProfit', 'minShare', 'maxShare'] as const) {
      const text = (filter[key] ?? '').trim().replace(',', '.').replace('−', '-')
      const value = Number(text)
      if (text && Number.isFinite(value)) { query[key] = String(value); ranged = true }
    }
    if (ranged) query.profitTier = filter.profitTier
  }
  if (filter.sort) { query.sort = filter.sort; query.dir = filter.dir ?? 'asc' }
  return query
}

// A filter card's field as the other filter cards draw it: its label above the input or list.
export function FilterField({ label, className, children }: { label: string; className?: string; children: ReactNode }) {
  return <label className={['commission-field', className].filter(Boolean).join(' ')}><span>{label}</span>{children}</label>
}

// A range field: one label over the least and the most value, each still named on its own for a screen reader.
export function FilterRange({ label, inputMode, min, max, onMin, onMax, minLabel, maxLabel }: {
  label: string; inputMode: 'numeric' | 'decimal'; min: string; max: string
  onMin: (event: ChangeEvent<HTMLInputElement>) => void; onMax: (event: ChangeEvent<HTMLInputElement>) => void; minLabel: string; maxLabel: string
}) {
  return <div className="commission-field" role="group" aria-label={label}>
    <span>{label}</span>
    <span className="commission-filter-range">
      <input type="text" inputMode={inputMode} placeholder="Min" value={min} onChange={onMin} aria-label={minLabel} />
      <i aria-hidden="true">–</i>
      <input type="text" inputMode={inputMode} placeholder="Max" value={max} onChange={onMax} aria-label={maxLabel} />
    </span>
  </div>
}

// Temizle and Filtrele, closing the card on the right as on the other filter cards.
export function FilterActions({ onClear }: { onClear: () => void }) {
  return <div className="commission-filter-actions">
    <button type="button" className="secondary commission-filter-clear" onClick={onClear}>Temizle</button>
    <button type="submit" className="rv-button-primary commission-filter-apply">Filtrele</button>
  </div>
}

// Which products the list shows: the fields are typed first and applied with Filtrele (Enter too); Temizle
// empties them and shows every product again.
function TariffFilters({ value, facets, onApply }: { value: TariffFilter; facets: { categories: string[]; brands: string[] } | undefined; onApply: (filter: TariffFilter) => void }) {
  const [draft, setDraft] = useState(value)
  const set = (key: keyof TariffFilter) => (event: { target: { value: string } }) => setDraft(current => ({ ...current, [key]: event.target.value }))
  const apply = (event?: FormEvent) => { event?.preventDefault(); onApply(draft) }
  const clear = () => { setDraft(noFilter); onApply(noFilter) }
  const noFacts = facets !== undefined && facets.categories.length === 0 && facets.brands.length === 0
  return <form className="commission-filters" onSubmit={apply} aria-label="Ürün filtreleri">
    <FilterField label="Ürün Adı"><input type="search" value={draft.name} onChange={set('name')} /></FilterField>
    <FilterField label="Barkod"><input type="search" value={draft.barcode} onChange={set('barcode')} /></FilterField>
    <FilterField label="Model Kodu"><input type="search" value={draft.model} onChange={set('model')} /></FilterField>
    <FilterRange label="Stok" inputMode="numeric" min={draft.minStock} max={draft.maxStock} onMin={set('minStock')} onMax={set('maxStock')} minLabel="En az stok" maxLabel="En çok stok" />
    <FilterField label="Kategori"><select value={draft.category} onChange={set('category')} disabled={!facets?.categories.length}>
      <option value="">Tümü</option>{facets?.categories.map(item => <option key={item} value={item}>{item}</option>)}
    </select></FilterField>
    <FilterField label="Marka"><select value={draft.brand} onChange={set('brand')} disabled={!facets?.brands.length}>
      <option value="">Tümü</option>{facets?.brands.map(item => <option key={item} value={item}>{item}</option>)}
    </select></FilterField>
    <FilterField label="Kolon Seçimi" className="commission-filter-band"><select value={draft.profitTier ?? ''} onChange={set('profitTier')}>
      <option value="">Seçilmedi</option>{tiers.map(tier => <option key={tier} value={String(tier)}>{tier}. Fiyat Aralığı</option>)}
    </select></FilterField>
    {draft.profitTier && <div className="commission-filter-profit">
      <FilterRange label="Kâr Tutarı (₺)" inputMode="decimal" min={draft.minProfit ?? ''} max={draft.maxProfit ?? ''} onMin={set('minProfit')} onMax={set('maxProfit')} minLabel="En az kâr tutarı (₺)" maxLabel="En çok kâr tutarı (₺)" />
      <FilterRange label="Kâr Oranı (%)" inputMode="decimal" min={draft.minShare ?? ''} max={draft.maxShare ?? ''} onMin={set('minShare')} onMax={set('maxShare')} minLabel="En az kâr oranı (%)" maxLabel="En çok kâr oranı (%)" />
    </div>}
    {noFacts && <p className="commission-filter-note">Kategori ve marka bu tarifede yok: tarife dosyası önceden yüklendi. Aynı dosyayı yeniden yüklediğinizde dolar.</p>}
    <FilterActions onClear={clear} />
  </form>
}

// A column's header with its order beside it: up (A–Z, low to high), down, then none. A band orders by what it leaves.
function SortHeader({ label, sort, filter, onFilter }: { label: string; sort: TariffSort; filter: TariffFilter; onFilter: (filter: TariffFilter) => void }) {
  const on = filter.sort === sort ? filter.dir ?? 'asc' : null
  return <th scope="col" aria-sort={on === 'asc' ? 'ascending' : on === 'desc' ? 'descending' : 'none'}>
    <button type="button" className={`commission-sort${on ? ' is-on' : ''}`} onClick={() => onFilter(nextSort(filter, sort))}
      title={on === null ? 'Artan sırala' : on === 'asc' ? 'Azalan sırala' : 'Sıralamayı kaldır'}>
      {label}<span aria-hidden="true">{on === 'asc' ? '↑' : on === 'desc' ? '↓' : '↕'}</span>
    </button>
  </th>
}

const bulkLabel = (bulk: TariffBulk) => bulk.kind === 'tier' ? `${bulk.tier}. Fiyat Aralığı` : bulk.kind === 'amount' ? 'En Yüksek Kâr Tutarı' : 'En Yüksek Kâr Oranı'

// Toplu Seçim: a column (a price band), then a criterion on its profit (its share
// of the cost or its amount) above or below a value, and the buttons that apply it, take that column's picks back or
// take every pick back.
function BulkPicker({ bulk, onApply, onClearColumn, onClearAll }: { bulk: TariffBulk; onApply: (bulk: TariffBulk) => void; onClearColumn: (column: string) => void; onClearAll: () => void }) {
  const [open, setOpen] = useState(false)
  // Only the price bands can be picked as a column.
  const [column, setColumn] = useState(bulk.kind === 'tier' ? String(bulk.tier) : '')
  // There is no unconditional choice: a bulk pick always filters on profit, by share (%) unless the saved pick used an amount.
  const [criterion, setCriterion] = useState<'rate' | 'amount'>(bulk.minAmount != null || bulk.maxAmount != null ? 'amount' : 'rate')
  const [above, setAbove] = useState(bulk.maxRate == null && bulk.maxAmount == null)
  const [value, setValue] = useState(String(bulk.minRate ?? bulk.minAmount ?? bulk.maxRate ?? bulk.maxAmount ?? ''))
  const ref = useRef<HTMLDivElement>(null)
  useDismissable(open, [ref], () => setOpen(false))
  const limit = Number(value.replace(',', '.'))
  const valid = column !== '' && value.trim() !== '' && Number.isFinite(limit)
  const apply = () => {
    const tierColumn = /^[1-4]$/.test(column)
    const at = valid ? limit : null
    onApply({ kind: tierColumn ? 'tier' : column as TariffBulk['kind'], tier: tierColumn ? Number(column) : 1,
      minRate: criterion === 'rate' && above ? at : null, minAmount: criterion === 'amount' && above ? at : null,
      maxRate: criterion === 'rate' && !above ? at : null, maxAmount: criterion === 'amount' && !above ? at : null })
    setOpen(false)
  }
  const active = bulk.kind !== ''
  return <div className="commission-bulk" ref={ref}>
    <button type="button" className={`commission-bulk-trigger${active ? ' is-active' : ''}`} aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      {active && <em>Filtre Aktif</em>}Toplu Seçim <UiIcon name="chevronDown" size={14} />
    </button>
    {open && <div className="commission-bulk-popover" role="dialog" aria-label="Toplu seçim">
      <label><span>Kolon</span>
        <select value={column} onChange={event => setColumn(event.target.value)}>
          <option value="">Kolon Seçimi</option>
          {tiers.map(tier => <option key={tier} value={String(tier)}>{tier}. Fiyat Aralığı</option>)}
        </select>
      </label>
      {column !== '' && <>
      <hr />
      <label><span>Ölçüt</span>
        <select value={criterion} onChange={event => setCriterion(event.target.value as 'rate' | 'amount')}>
          <option value="rate">Kâr / Zarar Oranına Göre (%)</option><option value="amount">Kâr / Zarar Tutarına Göre (₺)</option>
        </select>
      </label>
      <label><span>Karşılaştırma</span>
        <select value={above ? 'above' : 'below'} onChange={event => setAbove(event.target.value === 'above')}>
          <option value="above">Verilenden Büyük Değerler</option><option value="below">Verilenden Küçük Değerler</option>
        </select>
      </label>
      <label><span>Değer</span>
        <input type="text" inputMode="decimal" value={value} onChange={event => setValue(event.target.value)} placeholder={criterion === 'rate' ? 'ör. 30' : 'ör. 100'} />
      </label>
      <hr />
      <button type="button" className="rv-button-primary" disabled={!valid} onClick={apply}>Belirlenen Kriterlere Göre Seçimleri Yap</button>
      <button type="button" className="secondary is-danger" onClick={() => { onClearColumn(column); setOpen(false) }}>Yapılan Seçimleri Kaldır</button>
      </>}
      <hr />
      <button type="button" className="secondary" onClick={() => { onClearAll(); setColumn(''); setCriterion('rate'); setValue(''); setOpen(false) }}>Tüm Kolonlardan Seçimleri Kaldır</button>
    </div>}
  </div>
}

// Keeps a tariff's picks saved on the server: a change is sent 600 ms after the last one, one save at a time and the
// latest state winning; flush() sends what is waiting and resolves once the server holds it. Leaving the tariff sends
// what is still waiting.
function useSavedSelection(save: (body: string) => Promise<unknown>, serialized: string) {
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
    return next
  }, [save])
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
function TariffPlan(props: TariffPlanProps) {
  const selection = useQuery({
    queryKey: ['commission-tariff-selection', props.connectionId, props.tariff.id],
    queryFn: () => hubApi<TariffSelection>(`/connections/${props.connectionId}/commission-tariffs/${props.tariff.id}/selection`),
    gcTime: 0, refetchOnWindowFocus: false
  })
  // The 7-day view keeps the second period's picks too.
  const partnerSelection = useQuery({
    queryKey: ['commission-tariff-selection', props.connectionId, props.partner?.id],
    queryFn: () => hubApi<TariffSelection>(`/connections/${props.connectionId}/commission-tariffs/${props.partner!.id}/selection`),
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
function TariffPlanBody({ connectionId, tariff, partner, periods, rows, loading, error, onRetry, filter, onFilter, page, pageCount, onPage, onMessage, saved, partnerSaved }: TariffPlanProps & { saved: TariffSelection; partnerSaved?: TariffSelection }) {
  const [picks, setPicks] = useState<Map<string, TariffPick>>(() => selectionState(saved).picks)
  // The pick for every row, set in the Toplu Seçim popover.
  const [bulk, setBulk] = useState<TariffBulk>(() => selectionState(saved).bulk)
  const applied = bulk
  const save = useCallback((body: string) => hubApi<TariffSelection>(`/connections/${connectionId}/commission-tariffs/${tariff.id}/selection`, {
    method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-selection') }, body
  }), [connectionId, tariff.id])
  const saving = useSavedSelection(save, useMemo(() => JSON.stringify(selectionBody(picks, bulk)), [picks, bulk]))
  // The 7-day view: the second period's rows of the products on the page (by barcode; its rows have ids of their own)
  // and its picks, which every pick made here changes as it changes the first period's.
  const pageBarcodes = useMemo(() => [...new Set(rows?.items.map(row => row.barcode) ?? [])].sort(), [rows])
  const partnerRows = useQuery({
    queryKey: ['commission-tariff-rows', connectionId, partner?.id, 'barcodes', pageBarcodes],
    queryFn: () => hubApi<RowPage>(`/connections/${connectionId}/commission-tariffs/${partner!.id}/rows?${new URLSearchParams({ page: '0', limit: '200', barcodes: pageBarcodes.join(',') })}`),
    enabled: partner != null && pageBarcodes.length > 0
  })
  const partnerBy = useMemo(() => new Map((partnerRows.data?.items ?? []).map(row => [row.barcode, row])), [partnerRows.data])
  const [partnerPicks, setPartnerPicks] = useState<Map<string, TariffPick>>(() => selectionState(partnerSaved).picks)
  const [partnerBulk, setPartnerBulk] = useState<TariffBulk>(() => selectionState(partnerSaved).bulk)
  const partnerId = partner?.id
  const savePartner = useCallback((body: string) => hubApi<TariffSelection>(`/connections/${connectionId}/commission-tariffs/${partnerId}/selection`, {
    method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-selection') }, body
  }), [connectionId, partnerId])
  const partnerSaving = useSavedSelection(savePartner, useMemo(() => JSON.stringify(selectionBody(partnerPicks, partnerBulk)), [partnerPicks, partnerBulk]))
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
        const lefts = choices.flatMap(item => item.remaining === null ? [] : [item.remaining])
        const least = choices.filter(item => item.remaining !== null).sort((a, b) => a.remaining! - b.remaining!)[0]
        return <span key={period.id} className="commission-week-line">
          <em>{name} · {rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`}</em>
          <ProfitBadge price={least ?? choices[0]} label={lefts.length ? `${amountRange(lefts)} ${Math.max(...lefts) < 0 ? 'Zarar' : 'Kâr'}` : undefined}
            title={title} tag={`${tier}. Fiyat Aralığı · ${name}`} missing="Maliyet girilmemiş" />
        </span>
      })}
      <small className="commission-week-average">{weekTabLabel([tariff, partner]).split(' (')[0]} ortalaması: {known.length === owners.length && known.length ? `${amountRange(known)} ${Math.max(...known) < 0 ? 'Zarar' : 'Kâr'}` : 'hesaplanamadı'}</small>
    </>
  }
  // The models whose variants are shown: a model starts folded, its row alone, so a page of 50 models (each colour with
  // all its sizes and their band cards) does not draw thousands of cards at once.
  const [opened, setOpened] = useState<Set<string>>(() => new Set())
  const facets = useQuery({ queryKey: ['commission-tariff-facets', connectionId, tariff.id], queryFn: () => hubApi<{ categories: string[]; brands: string[] }>(`/connections/${connectionId}/commission-tariffs/${tariff.id}/facets`) })
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

  return <div className="commission-rows">
    <div className="commission-rows-head">
      <strong>{partner ? `${weekTabLabel([tariff, partner])} · ${periodName(tariff)} ve ${periodName(partner)} Bantları` : `${tariff.periodLabel?.trim() ? `${tariff.periodLabel.trim()} · ` : ''}${tariff.periodText} Bantları`}</strong>
    </div>
    {partner && <p className="commission-period-note">Bant sınırları iki dönemde aynı, komisyon oranları farklı. Her kartta iki dönemin komisyonu ve kârı ile gün sayısına göre ağırlıklı ({periodDays(tariff)}/{periodDays(tariff) + periodDays(partner)} ve {periodDays(partner)}/{periodDays(tariff) + periodDays(partner)}) ortalama kâr görünür. Burada yaptığınız seçim iki döneme birden kaydedilir; Excel İndir bu ürünlere “{periodDays(tariff) + periodDays(partner)} Günlük Fiyat” yazar. Toplu Seçim’in kâr koşulu her dönemde ayrı uygulanır; koşulu yalnız bir dönemde sağlayan ürüne o dönemin fiyatı yazılır.</p>}
    {manyPeriods && !partner && <p className="commission-period-note">Seçimler her dönem için ayrı kaydedilir. Excel İndir tüm dönemlerin seçimlerini Trendyol dosyasına birlikte yazar: iki dönemde aynı fiyatı seçtiğiniz ürüne “7 Günlük Fiyat”, tek dönemde seçtiğiniz ürüne o dönemin fiyatı yazılır. Bir ürüne iki dönemde farklı fiyat seçtiyseniz dosya ürün başına tek fiyat taşıdığı için Excel’i indirdiğiniz sekmenin ({openName}) fiyatı yazılır.</p>}
    {/* Which products the list shows. */}
    <TariffFilters key={JSON.stringify(filter)} value={filter} facets={facets.data} onApply={onFilter} />
    {/* The pick for every product: a band, or the best band by money or by share; with a condition on what it leaves. */}
    <div className="commission-plan-bar">
      <span className="commission-plan-count" role="status">{summary}{savingStatus === 'saving' ? <small> · Kaydediliyor…</small> : savingStatus === 'error' ? <small className="commission-warn"> · Seçimler kaydedilemedi</small> : null}</span>
      <BulkPicker bulk={bulk} onApply={applyBulk} onClearColumn={clearColumn} onClearAll={clearAll} />
      <button type="button" className="rv-button-primary commission-plan-download" disabled={(!bulkOn && ownPicks === 0 && !manyPeriods) || download.isPending} onClick={() => download.mutate()}>
        <UiIcon name="download" size={16} />{download.isPending ? 'Hazırlanıyor…' : 'Excel İndir'}
      </button>
    </div>
    {loading && <Busy />}
    {error !== null && <ErrorBox error={error} onRetry={onRetry} />}
    {rows && rows.items.length === 0 && <p className="cs-empty">Filtreye uyan ürün yok.</p>}
    {rows && rows.items.length > 0 && <div className="commission-table-wrap"><table className={`commission-table is-plan${partner ? ' is-week' : ''}`}>
      <thead><tr><th scope="col" className="commission-expand" aria-label="Bedenler" /><SortHeader label="Ürün" sort="name" filter={filter} onFilter={onFilter} /><th scope="col" className="commission-stock">Stok</th><SortHeader label="Güncel Fiyat" sort="price" filter={filter} onFilter={onFilter} />{tiers.map(tier => <SortHeader key={tier} label={`${tier}. Fiyat Aralığı`} sort={`tier${tier}` as TariffSort} filter={filter} onFilter={onFilter} />)}<th scope="col">Kendi Fiyatın</th></tr></thead>
      <tbody>{groupByModel(rows.items).map(([key, group]) => {
        // A variant's row; under its model it shows its size, codes and cost, the model's name and picture being above.
        const variantRow = (row: CommissionTariffRow, nested: boolean) => {
          const pick = picks.get(row.id)
          const chosen = pickedTier(row, pick, applied)
          // In the 7-day view, a product picked differently in the second period (on its own tab) says so.
          const other = partner ? partnerBy.get(row.barcode) : undefined
          const otherChosen = other ? pickedTier(other, partnerPicks.get(other.id), partnerBulk) : chosen
          const differs = partner && other && otherChosen !== chosen
            ? <small className="commission-warn">{periodName(partner)} seçimi farklı: {otherChosen === null ? 'seçilmedi' : otherChosen === 'own' ? 'kendi fiyatı' : `${otherChosen}. fiyat aralığı`}</small> : null
          return <tr key={row.id} className={`${nested ? 'is-variant' : ''}${chosen !== null ? ' is-picked' : ''}`.trim() || undefined}>
            <td className="commission-expand" />
            <th scope="row">{nested
              ? <span className="commission-variant-cell">
                <span className="commission-product">{row.size ?? row.productName ?? row.barcode}</span>
                <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
                {differs}
              </span>
              : <span className="commission-product-cell">
                {productImage(row)}
                <span>
                  <span className="commission-product">{row.productName ?? row.barcode}{row.size ? ` · ${row.size}` : ''}</span>
                  <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                  <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                  {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
                  {differs}
                </span>
              </span>}</th>
            <td className="commission-stock">{stockText(row.stock)}</td>
            <td>
              <span className="commission-price">{row.basisPrice === null ? '—' : money(row.basisPrice)}</span>
              {rateDisagrees(row) && <small className="commission-warn" title="Pazaryerinin dosyadaki güncel komisyonu, fiyatın bandından farklı.">Dosyada {rateText(row.currentRate!)}</small>}
            </td>
            {tiers.map(tier => {
              const choice = row.choices?.find(item => item.tier === tier)
              if (!choice) return <td key={tier} className="commission-choice-none">—</td>
              const band = row.bands.find(item => item.tier === tier)
              const picked = chosen === tier
              return <td key={tier}>
                <div className={`commission-choice${picked ? ' is-picked' : ''}${choice.remaining === null ? ' is-unknown' : choice.remaining < 0 ? ' is-loss' : ''}`}>
                  {/* The card's headline is its band limit; the price the row would get stays in the badge's breakdown. */}
                  <strong title={bandLimitHint([band])}>{bandLimitText([band]) ?? money(choice.price)}{row.currentTier === tier && <small className="commission-now"> · şu an</small>}</strong>
                  {partner ? weekLines([row], tier, `${row.productName ?? row.barcode}${row.size ? ` · ${row.size}` : ''}`) : <>
                    <span>{rateText(choice.rate)} komisyon · {money(choice.commission)}</span>
                    <ProfitBadge price={choice} title={`${row.productName ?? row.barcode}${row.size ? ` · ${row.size}` : ''}`} tag={`${tier}. Fiyat Aralığı`} missing={row.costPrice ? 'Kâr hesaplanamadı' : 'Maliyet girilmemiş'} />
                  </>}
                  <button type="button" className="commission-switch" aria-pressed={picked} onClick={() => toggle(row, tier, picked)}><span />{picked ? 'Seçili' : 'Seç'}</button>
                </div>
              </td>
            })}
            <td><OwnPrice row={row} pick={pick} picked={chosen === 'own'} onPick={price => setRowPicks([row], price === null ? null : { tier: null, price })} /></td>
          </tr>
        }
        if (group.length === 1) return variantRow(group[0], false)
        const first = group.find(row => row.imageUrl) ?? group[0]
        const open = opened.has(key)
        const prices = group.flatMap(row => row.basisPrice === null ? [] : [row.basisPrice])
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
                <small className="commission-model-count">{group.length.toLocaleString('tr-TR')} {first.color ? 'beden' : 'varyant'}</small>
              </span>
            </span></th>
            <td className="commission-stock">{stockText(group.some(row => row.stock != null) ? group.reduce((sum, row) => sum + (row.stock ?? 0), 0) : null)}</td>
            <td><span className="commission-price">{prices.length ? priceRange(prices) : '—'}</span></td>
            {tiers.map(tier => {
              const choices = group.flatMap(row => row.choices?.filter(item => item.tier === tier) ?? [])
              if (!choices.length) return <td key={tier} className="commission-choice-none">—</td>
              const owners = group.filter(row => row.choices?.some(item => item.tier === tier))
              const all = owners.every(row => pickedTier(row, picks.get(row.id), applied) === tier)
              const rates = [...new Set(choices.map(item => item.rate))]
              const lefts = choices.flatMap(item => item.remaining === null ? [] : [item.remaining])
              // The variant leaving the least is the one the badge opens.
              const least = choices.filter(item => item.remaining !== null).sort((a, b) => a.remaining! - b.remaining!)[0]
              const leastRow = least && group.find(row => row.choices?.includes(least))
              const ranged = lefts.length > 0 && Math.min(...lefts) !== Math.max(...lefts)
              return <td key={tier}>
                <div className={`commission-choice is-model${all ? ' is-picked' : ''}${!lefts.length ? ' is-unknown' : Math.min(...lefts) < 0 ? ' is-loss' : ''}`}>
                  <strong title={bandLimitHint(owners.map(row => row.bands.find(item => item.tier === tier)))}>{bandLimitText(owners.map(row => row.bands.find(item => item.tier === tier))) ?? priceRange(choices.map(item => item.price))}{owners.some(row => row.currentTier === tier) && <small className="commission-now"> · şu an</small>}</strong>
                  {partner ? weekLines(owners, tier, first.productName ?? first.barcode) : <>
                    <span>{rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`} komisyon</span>
                    <ProfitBadge price={least ?? choices[0]} label={ranged ? `${amountRange(lefts)} Kâr` : undefined}
                      title={first.productName ?? first.barcode} tag={`${tier}. Fiyat Aralığı`}
                      aside={ranged ? `En az kâr bırakan beden: ${leastRow?.size ?? leastRow?.barcode ?? '—'}` : undefined} missing="Maliyet girilmemiş" />
                  </>}
                  {lefts.length > 0 && lefts.length < choices.length && <small>{(choices.length - lefts.length).toLocaleString('tr-TR')} varyantın kârı hesaplanamadı</small>}
                  <button type="button" className="commission-switch" aria-pressed={all} onClick={() => pickModel(owners, tier, all)}><span />{all ? 'Tümü Seçili' : 'Tüm Varyantlarını Seç'}</button>
                </div>
              </td>
            })}
            <td><ModelOwnPrice rows={group} picks={picks} onPick={price => setRowPicks(group, price === null ? null : { tier: null, price })} /></td>
          </tr>
          {open && group.map(row => variantRow(row, true))}
        </Fragment>
      })}</tbody>
    </table></div>}
    {rows && pageCount > 1 && <div className="commission-pager"><button type="button" className="secondary" disabled={page === 0} onClick={() => onPage(page - 1)}>Önceki</button><span>{(page + 1).toLocaleString('tr-TR')} / {pageCount.toLocaleString('tr-TR')}</span><button type="button" className="secondary" disabled={page + 1 >= pageCount} onClick={() => onPage(page + 1)}>Sonraki</button></div>}
  </div>
}

// A price of the seller's own for all of a model's sizes at once: each size takes it as its own pick (and its own field
// shows it when opened); what it leaves is the range over the sizes, and the sizes it puts in no band are counted.
function ModelOwnPrice({ rows, picks, onPick }: { rows: CommissionTariffRow[]; picks: Map<string, TariffPick>; onPick: (price: number | null) => void }) {
  const shared = rows.map(row => picks.get(row.id)?.price ?? null)
  const [text, setText] = useState(() => shared[0] !== null && shared.every(price => price === shared[0]) ? String(shared[0]).replace('.', ',') : '')
  const parse = (value: string) => { const number = Number(value.trim().replace(/\.(?=\d{3}(\D|$))/g, '').replace(',', '.')); return value.trim() && Number.isFinite(number) && number > 0 ? number : null }
  const price = parse(text)
  const choices = price === null ? [] : rows.map(row => ownChoice(row, price))
  const banded = choices.filter((choice): choice is CommissionChoice => choice !== null)
  const tiersHit = [...new Set(banded.map(choice => choice.tier))].sort()
  const lefts = banded.flatMap(choice => choice.remaining === null ? [] : [choice.remaining])
  return <div className={`commission-own${price !== null && shared.every(value => value === price) ? ' is-picked' : ''}`}>
    <input type="text" inputMode="decimal" placeholder="Tüm bedenlere fiyat" value={text} aria-label={`${rows[0]?.productName ?? 'Model'} için tüm bedenlere kendi fiyatın`}
      onChange={event => { setText(event.target.value); onPick(parse(event.target.value)) }} />
    {text.trim() !== '' && (banded.length ? <small>{tiersHit.map(tier => `${tier}.`).join(', ')} fiyat aralığı · {rows.length} beden<b>{lefts.length ? `${amountRange(lefts)} Kâr` : 'Maliyet girilmemiş'}</b>{banded.length < rows.length && <span className="commission-warn">{rows.length - banded.length} beden hiçbir fiyat aralığına düşmüyor</span>}</small> : <small className="commission-warn">Hiçbir fiyat aralığına düşmüyor</small>)}
  </div>
}

// A price of the seller's own for one product: typed with a comma or a point, it shows the band it falls in and what
// it leaves, and becomes the row's pick.
function OwnPrice({ row, pick, picked, onPick }: { row: CommissionTariffRow; pick: TariffPick | undefined; picked: boolean; onPick: (price: number | null) => void }) {
  const [text, setText] = useState(() => pick?.price ? String(pick.price).replace('.', ',') : '')
  const parse = (value: string) => { const number = Number(value.trim().replace(/\.(?=\d{3}(\D|$))/g, '').replace(',', '.')); return value.trim() && Number.isFinite(number) && number > 0 ? number : null }
  const price = parse(text)
  const choice = price === null ? null : ownChoice(row, price)
  return <div className={`commission-own${picked ? ' is-picked' : ''}`}>
    <input type="text" inputMode="decimal" placeholder="Fiyat girin" value={text} aria-label={`${row.productName ?? row.barcode} için kendi fiyatın`}
      onChange={event => { setText(event.target.value); onPick(parse(event.target.value)) }} />
    {text.trim() !== '' && (choice ? <small>{choice.tier}. fiyat aralığı · {rateText(choice.rate)}<ProfitBadge price={choice} title={row.productName ?? row.barcode} tag="Kendi Fiyatın" missing={row.costPrice ? 'Kâr hesaplanamadı' : 'Maliyet girilmemiş'} /></small> : <small className="commission-warn">Hiçbir fiyat aralığına düşmüyor</small>)}
  </div>
}
