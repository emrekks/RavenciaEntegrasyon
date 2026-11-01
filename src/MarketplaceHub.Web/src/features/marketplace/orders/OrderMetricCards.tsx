// The summary cards above the order list. "İşleme Alınan" is the same set as the "İşleme Alınanlar"
// tab (the order summary's processing count): taken into processing, not yet handed to the carrier.
// "Yeniden Gönderim" is the counter of its tab (the /orders/summary number) and opens it.
// "Mikro İhracat Sipariş" and "İptal" show today (the Istanbul day), whatever the list's filters are: the micro-export
// orders placed today (the row's flag icon marks them; hovering lists their countries), and the orders cancelled today
// as the Genel Bakış card counts them, with the partly cancelled ones in a line under it. Each opens the list on what
// it counted.
import { CountUp } from '../../../shared/components'
import { countryName, Flag } from './order-row-marks'
import type { DayCard } from './daily-cards'
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

export function OrderMetricCards({ newOrders, processing, late, shipped, microExport, microCountries, cancelled, partiallyCancelled, resent, onOpenTab, onOpenDay }: { newOrders: Metric; processing: Metric; late: Metric; shipped: Metric; microExport: Metric; microCountries?: Record<string, number>; cancelled: Metric; partiallyCancelled: Metric; resent: Metric; onOpenTab: (tab: string) => void; onOpenDay: (card: DayCard) => void }) {
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
      <button type="button" className="rr-metric-open" title="Bugün verilen mikro ihracat siparişleri" onClick={() => onOpenDay('micro')}><small>Mikro İhracat Sipariş</small><strong><CountUp value={metricText(microExport)} /></strong></button>
      {countries.length > 0 && <div className="rr-metric-tip" role="tooltip"><small className="rr-metric-tip-h">Bugün</small>{countries.map(([code, count]) => <div className="rr-metric-tip-r" key={code || 'unknown'}>{code ? <Flag code={code} /> : <span className="rr-metric-tip-none" aria-hidden="true" />}<span>{code ? countryName(code) : 'Ülke Bilinmiyor'}</span><b className="rr-num">{count.toLocaleString('tr-TR')}</b></div>)}</div>}
    </article>
    <article className={`${positive(cancelled) ? 'rr-bad ' : ''}rr-metric-link`}>
      <button type="button" className="rr-metric-open" title="Bugün iptal edilen siparişler" onClick={() => onOpenDay('cancelled')}><small>İptal</small><strong><CountUp value={metricText(cancelled)} /></strong></button>
      {partial > 0 && <button type="button" className="rr-metric-sub" title="Bugün kısmen iptal edilen siparişler" onClick={() => onOpenDay('partial')}>{partial.toLocaleString('tr-TR')} Kısmi İptal</button>}
    </article>
    <article className="rr-metric-link"><button type="button" className="rr-metric-open" onClick={() => onOpenTab('RESENT')}><small>Yeniden Gönderim</small><strong><CountUp value={metricText(resent)} /></strong></button></article>
  </div>
}
