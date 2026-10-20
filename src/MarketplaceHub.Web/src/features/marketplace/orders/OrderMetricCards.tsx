// The summary cards above the order list. "İşleme Alınan" is the same set as the "İşleme Alınanlar"
// tab (the order summary's processing count): taken into processing, not yet handed to the carrier.
import './order-metrics.css'

type Metric = { value: number | undefined; loading: boolean }

function metricText({ value, loading }: Metric) {
  return loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
}

export function OrderMetricCards({ newOrders, processing, late, shipped, uninvoiced }: { newOrders: Metric; processing: Metric; late: Metric; shipped: Metric; uninvoiced: Metric }) {
  return <div className="rr-metrics rr-metrics-5">
    <article className="rr-warn"><small>Yeni Sipariş</small><strong>{metricText(newOrders)}</strong><span>işleme alınmayı bekliyor</span></article>
    <article><small>İşleme Alınan</small><strong>{metricText(processing)}</strong><span>kargoya verilmeyi bekliyor</span></article>
    <article className="rr-bad"><small>Geciken</small><strong>{metricText(late)}</strong><span>kargoya verme süresi doldu</span></article>
    <article><small>Kargoda</small><strong>{metricText(shipped)}</strong><span>alıcıya doğru yolda</span></article>
    <article><small>Fatura Bekleyen</small><strong>{metricText(uninvoiced)}</strong><span>kesilmesi gereken fatura</span></article>
  </div>
}
