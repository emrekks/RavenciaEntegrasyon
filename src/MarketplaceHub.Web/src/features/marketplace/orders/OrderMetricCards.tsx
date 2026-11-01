// The summary cards above the order list. "İşleme Alınan" is the same set as the "İşleme Alınanlar"
// tab (the order summary's processing count): taken into processing, not yet handed to the carrier.
// "Mikro İhracat Sipariş" counts the pending micro-export orders, whatever the list's filters are: the orders the row's
// flag icon marks that are not yet handed to the carrier (New and İşleme Alınan, the overview's Bekleyen set, any day);
// hovering lists their countries. "İptal" shows today (the Istanbul day): the orders cancelled today as the Genel Bakış
// card counts them, with the partly cancelled ones in a chip beside the count.
// "Yeniden Gönderim" counts the resends that have not been handed to the carrier yet. "Kargolanacak Ürün Adedi" is the
// products of the orders still to ship, the overview's Bekleyen Ürün Adedi; hovering lists the stores.
// Each of these opens the list on what it counted (the products to ship open the pending orders, the overview's list).
import { CountUp } from '../../../shared/components'
import { RrLogo } from '../returns-ui'
import { countryName, Flag } from './order-row-marks'
import type { DayCard } from './daily-cards'
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }
type ShipRow = { key: string; code: string; name: string; units: number }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

// The countries of the micro-export orders, the biggest first; orders of no known country come last.
export function microCountryRows(countries: Record<string, number> | undefined) {
  return Object.entries(countries ?? {}).filter(([, count]) => count > 0)
    .sort(([leftCode, left], [rightCode, right]) => (leftCode === '' ? 1 : 0) - (rightCode === '' ? 1 : 0) || right - left || leftCode.localeCompare(rightCode))
}

export function OrderMetricCards({ newOrders, processing, late, shipped, microExport, microCountries, cancelled, partiallyCancelled, resent, shipUnits, shipRows, onOpenDay }: { newOrders: Metric; processing: Metric; late: Metric; shipped: Metric; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; partiallyCancelled: Metric; resent: Metric; shipUnits: Metric; shipRows?: ShipRow[]; onOpenDay: (card: DayCard) => void }) {
  // The partly cancelled orders are a chip beside the cancelled count only while there are any.
  const partial = partiallyCancelled.loading ? 0 : partiallyCancelled.value ?? 0
  // A warning card is plain at 0 and warns only above it: Geciken with a mild box and dot, İptal with its number alone.
  const positive = (metric: Metric) => !metric.loading && (metric.value ?? 0) > 0
  const countries = microCountryRows(microCountries)
  const stores = shipRows ?? []
  return <div className="rr-metrics rr-metrics-8">
    <article className="rr-warn"><small>Yeni Sipariş</small><strong><CountUp value={metricText(newOrders)} /></strong></article>
    <article><small>İşleme Alınan</small><strong><CountUp value={metricText(processing)} /></strong></article>
    <article className={positive(late) ? 'rr-bad rr-alert' : undefined}><small>Geciken</small><strong><CountUp value={metricText(late)} /></strong></article>
    <article><small>Kargoda</small><strong><CountUp value={metricText(shipped)} /></strong></article>
    <article className="rr-metric-link rr-metric-tipped">
      <button type="button" className="rr-metric-open" title={countries.length > 0 ? undefined : 'Kargoya verilmemiş mikro ihracat siparişleri'} onClick={() => onOpenDay('micro')}><small>Mikro İhracat Sipariş</small><strong><CountUp value={metricText(microExport)} /></strong></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip">{countries.map(([code, count]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count.toLocaleString('tr-TR')}</b></div>)}</div>}
    </article>
    <article className={`${positive(cancelled) ? 'rr-bad ' : ''}rr-metric-link${partial > 0 ? ' rr-metric-chipped' : ''}`}>
      <button type="button" className="rr-metric-open" title="Bugün iptal edilen siparişler" onClick={() => onOpenDay('cancelled')}><small>İptal</small><strong><CountUp value={metricText(cancelled)} /></strong></button>
      {partial > 0 && <button type="button" className="rr-metric-chip" title={`Bugün kısmen iptal edilen ${partial.toLocaleString('tr-TR')} sipariş`} onClick={() => onOpenDay('partial')}>{partial.toLocaleString('tr-TR')} Kısmi</button>}
    </article>
    <article className="rr-metric-link"><button type="button" className="rr-metric-open" title="Kargoya verilmemiş yeniden gönderimler" onClick={() => onOpenDay('resent')}><small>Yeniden Gönderim</small><strong><CountUp value={metricText(resent)} /></strong></button></article>
    <article className="rr-metric-link rr-metric-tipped">
      <button type="button" className="rr-metric-open" onClick={() => onOpenDay('ship')}><small>Kargolanacak Ürün Adedi</small><strong><CountUp value={metricText(shipUnits)} /></strong></button>
      {stores.length > 0 && <div className="rr-metric-tip" role="tooltip">{stores.map(row => <div className="rr-metric-tip-r" key={row.key}><RrLogo code={row.code} /><span>{row.name}</span><b className="rr-num">{row.units.toLocaleString('tr-TR')} adet</b></div>)}</div>}
    </article>
  </div>
}
