// The summary cards above the order list. "İşleme Alınan" is the same set as the "İşleme Alınanlar"
// tab (the order summary's processing count): taken into processing, not yet handed to the carrier.
// "İptal" and "Yeniden Gönderim" are the counters of their tabs (the same /orders/summary numbers) and open them.
import { CountUp } from '../../../shared/components'
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

export function OrderMetricCards({ newOrders, processing, late, shipped, uninvoiced, cancelled, partiallyCancelled, resent, onOpenTab }: { newOrders: Metric; processing: Metric; late: Metric; shipped: Metric; uninvoiced: Metric; cancelled: Metric; partiallyCancelled: Metric; resent: Metric; onOpenTab: (tab: string) => void }) {
  // The partly cancelled orders are a line under the cancelled count only while there are any.
  const partial = partiallyCancelled.loading ? 0 : partiallyCancelled.value ?? 0
  return <div className="rr-metrics rr-metrics-7">
    <article className="rr-warn"><small>Yeni Sipariş</small><strong><CountUp value={metricText(newOrders)} /></strong></article>
    <article><small>İşleme Alınan</small><strong><CountUp value={metricText(processing)} /></strong></article>
    <article className="rr-bad"><small>Geciken</small><strong><CountUp value={metricText(late)} /></strong></article>
    <article><small>Kargoda</small><strong><CountUp value={metricText(shipped)} /></strong></article>
    <article><small>Fatura Bekleyen</small><strong><CountUp value={metricText(uninvoiced)} /></strong></article>
    <article className="rr-bad rr-metric-link">
      <button type="button" className="rr-metric-open" onClick={() => onOpenTab('CANCELLED')}><small>İptal</small><strong><CountUp value={metricText(cancelled)} /></strong></button>
      {partial > 0 && <button type="button" className="rr-metric-sub" onClick={() => onOpenTab('PARTIALLY_CANCELLED')}>{partial.toLocaleString('tr-TR')} Kısmi İptal</button>}
    </article>
    <article className="rr-metric-link"><button type="button" className="rr-metric-open" onClick={() => onOpenTab('RESENT')}><small>Yeniden Gönderim</small><strong><CountUp value={metricText(resent)} /></strong></button></article>
  </div>
}
