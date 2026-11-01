// The summary cards above the order list. "İşleme Alınan" is the same set as the "İşleme Alınanlar"
// tab (the order summary's processing count): taken into processing, not yet handed to the carrier.
// "İptal" and "Yeniden Gönderim" are the counters of their tabs (the same /orders/summary numbers) and open them.
// "Mikro İhracat Sipariş" counts the orders the row's flag icon marks, under the list's filters in any tab, and opens
// the list filtered to them; hovering it lists the countries.
import { CountUp } from '../../../shared/components'
import { countryName, Flag } from './order-row-marks'
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

// The countries of the micro-export orders, the biggest first; orders of no known country come last.
export function microCountryRows(countries: Record<string, number> | undefined) {
  return Object.entries(countries ?? {}).filter(([, count]) => count > 0)
    .sort(([leftCode, left], [rightCode, right]) => (leftCode === '' ? 1 : 0) - (rightCode === '' ? 1 : 0) || right - left || leftCode.localeCompare(rightCode))
}

export function OrderMetricCards({ newOrders, processing, late, shipped, microExport, microCountries, cancelled, partiallyCancelled, resent, onOpenTab, onOpenMicro }: { newOrders: Metric; processing: Metric; late: Metric; shipped: Metric; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; partiallyCancelled: Metric; resent: Metric; onOpenTab: (tab: string) => void; onOpenMicro: () => void }) {
  // The partly cancelled orders are a line under the cancelled count only while there are any.
  const partial = partiallyCancelled.loading ? 0 : partiallyCancelled.value ?? 0
  // A warning card is plain at 0 and warns only above it: Geciken with a mild box and dot, İptal with its number alone.
  const positive = (metric: Metric) => !metric.loading && (metric.value ?? 0) > 0
  const countries = microCountryRows(microCountries)
  return <div className="rr-metrics rr-metrics-7">
    <article className="rr-warn"><small>Yeni Sipariş</small><strong><CountUp value={metricText(newOrders)} /></strong></article>
    <article><small>İşleme Alınan</small><strong><CountUp value={metricText(processing)} /></strong></article>
    <article className={positive(late) ? 'rr-bad rr-alert' : undefined}><small>Geciken</small><strong><CountUp value={metricText(late)} /></strong></article>
    <article><small>Kargoda</small><strong><CountUp value={metricText(shipped)} /></strong></article>
    <article className="rr-metric-link rr-metric-tipped">
      <button type="button" className="rr-metric-open" onClick={onOpenMicro}><small>Mikro İhracat Sipariş</small><strong><CountUp value={metricText(microExport)} /></strong></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip">{countries.map(([code, count]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count.toLocaleString('tr-TR')}</b></div>)}</div>}
    </article>
    <article className={`${positive(cancelled) ? 'rr-bad ' : ''}rr-metric-link`}>
      <button type="button" className="rr-metric-open" onClick={() => onOpenTab('CANCELLED')}><small>İptal</small><strong><CountUp value={metricText(cancelled)} /></strong></button>
      {partial > 0 && <button type="button" className="rr-metric-sub" onClick={() => onOpenTab('PARTIALLY_CANCELLED')}>{partial.toLocaleString('tr-TR')} Kısmi İptal</button>}
    </article>
    <article className="rr-metric-link"><button type="button" className="rr-metric-open" onClick={() => onOpenTab('RESENT')}><small>Yeniden Gönderim</small><strong><CountUp value={metricText(resent)} /></strong></button></article>
  </div>
}
