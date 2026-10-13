import { useEffect, useMemo, useState } from 'react'
import { hubApi } from '../../shared/api'
import { Busy, CargoProviderIcon, ErrorBox, UiIcon } from '../../shared/components'
import { productImageFallbackUrls } from '../marketplace/product-image-lookups'

export type InvoicePreviewTarget = { orderId: string; packageId: string; providerConnectionId: string }
type PreviewLine = { description: string; sku: string | null; quantity: number; unit: string; vatRate: number; unitPrice: number; discountAmount: number; vatAmount: number; total: number }
type PreviewItem = {
  orderId: string; packageId: string; providerConnectionId: string; orderNumber: string; platformCode: string; platformName: string; environment: string
  customerType: string; customerName: string; taxIdentityNumber: string; invoiceAddressJson: string; invoiceType: string; currency: string
  taxExclusiveTotal: number; discountTotal: number; taxTotal: number; payableTotal: number; lines: PreviewLine[]; canConfirm: boolean; blockedReason: string | null
  previewDigest: string; existingInvoiceId: string | null; existingInvoiceStatus: string | null; nextAction: string | null; includeInternetSalesInfo: boolean
  targetPackageNumbers?: string[] | null
  deliveryCarrierName?: string | null; deliverySentAt?: string | null; deliveryPackageNumber?: string | null
}
type ConfirmResult = { items: Array<{ packageId: string; invoiceId: string | null; jobId: string | null; status: string; action: string; message: string }> }

function money(value: number, currency: string) { return value.toLocaleString('tr-TR', { style: 'currency', currency }) }
function addressLines(json: string) {
  try {
    const root = JSON.parse(json) as unknown
    const output: string[] = []
    const visit = (value: unknown) => {
      if (Array.isArray(value)) { value.forEach(visit); return }
      if (typeof value !== 'object' || value === null) return
      for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
        if (typeof child === 'string' && child.trim() && /address|street|city|district|province|postal|zip|neighborhood|country|full/i.test(key)) output.push(child.trim())
        else if (typeof child === 'object') visit(child)
      }
    }
    visit(root)
    return [...new Set(output)].slice(0, 6)
  } catch { return [] }
}
function PreviewLineImage({ sku, name }: { sku: string | null; name: string }) {
  const sources = useMemo(() => productImageFallbackUrls([sku], null, name), [sku, name])
  const [index, setIndex] = useState(0)
  useEffect(() => setIndex(0), [sources])
  const src = sources[index]
  return <span className="invoice-workspace-preview-thumb">{src ? <img src={src} alt={`${name} görseli`} loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => setIndex(current => current + 1)} /> : <UiIcon name="image" size={18} />}</span>
}
function actionLabel(action: string | null) {
  return action === 'DELIVERY_ONLY' ? 'Yalnız Platforma İletim'
    : action === 'SUBMIT_RETRY' ? 'Reddedilen Mali İsteği Tekrar Sıraya Al'
      : action === 'CREATE_AND_SUBMIT' ? 'Mali Faturayı Oluştur ve Platforma İlet'
        : action === 'WAIT' ? 'Mevcut İşlem Sonuçlanıyor' : action === 'NONE' ? 'İşlem Tamamlandı' : 'İnceleme Gerekiyor'
}

export function InvoiceWorkspacePreviewModal({ targets, onClose, onComplete }: { targets: InvoicePreviewTarget[]; onClose: () => void; onComplete: () => void }) {
  const [previews, setPreviews] = useState<PreviewItem[]>([])
  const [results, setResults] = useState<ConfirmResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [includeInternetSalesInfo, setIncludeInternetSalesInfo] = useState(true)
  const targetPayload = useMemo(() => JSON.stringify(targets), [targets])

  useEffect(() => {
    let active = true
    setLoading(true); setError(null); setResults(null); setPreviews([])
    void hubApi<PreviewItem[]>('/invoice-workspace/preview', { method: 'POST', body: JSON.stringify({ items: JSON.parse(targetPayload) as InvoicePreviewTarget[], includeInternetSalesInfo }) })
      .then(items => { if (active) setPreviews(items) })
      .catch(reason => { if (active) setError(reason) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [targetPayload, includeInternetSalesInfo])

  async function confirm() {
    setSubmitting(true); setError(null)
    try {
      const eligible = previews.filter(item => item.canConfirm)
      const response = await hubApi<ConfirmResult>('/invoice-workspace/confirm', {
        method: 'POST',
        headers: { 'Idempotency-Key': `invoice-workspace-confirm:${crypto.randomUUID()}` },
        body: JSON.stringify({ items: eligible.map(item => ({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: item.providerConnectionId, previewDigest: item.previewDigest })), includeInternetSalesInfo })
      })
      setResults(response)
      onComplete()
    } catch (reason) { setError(reason) }
    finally { setSubmitting(false) }
  }

  const eligibleCount = previews.filter(item => item.canConfirm).length
  const internetSalesInfoLocked = previews.length > 0 && previews.every(item => item.existingInvoiceId !== null && item.nextAction !== 'SUBMIT_RETRY')
  return <div className="invoice-detail-backdrop invoice-workspace-preview-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal invoice-detail-modal invoice-workspace-preview-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-preview-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Salt okunur fatura kontrolü</p><h2 id="invoice-workspace-preview-title">Fatura oluşturma önizlemesi</h2><p>Bilgileri kontrol edip onayladığınızda işlem kuyruğa alınır.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Önizlemeyi kapat"><UiIcon name="close" /></button></header>
      <div className="invoice-detail-body">
        {loading ? <Busy text="Sipariş ve fatura verileri okunuyor…" /> : error && !previews.length ? <ErrorBox error={error} /> : <>
          {error && <ErrorBox error={error} />}
          {previews.map(item => {
            const address = addressLines(item.invoiceAddressJson)
            return <article className="invoice-workspace-preview-card" key={item.orderId}>
              <div className="invoice-workspace-preview-heading"><div><strong>{item.platformName} · #{item.orderNumber}</strong><span>{item.environment} · {item.customerType} · {item.invoiceType}</span></div><span className={`badge ${item.canConfirm ? 'good' : 'neutral'}`}><i aria-hidden="true" />{actionLabel(item.nextAction)}</span></div>
              <p className="invoice-workspace-preview-targets">Tek mali fatura · {(item.targetPackageNumbers?.length ?? 0)} hedef paket{(item.targetPackageNumbers?.length ?? 0) === 1 ? '' : 'e'} iletilecek: {item.targetPackageNumbers?.join(', ') || 'Paket numarası yok'}</p>
              <dl className="invoice-workspace-preview-facts"><div><dt>Alıcı / unvan</dt><dd>{item.customerName || '—'}</dd></div><div><dt>TC / vergi no</dt><dd>{item.taxIdentityNumber || '—'}</dd></div><div><dt>Fatura adresi</dt><dd>{address.length ? address.join(', ') : 'Adres bilgisi yok'}</dd></div><div><dt>Fatura türü</dt><dd>{item.invoiceType}</dd></div>{item.deliveryPackageNumber && <div><dt>Gönderi paketi</dt><dd>{item.deliveryPackageNumber}</dd></div>}{item.deliveryCarrierName && <div><dt>Kargo firması</dt><dd className="invoice-workspace-preview-carrier"><CargoProviderIcon value={item.deliveryCarrierName} fallbackText /></dd></div>}{item.deliverySentAt && <div><dt>Gönderim tarihi</dt><dd>{new Date(item.deliverySentAt).toLocaleDateString('tr-TR')}</dd></div>}</dl>
              <div className="invoice-workspace-preview-lines" role="table" aria-label={`Sipariş ${item.orderNumber} fatura kalemleri`}><div role="row" className="invoice-workspace-preview-line invoice-workspace-preview-line-head"><strong>Ürün</strong><strong>Miktar</strong><strong>Birim fiyat</strong><strong>İndirim</strong><strong>KDV</strong><strong>Toplam</strong></div>{item.lines.map((line, index) => <div role="row" className="invoice-workspace-preview-line" key={`${line.sku ?? line.description}-${index}`}><span className="invoice-workspace-preview-product"><PreviewLineImage sku={line.sku} name={line.description} /><span><strong>{line.description}</strong><small>{line.sku || 'SKU yok'} · %{line.vatRate} KDV</small></span></span><span>{line.quantity} {line.unit}</span><span>{money(line.unitPrice, item.currency)}</span><span>{money(line.discountAmount, item.currency)}</span><span>{money(line.vatAmount, item.currency)}</span><strong>{money(line.total, item.currency)}</strong></div>)}</div>
              <div className="invoice-workspace-preview-total"><span>Ara toplam <strong>{money(item.taxExclusiveTotal, item.currency)}</strong></span><span>KDV <strong>{money(item.taxTotal, item.currency)}</strong></span><span>İndirim <strong>{money(item.discountTotal, item.currency)}</strong></span><span>Fatura toplamı <strong>{money(item.payableTotal, item.currency)}</strong></span></div>
              {item.blockedReason && <p className="invoice-workspace-preview-blocked" role="status">{item.blockedReason}</p>}
            </article>
          })}
          {results && <section className="invoice-workspace-preview-results" aria-live="polite"><h3>İşlem sonuçları</h3>{results.items.map(result => <p key={result.packageId}><strong>{result.status} · {result.action}</strong><span>{result.message}</span></p>)}</section>}
        </>}
      </div>
      <footer className="invoice-detail-footer"><label className="invoice-workspace-internet-sales-toggle" title={internetSalesInfoLocked ? 'Bu kayıtta mali fatura daha önce oluşturulmuş; seçenek mevcut faturayı değiştirmez.' : 'Kapalıysa e-Arşiv faturasına web sitesi, ödeme ve aracı hizmet bilgileri eklenmez. Kargo bilgisi bundan etkilenmez.'}><input type="checkbox" checked={includeInternetSalesInfo} disabled={loading || submitting || Boolean(results) || internetSalesInfoLocked} onChange={event => setIncludeInternetSalesInfo(event.target.checked)} /><span>İnternet Satış Bilgilerini Doldur</span></label><button type="button" className="secondary" onClick={onClose}>{results ? 'Kapat' : 'Vazgeç'}</button>{!results && <button type="button" disabled={loading || submitting || eligibleCount === 0} onClick={() => void confirm()}>{submitting ? 'İşlem kuyruğa alınıyor…' : eligibleCount > 1 ? `Onayla ve sıraya al (${eligibleCount})` : previews[0]?.nextAction === 'DELIVERY_ONLY' ? 'Platforma iletimi sıraya al' : 'Onayla ve sıraya al'}</button>}</footer>
    </section>
  </div>
}
