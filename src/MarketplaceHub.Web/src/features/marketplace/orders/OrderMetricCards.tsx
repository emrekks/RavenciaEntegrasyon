// The six summary cards above the order list (three by two; two by three on a phone); every count reads "N Paket".
// "Yeni Sipariş" reads "N Paket | M Adet Ürün": the orders of the Yeni tab under the list's filters and their products
// (each line's ordered quantity less what was cancelled of it); hovering lists the products per store.
// "İşleme Alınan" is the same set as the "İşleme Alınanlar" tab (taken into processing, not yet handed to the carrier)
// and reads and hovers the same way as Yeni Sipariş.
// "Geciken" turns red and pulses softly while there is any. "Mikro İhracat Sipariş" counts today's micro-export
// orders not yet handed to the carrier (placed on the Istanbul day, whatever the list's filters are: the orders the row's flag icon marks); hovering
// lists their countries. "İptal" shows today (the
// Istanbul day): "N Paket" for the orders cancelled whole as the Genel Bakış card counts them, and while
// orders were cancelled in part a chip "N Kısmi İptal | M Adet" (the units cancelled of them) that pulses like Geciken.
// "Yeniden Gönderim" counts the resends that have not been handed to the carrier yet.
// Geciken, İptal and Yeniden Gönderim list their stores on hover while they count any.
// The cancellation, micro-export and resend cards open the list on what they counted; the card whose list is open shows
// as selected and tapping it again closes the list (back to Tümü).
import type { ReactNode } from 'react'
import { CardBackgroundIcon, CountUp, type CardIconName, type CardTone } from '../../../shared/components'
import { RrLogo } from '../returns-ui'
import { countryName, Flag } from './order-row-marks'
import type { DayCard } from './daily-cards'
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }
type Store = { key: string; code: string; name: string }
type UnitRow = Store & { units: number }
type PackageRow = Store & { packages: number }
type CancelRow = Store & { packages: number; units?: number; partial: number; partialUnits?: number }
type TipRow = Store & { value: ReactNode }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

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
function Packages({ packages, units }: { packages: Metric; units: number | undefined }) {
  return <strong className="rr-metric-pk">
    <CountUp value={metricText(packages)} />
    {!packages.loading && ' '}
    {!packages.loading && <span className="rr-metric-sub">Paket{units !== undefined && <>{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}{count(units)} <span className="rr-metric-adet">Adet </span>Ürün</>}</span>}
  </strong>
}

// The panel a card opens on hover or focus: one row per store, its logo, name and figure.
function StoreTip({ rows }: { rows: TipRow[] }) {
  // An empty card opens no list (owner, 5 Nov 2026: "boşsa popup çıkmasın").
  if (rows.length === 0) return null
  return <div className="rr-metric-tip" role="tooltip">{rows.map(row => <div className="rr-metric-tip-r" key={row.key}><RrLogo code={row.code} /><span>{row.name}</span><b className="rr-num">{row.value}</b></div>)}</div>
}

const tipped = (...names: Array<string | false | undefined>) => names.filter(Boolean).join(' ') || undefined

// A card that reads "N Paket | M Adet Ürün" and lists the products per store on hover or focus (no panel without them).
function UnitsCard({ title, icon, tone, packages, units, rows }: { title: string; icon: CardIconName; tone?: CardTone; packages: Metric; units: number | undefined; rows: UnitRow[] | undefined }) {
  const stores = packages.loading ? [] : rows ?? []
  return <article className={tipped(tone && `rr-${tone}`, stores.length > 0 && 'rr-metric-tipped')} tabIndex={stores.length > 0 ? 0 : undefined}>
    <CardBackgroundIcon name={icon} tone={tone} />
    <small>{title}</small><Packages packages={packages} units={units} />
    {stores.length > 0 && <StoreTip rows={stores.map(row => ({ ...row, value: `${count(row.units)} adet` }))} />}
  </article>
}

const packageTip = (rows: PackageRow[]) => rows.map(row => ({ ...row, value: `${count(row.packages)} paket` }))

// Touch screens: a tap on a card shows its stores (the shared tap handler opens the breakdown) and never switches the
// list's tab; a computer opens the card's list as before.

export function OrderMetricCards({ newOrders, newUnits, newRows, processing, processingUnits, processingRows, late, lateRows, microExport, microCountries, cancelled, cancelledRows, partiallyCancelled, partialUnits, resent, resentRows, onOpenDay, activeCard }: { newOrders: Metric; newUnits?: number; newRows?: UnitRow[]; processing: Metric; processingUnits?: number; processingRows?: UnitRow[]; late: Metric; lateRows?: PackageRow[]; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; cancelledUnits?: number; cancelledRows?: CancelRow[]; partiallyCancelled: Metric; partialUnits?: number; resent: Metric; resentRows?: PackageRow[]; onOpenDay: (card: DayCard) => void; activeCard?: DayCard }) {
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
  const resentStores = stores(resent, resentRows)
  return <div className="rr-metrics rr-metrics-6">
    <UnitsCard title="Yeni Sipariş" icon="orders" tone="warn" packages={newOrders} units={newUnits} rows={newRows} />
    <UnitsCard title="İşleme Alınan" icon="packagePen" tone="warn" packages={processing} units={processingUnits} rows={processingRows} />
    {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- focusable so a keyboard opens its store list */}
    <article className={tipped(positive(late) && 'rr-bad rr-alert', 'rr-metric-tipped')} tabIndex={0}>
      <CardBackgroundIcon name="clock" tone={positive(late) ? 'bad' : undefined} />
      <small>Geciken</small><Packages packages={late} units={undefined} />
      {<StoreTip rows={packageTip(lateStores)} />}
    </article>
    <article className={tipped('rr-metric-link rr-metric-tipped', activeCard === 'micro' && 'rr-metric-on')}>
      <CardBackgroundIcon name="globe" />
      <button type="button" className="rr-metric-open" title={countries.length > 0 ? undefined : 'Bugün gelen, kargoya verilmemiş mikro ihracat siparişleri'} aria-pressed={activeCard === 'micro'} onClick={() => openOnPointer('micro')}><small>Mikro İhracat Sipariş</small><Packages packages={microExport} units={undefined} /></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip">{countries.map(([code, value]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count(value)}</b></div>)}</div>}
    </article>
    <article className={tipped(positive(cancelled) && 'rr-bad', 'rr-metric-link', partial > 0 && 'rr-metric-chipped', 'rr-metric-tipped', (activeCard === 'cancelled' || activeCard === 'partial') && 'rr-metric-on')}>
      <CardBackgroundIcon name="packageX" tone={positive(cancelled) ? 'bad' : undefined} />
      <button type="button" className="rr-metric-open" title={cancelStores.length > 0 ? undefined : 'Bugün iptal edilen siparişler'} aria-pressed={activeCard === 'cancelled'} onClick={() => openOnPointer('cancelled')}><small>İptal</small><Packages packages={cancelled} units={undefined} /></button>
      {partial > 0 && <button type="button" className={tipped('rr-metric-chip rr-metric-chip-alert', activeCard === 'partial' && 'rr-metric-on')} aria-pressed={activeCard === 'partial'} title={cancelStores.length > 0 ? undefined : `Bugün kısmen iptal edilen ${count(partial)} sipariş`} onClick={() => openOnPointer('partial')}>{count(partial)} Kısmi İptal{partialUnits !== undefined && <>{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}{count(partialUnits)} Adet</>}</button>}
      {<StoreTip rows={cancelStores.map(row => ({ ...row, value: cancelRowText(row) }))} />}
    </article>
    <article className={tipped('rr-metric-link', 'rr-metric-tipped', activeCard === 'resent' && 'rr-metric-on')}>
      <CardBackgroundIcon name="refresh" />
      <button type="button" className="rr-metric-open" title={resentStores.length > 0 ? undefined : 'Kargoya verilmemiş yeniden gönderimler'} aria-pressed={activeCard === 'resent'} onClick={() => openOnPointer('resent')}><small>Yeniden Gönderim</small><Packages packages={resent} units={undefined} /></button>
      {<StoreTip rows={packageTip(resentStores)} />}
    </article>
  </div>
}
