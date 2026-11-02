// The six summary cards above the order list (three by two; two by three on a phone).
// "Yeni Sipariş" reads "N Paket | M Adet Ürün": the orders of the Yeni tab under the list's filters and their products
// (each line's ordered quantity less what was cancelled of it); hovering lists the products per store.
// "İşleme Alınan" is the same set as the "İşleme Alınanlar" tab: taken into processing, not yet handed to the carrier.
// "Geciken" turns red and pulses softly while there is any. "Mikro İhracat Sipariş" counts the pending micro-export
// orders, whatever the list's filters are: the orders the row's flag icon marks that are not yet handed to the carrier
// (New and İşleme Alınan, the overview's Bekleyen set, any day); hovering lists their countries. "İptal" shows today (the
// Istanbul day): "N Paket" for the orders cancelled whole as the Genel Bakış card counts them, and while
// orders were cancelled in part a chip "N Kısmi İptal | M Adet" (the units cancelled of them) that pulses like Geciken.
// "Yeniden Gönderim" counts the resends that have not been handed to the carrier yet.
// The cancellation, micro-export and resend cards open the list on what they counted.
import { CountUp } from '../../../shared/components'
import { RrLogo } from '../returns-ui'
import { countryName, Flag } from './order-row-marks'
import type { DayCard } from './daily-cards'
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }
type UnitRow = { key: string; code: string; name: string; units: number }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

const count = (value: number) => value.toLocaleString('tr-TR')

// The countries of the micro-export orders, the biggest first; orders of no known country come last.
export function microCountryRows(countries: Record<string, number> | undefined) {
  return Object.entries(countries ?? {}).filter(([, count]) => count > 0)
    .sort(([leftCode, left], [rightCode, right]) => (leftCode === '' ? 1 : 0) - (rightCode === '' ? 1 : 0) || right - left || leftCode.localeCompare(rightCode))
}

// "N Paket | M Adet Ürün": the package count big, the rest smaller on the same line. The units are left out while they
// are not known (an older API), never shown as 0.
function Packages({ packages, units }: { packages: Metric; units: number | undefined }) {
  return <strong className="rr-metric-pk">
    <CountUp value={metricText(packages)} />
    {!packages.loading && ' '}
    {!packages.loading && <span className="rr-metric-sub">Paket{units !== undefined && <>{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}{count(units)} Adet Ürün</>}</span>}
  </strong>
}

export function OrderMetricCards({ newOrders, newUnits, newRows, processing, late, microExport, microCountries, cancelled, partiallyCancelled, partialUnits, resent, onOpenDay }: { newOrders: Metric; newUnits?: number; newRows?: UnitRow[]; processing: Metric; late: Metric; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; cancelledUnits?: number; partiallyCancelled: Metric; partialUnits?: number; resent: Metric; onOpenDay: (card: DayCard) => void }) {
  // The partly cancelled orders are a chip beside the cancelled count only while there are any.
  const partial = partiallyCancelled.loading ? 0 : partiallyCancelled.value ?? 0
  // A warning card is plain at 0 and warns only above it: Geciken with a softly pulsing red box, İptal with its number.
  const positive = (metric: Metric) => !metric.loading && (metric.value ?? 0) > 0
  const countries = microCountryRows(microCountries)
  const stores = newOrders.loading ? [] : newRows ?? []
  return <div className="rr-metrics rr-metrics-6">
    <article className={`rr-warn${stores.length > 0 ? ' rr-metric-tipped' : ''}`} tabIndex={stores.length > 0 ? 0 : undefined}>
      <small>Yeni Sipariş</small><Packages packages={newOrders} units={newUnits} />
      {stores.length > 0 && <div className="rr-metric-tip" role="tooltip">{stores.map(row => <div className="rr-metric-tip-r" key={row.key}><RrLogo code={row.code} /><span>{row.name}</span><b className="rr-num">{count(row.units)} adet</b></div>)}</div>}
    </article>
    <article><small>İşleme Alınan</small><strong><CountUp value={metricText(processing)} /></strong></article>
    <article className={positive(late) ? 'rr-bad rr-alert' : undefined}><small>Geciken</small><strong><CountUp value={metricText(late)} /></strong></article>
    <article className="rr-metric-link rr-metric-tipped">
      <button type="button" className="rr-metric-open" title={countries.length > 0 ? undefined : 'Kargoya verilmemiş mikro ihracat siparişleri'} onClick={() => onOpenDay('micro')}><small>Mikro İhracat Sipariş</small><strong><CountUp value={metricText(microExport)} /></strong></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip">{countries.map(([code, value]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count(value)}</b></div>)}</div>}
    </article>
    <article className={`${positive(cancelled) ? 'rr-bad ' : ''}rr-metric-link${partial > 0 ? ' rr-metric-chipped' : ''}`}>
      <button type="button" className="rr-metric-open" title="Bugün iptal edilen siparişler" onClick={() => onOpenDay('cancelled')}><small>İptal</small><Packages packages={cancelled} units={undefined} /></button>
      {partial > 0 && <button type="button" className="rr-metric-chip rr-metric-chip-alert" title={`Bugün kısmen iptal edilen ${count(partial)} sipariş`} onClick={() => onOpenDay('partial')}>{count(partial)} Kısmi İptal{partialUnits !== undefined && <>{' '}<span className="rr-metric-sep" aria-hidden="true">|</span>{' '}{count(partialUnits)} Adet</>}</button>}
    </article>
    <article className="rr-metric-link"><button type="button" className="rr-metric-open" title="Kargoya verilmemiş yeniden gönderimler" onClick={() => onOpenDay('resent')}><small>Yeniden Gönderim</small><strong><CountUp value={metricText(resent)} /></strong></button></article>
  </div>
}
