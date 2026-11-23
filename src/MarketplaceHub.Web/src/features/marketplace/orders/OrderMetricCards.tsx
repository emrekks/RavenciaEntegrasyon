// The six summary cards above the order list (three by two; two by three on a phone); every count reads "N Paket".
// "Yeni Sipariş" reads "N Paket | M Adet Ürün": the orders of the Yeni tab under the list's filters and their products
// (each line's ordered quantity less what was cancelled of it); hovering lists the products per store.
// "İşleme Alınan" is the same set as the "İşleme Alınanlar" tab (taken into processing, not yet handed to the carrier)
// and reads and hovers the same way as Yeni Sipariş.
// "Geciken" turns red and pulses softly while there is any. "Mikro İhracat Sipariş" counts today's micro-export
// orders not yet handed to the carrier (placed on the Istanbul day, whatever the list's filters are: the orders the row's flag icon marks); hovering
// lists their countries. "İptal" shows today (the
// Istanbul day): "N Paket" for every order cancelled today, whole or in part, as the Genel Bakış card counts them, and while
// orders were cancelled in part a chip "N Kısmi İptal | M Adet" (the units cancelled of them) that pulses like Geciken.
// "Yeniden Gönderim" counts the resends that have not been handed to the carrier yet.
// Trendyol never returns resend packages, so a gear in the card's corner opens the window where the owner adds a
// re-shipment by marketplace and order number (owner, 23 Nov 2026); it is then one of the rows the card counts. A count
// typed with the old gear shows beside the figure as "Eski Hatırlatma: N" until cleared and is not added to it.
// Geciken, İptal and Yeniden Gönderim list their stores on hover while they count any.
// The cancellation, micro-export and resend cards open the list on what they counted; the card whose list is open shows
// as selected and tapping it again closes the list (back to Tümü).
import type { ReactNode } from 'react'
import { CardBackgroundIcon, CountUp, UiIcon, type CardIconName, type CardTone } from '../../../shared/components'
import { RrLogo } from '../../../shared/ui/rr-components'
import { countryName, Flag } from './order-row-marks'
import type { DayCard } from './daily-cards'
import { type OrderCargoCount } from './order-summary'
import { cargoLabel } from '../../../shared/components/CargoProviderIcon'
import { cardCountText, toneAbove } from '../../../shared/metric-value'
import './order-metrics.css'

// error: the count could not be read; shown as "—", never as 0 or an endless "…".
type Metric = { value: number | undefined; loading: boolean; error?: boolean }
type Store = { key: string; code: string; name: string }
type UnitRow = Store & { units: number; packages?: number }
type PackageRow = Store & { packages: number; exchanges?: number }
type CancelRow = Store & { packages: number; units?: number; partial: number; partialUnits?: number }
type TipRow = Store & { value: ReactNode }

function metricText({ value, loading, error }: Metric) {
  return cardCountText(value, { loading, error: Boolean(error) })
}
// A count that is known: neither loading, nor failed, nor missing (those read "…" or "—" with no "Paket" after them). A real zero reads "0 Paket".
const known = (metric: Metric) => !metric.loading && !metric.error && metric.value !== undefined

const count = (value: number) => value.toLocaleString('tr-TR')

// The countries of the micro-export orders, the biggest first; orders of no known country come last.
export function microCountryRows(countries: Record<string, number> | undefined) {
  return Object.entries(countries ?? {}).filter(([, count]) => count > 0)
    .sort(([leftCode, left], [rightCode, right]) => (leftCode === '' ? 1 : 0) - (rightCode === '' ? 1 : 0) || right - left || leftCode.localeCompare(rightCode))
}

// An İptal store row: "N paket · M adet" for the orders cancelled whole and, while some were cancelled in part,
// "K kısmi · L adet"; units are left out while unknown (an older API).
export function cancelRowText(row: Omit<CancelRow, keyof Store>) {
  const part = (packages: number, units: number | undefined, label: string) => `${count(packages)} ${label}${units !== undefined ? ` · ${count(units)} adet` : ''}`
  return [row.packages > 0 ? part(row.packages, row.units, 'paket') : undefined, row.partial > 0 ? part(row.partial, row.partialUnits, 'kısmi') : undefined].filter(Boolean).join(', ')
}

// "N Paket | M Adet Ürün": the package count big, the rest smaller on the same line. The units are left out while they
// are not known (an older API), never shown as 0.
function Packages({ packages, units, reminder }: { packages: Metric; units: number | undefined; reminder?: number }) {
  return <strong className="rr-metric-pk" title={packages.error ? 'Sayı okunamadı; sayfayı yenileyip tekrar deneyin.' : undefined}>
    <CountUp value={metricText(packages)} />
    {known(packages) && ' '}
    {known(packages) && <span className="rr-metric-sub">Paket{units !== undefined && <>{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}{count(units)} <span className="rr-metric-adet">Adet </span>Ürün</>}{reminder !== undefined && reminder > 0 && <span className="rr-metric-note">{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}Eski Hatırlatma: {count(reminder)}</span>}</span>}
  </strong>
}

// The panel a card opens on hover or focus: one row per store, its logo, name and figure.
function StoreTip({ rows, cargo = [] }: { rows: TipRow[]; cargo?: OrderCargoCount[] }) {
  // An empty card opens no list (owner, 5 Nov 2026: "boşsa popup çıkmasın").
  if (rows.length === 0) return null
  // Below the stores, after a line, the cargo companies in words (owner, 5 Nov 2026: "logo olarak değil yazıyla").
  return <div className="rr-metric-tip" role="tooltip">{rows.map(row => <div className="rr-metric-tip-r" key={row.key}><RrLogo code={row.code} /><span>{row.name}</span><b className="rr-num">{row.value}</b></div>)}
    {cargo.length > 0 && <div className="rr-metric-tip-sep" aria-hidden="true" />}
    {cargo.map(row => <div className="rr-metric-tip-r rr-metric-tip-cargo" key={`cargo:${row.cargo}`}><span>{row.cargo ? cargoLabel(row.cargo) : "Kargo Belirlenmedi"}</span><b className="rr-num">{count(row.orders)} paket</b></div>)}</div>
}

// The gear in the Yeniden Gönderim card's corner (owner, 23 Nov 2026): it opens the window where the owner adds a
// re-shipment by marketplace and order number (ManualResendDialog); it no longer keeps a typed count.
export function ResendRecordsGear({ onOpen }: { onOpen: () => void }) {
  return <div className="rr-reminder">
    <button type="button" className="rr-reminder-gear" aria-label="Yeniden Gönderim Ekle" title="Yeniden Gönderim Ekle" aria-haspopup="dialog" onClick={event => { event.stopPropagation(); onOpen() }}><UiIcon name="settings" size={16} /></button>
  </div>
}

const tipped = (...names: Array<string | false | undefined>) => names.filter(Boolean).join(' ') || undefined

// A card that reads "N Paket | M Adet Ürün" and lists the products per store on hover or focus (no panel without them).
function UnitsCard({ title, icon, tone, packages, units, rows, cargo }: { title: string; icon: CardIconName; tone?: CardTone; packages: Metric; units: number | undefined; rows: UnitRow[] | undefined; cargo?: OrderCargoCount[] | null }) {
  const stores = known(packages) ? rows ?? [] : []
  // The tone marks a count above nought only; zero stays in the neutral ink.
  const shownTone = toneAbove(packages.value, tone ?? 'warn') && tone
  return <article className={tipped(shownTone && `rr-${shownTone}`, stores.length > 0 && 'rr-metric-tipped')} tabIndex={stores.length > 0 ? 0 : undefined}>
    <CardBackgroundIcon name={icon} tone={shownTone} />
    <small>{title}</small><Packages packages={packages} units={units} />
    {stores.length > 0 && <StoreTip rows={stores.map(row => ({ ...row, value: row.packages !== undefined ? `${count(row.packages)} paket · ${count(row.units)} adet` : `${count(row.units)} adet` }))} cargo={(cargo ?? []).filter(row => row.orders > 0)} />}
  </article>
}

const packageTip = (rows: PackageRow[]) => rows.map(row => ({ ...row, value: `${count(row.packages)} paket` }))

// A Yeniden Gönderim store row says what it counts (owner, 23 Nov 2026): "1 paket · değişim, kargoya verilmesi bekleniyor"
// when all are exchanges (Trendyol names their package but no tracking link), "3 paket · 1 değişim" when some are.
export function resentTipText(row: { packages: number; exchanges?: number }) {
  const exchanges = row.exchanges ?? 0
  if (exchanges <= 0) return `${count(row.packages)} paket`
  return exchanges >= row.packages ? `${count(row.packages)} paket · değişim, kargoya verilmesi bekleniyor` : `${count(row.packages)} paket · ${count(exchanges)} değişim`
}

// Touch screens: a tap on a card shows its stores (the shared tap handler opens the breakdown) and never switches the
// list's tab; a computer opens the card's list as before.

export function OrderMetricCards({ newOrders, newUnits, newRows, newCargo, processing, processingUnits, processingRows, processingCargo, late, lateRows, microExport, microCountries, cancelled, cancelledRows, partiallyCancelled, partialUnits, resent, resentRows, resentReminder = 0, onOpenResentRecords, onOpenDay, activeCard }: { newOrders: Metric; newUnits?: number; newRows?: UnitRow[]; newCargo?: OrderCargoCount[] | null; processing: Metric; processingUnits?: number; processingRows?: UnitRow[]; processingCargo?: OrderCargoCount[] | null; late: Metric; lateRows?: PackageRow[]; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; cancelledUnits?: number; cancelledRows?: CancelRow[]; partiallyCancelled: Metric; partialUnits?: number; resent: Metric; resentRows?: PackageRow[]; resentReminder?: number; onOpenResentRecords?: () => void; onOpenDay: (card: DayCard) => void; activeCard?: DayCard }) {
  // The cards no longer switch the list to their orders (owner, 5 Nov 2026): a click shows the stores, as Yeni Sipariş does.
  const openOnPointer = (_card: DayCard) => { void _card; void onOpenDay }
  // The partly cancelled orders are a chip beside the cancelled count only while there are any.
  const partial = partiallyCancelled.loading ? 0 : partiallyCancelled.value ?? 0
  // A warning card is plain at 0 and warns only above it: Geciken with a softly pulsing red box, İptal with its number.
  const positive = (metric: Metric) => !metric.loading && (metric.value ?? 0) > 0
  const countries = microCountryRows(microCountries)
  // The stores of a card, only while it counts any.
  const stores = <T,>(metric: Metric, rows: T[] | undefined, always = false) => (always || positive(metric)) && !metric.loading ? rows ?? [] : []
  const lateStores = stores(late, lateRows)
  const cancelStores = stores(cancelled, cancelledRows, partial > 0)
  // The card counts the rows Yeniden Gönderimler lists as not handed to the carrier, hand-added ones included (one rule on
  // the server); an old typed count is only named beside it ("Eski Hatırlatma: N").
  const resentStores = stores(resent, resentRows)
  // A card that counts a known zero has nothing to open: no pointer, no highlight, the button disabled. A card that is
  // the open list stays enabled so the owner can still turn its filter off.
  const idle = (metric: Metric, on: boolean) => known(metric) && metric.value === 0 && !on
  const microIdle = idle(microExport, activeCard === 'micro')
  const cancelIdle = idle(cancelled, activeCard === 'cancelled' || activeCard === 'partial') && partial === 0
  const resentIdle = idle(resent, activeCard === 'resent')
  return <div className="rr-metrics rr-metrics-6">
    <UnitsCard title="Yeni Sipariş" icon="orders" tone="warn" packages={newOrders} units={newUnits} rows={newRows} cargo={newCargo} />
    <UnitsCard title="İşleme Alınan" icon="packagePen" tone="warn" packages={processing} units={processingUnits} rows={processingRows} cargo={processingCargo} />
    {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- focusable so a keyboard opens its store list */}
    <article className={tipped(positive(late) && 'rr-bad rr-alert', 'rr-metric-tipped')} tabIndex={0}>
      <CardBackgroundIcon name="clock" tone={positive(late) ? 'bad' : undefined} />
      <small>Geciken</small><Packages packages={late} units={undefined} />
      {<StoreTip rows={packageTip(lateStores)} />}
    </article>
    <article className={tipped('rr-metric-link rr-metric-tipped', microIdle && 'rr-metric-idle', activeCard === 'micro' && 'rr-metric-on')}>
      <CardBackgroundIcon name="globe" />
      <button type="button" className="rr-metric-open" title={countries.length > 0 ? undefined : 'Kargoya verilmemiş mikro ihracat siparişleri'} aria-pressed={activeCard === 'micro'} disabled={microIdle} onClick={() => openOnPointer('micro')}><small>Mikro İhracat Sipariş</small><Packages packages={microExport} units={undefined} /></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip">{countries.map(([code, value]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count(value)}</b></div>)}</div>}
    </article>
    <article className={tipped(positive(cancelled) && 'rr-bad', 'rr-metric-link', partial > 0 && 'rr-metric-chipped', 'rr-metric-tipped', cancelIdle && 'rr-metric-idle', (activeCard === 'cancelled' || activeCard === 'partial') && 'rr-metric-on')}>
      <CardBackgroundIcon name="packageX" tone={positive(cancelled) ? 'bad' : undefined} />
      <button type="button" className="rr-metric-open" title={cancelStores.length > 0 ? undefined : 'Bugün iptal edilen siparişler'} aria-pressed={activeCard === 'cancelled'} disabled={cancelIdle} onClick={() => openOnPointer('cancelled')}><small>İptal</small><Packages packages={cancelled} units={undefined} /></button>
      {partial > 0 && <button type="button" className={tipped('rr-metric-chip rr-metric-chip-alert', activeCard === 'partial' && 'rr-metric-on')} aria-pressed={activeCard === 'partial'} title={cancelStores.length > 0 ? (partialUnits !== undefined ? `${count(partialUnits)} adet ürün` : undefined) : `Bugün kısmen iptal edilen ${count(partial)} sipariş${partialUnits !== undefined ? ` · ${count(partialUnits)} adet ürün` : ''}`} onClick={() => openOnPointer('partial')}><b>{count(partial)}</b> Kısmi İptal{partialUnits !== undefined && <span className="rr-metric-adet">{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}<b>{count(partialUnits)}</b> Adet Ürün</span>}</button>}
      {<StoreTip rows={cancelStores.map(row => ({ ...row, value: cancelRowText(row) }))} />}
    </article>
    <article className={tipped(positive(resent) && 'rr-warn', 'rr-metric-link', 'rr-metric-tipped', resentIdle && 'rr-metric-idle', activeCard === 'resent' && 'rr-metric-on')}>
      <CardBackgroundIcon name="refresh" />
      <button type="button" className="rr-metric-open" title={resentStores.length > 0 ? undefined : 'Kargoya verilmemiş yeniden gönderimler; sağ üstteki ayar simgesiyle sipariş numarasıyla elle eklenir'} aria-pressed={activeCard === 'resent'} disabled={resentIdle} onClick={() => openOnPointer('resent')}><small>Yeniden Gönderim</small><Packages packages={resent} units={undefined} reminder={resentReminder} /></button>
      {onOpenResentRecords && <ResendRecordsGear onOpen={onOpenResentRecords} />}
      {<StoreTip rows={resentStores.map(row => ({ ...row, value: resentTipText(row) }))} />}
    </article>
  </div>
}
