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
const identifier = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*[,;:-]?\s*/gi
// Mirrors the fiscal payload: one street text (by the same field priority), then district, city, country and postal code.
export function addressLines(json: string) {
  try {
    const root = JSON.parse(json) as unknown
    const find = (names: string[]): string | null => {
      const queue: unknown[] = [root]
      while (queue.length) {
        const node = queue.shift()
        if (Array.isArray(node)) { queue.push(...node); continue }
        if (typeof node !== 'object' || node === null) continue
        const record = node as Record<string, unknown>
        for (const name of names) {
          const value = Object.entries(record).find(([key, child]) => key.toLowerCase() === name.toLowerCase() && typeof child === 'string' && child.trim())?.[1]
          if (typeof value === 'string') { const text = value.replace(identifier, '').trim(); if (text) return text }
        }
        queue.push(...Object.values(record).filter(child => typeof child === 'object' && child !== null))
      }
      return null
    }
    const lines = [find(['fullAddress', 'address1', 'addressText', 'address']), find(['district']), find(['city']), find(['countryCode', 'country']), find(['postalCode'])]
    return [...new Set(lines.filter((value): value is string => Boolean(value)))]
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

export function InvoiceWorkspacePreviewModal({ targets, onClose, onComplete, onNotice }: { targets: InvoicePreviewTarget[]; onClose: () => void; onComplete: () => void; onNotice: (message: string, kind: 'success' | 'error' | 'info' | 'pending') => void }) {
  const [previews, setPreviews] = useState<PreviewItem[]>([])
  const [results, setResults] = useState<ConfirmResult | null>(null)
  const [loading, setLoading] = useState(false)
  const submitting = false
  const [error, setError] = useState<unknown>(null)
  const [includeInternetSalesInfo, setIncludeInternetSalesInfo] = useState(false)
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

  // The write goes out immediately: the dialog closes right away and the outcome arrives as a notification.
  function confirm() {
    const eligible = previews.filter(item => item.canConfirm)
    const body = JSON.stringify({ items: eligible.map(item => ({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: item.providerConnectionId, previewDigest: item.previewDigest })), includeInternetSalesInfo })
    const labels = new Map(eligible.map(item => [item.packageId, `#${item.orderNumber}`]))
    onClose()
    onNotice(eligible.length === 1 ? `${labels.get(eligible[0].packageId)} nolu siparişin faturası oluşturuluyor ve pazaryerine iletiliyor…` : `${eligible.length} siparişin faturası oluşturuluyor ve pazaryerine iletiliyor…`, 'pending')
    void hubApi<ConfirmResult>('/invoice-workspace/confirm', { method: 'POST', headers: { 'Idempotency-Key': `invoice-workspace-confirm:${crypto.randomUUID()}` }, body })
      .then(response => {
        let succeeded = 0
        let failedCount = 0
        for (const result of response.items) {
          const label = labels.get(result.packageId) ?? '#?'
          const failed = ['FAILED', 'BLOCKED', 'VALIDATION_FAILED', 'PREVIEW_STALE', 'CONFLICT'].includes(result.status)
          if (failed) { failedCount++; onNotice(`${label} nolu siparişin faturası oluşturulamadı: ${result.message}`, 'error') }
          else if (result.status === 'COMPLETED') { succeeded++; onNotice(/^Fatura (kesildi|E-Faturam)/u.test(result.message) ? `${label} nolu siparişin faturası ${result.message.replace(/^Fatura /u, '')}` : `${label} nolu sipariş: ${result.message}`, 'success') }
          else onNotice(`${label} nolu siparişin faturası: ${result.message}`, 'info')
        }
        if (response.items.length > 1) onNotice(`${response.items.length} siparişin faturası işlendi: ${succeeded} başarılı, ${failedCount} başarısız${response.items.length - succeeded - failedCount > 0 ? `, ${response.items.length - succeeded - failedCount} devam ediyor` : ''}.`, failedCount > 0 ? 'error' : succeeded > 0 ? 'success' : 'info')
      })
      .catch(reason => onNotice(`${eligible.length === 1 ? `${labels.get(eligible[0].packageId)} nolu siparişin faturası` : 'Faturalar'} oluşturulamadı: ${reason instanceof Error ? reason.message : 'Bilinmeyen hata'}`, 'error'))
      .finally(onComplete)
  }

  const eligibleCount = previews.filter(item => item.canConfirm).length
  const internetSalesInfoLocked = previews.length > 0 && previews.every(item => item.existingInvoiceId !== null && item.nextAction !== 'SUBMIT_RETRY')
  return <div className="invoice-detail-backdrop invoice-workspace-preview-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal invoice-detail-modal invoice-workspace-preview-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-preview-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Salt okunur fatura kontrolü</p><h2 id="invoice-workspace-preview-title">Fatura oluşturma önizlemesi</h2><p>Bilgileri kontrol edin; onayladığınızda fatura hemen kesilir ve sonuç bildirim olarak gelir.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Önizlemeyi kapat"><UiIcon name="close" /></button></header>
      <div className="invoice-detail-body">
        {loading ? <Busy text="Sipariş ve fatura verileri okunuyor…" /> : error && !previews.length ? <ErrorBox error={error} /> : <>
          {error && <ErrorBox error={error} />}
          {previews.map(item => {
            const address = addressLines(item.invoiceAddressJson)
            return <article className="invoice-workspace-preview-card" key={item.orderId}>
              <div className="invoice-workspace-preview-heading"><div><strong>{item.platformName} · #{item.orderNumber}</strong><span>{item.environment} · {item.customerType} · {item.invoiceType}</span></div><span className={`badge ${item.canConfirm ? 'good' : 'neutral'}`}><i aria-hidden="true" />{actionLabel(item.nextAction)}</span></div>
              <div className="invoice-workspace-preview-targets"><span className="invoice-workspace-preview-targets-label">Tek mali fatura kesilir ve {(item.targetPackageNumbers?.length ?? 0) || 1} pakete iletilir</span>{(item.targetPackageNumbers?.length ? item.targetPackageNumbers : []).map(number => <span className="invoice-workspace-preview-package" key={number}><UiIcon name="box" size={14} />{number}</span>)}{!item.targetPackageNumbers?.length && <span className="invoice-workspace-preview-package">Paket numarası yok</span>}</div>
              <dl className="invoice-workspace-preview-facts"><div><dt>Alıcı / unvan</dt><dd>{item.customerName || '—'}</dd></div><div><dt>TC / vergi no</dt><dd>{item.taxIdentityNumber || '—'}</dd></div><div><dt>Fatura adresi</dt><dd>{address.length ? address.join(', ') : 'Adres bilgisi yok'}</dd></div><div><dt>Fatura türü</dt><dd>{item.invoiceType}</dd></div>{item.deliveryPackageNumber && <div><dt>Gönderi paketi</dt><dd>{item.deliveryPackageNumber}</dd></div>}{item.deliveryCarrierName && <div><dt>Kargo firması</dt><dd className="invoice-workspace-preview-carrier"><CargoProviderIcon value={item.deliveryCarrierName} fallbackText /></dd></div>}{item.deliverySentAt && <div><dt>Gönderim tarihi</dt><dd>{new Date(item.deliverySentAt).toLocaleDateString('tr-TR')}</dd></div>}</dl>
              <div className="invoice-workspace-preview-lines" role="table" aria-label={`Sipariş ${item.orderNumber} fatura kalemleri`}><div role="row" className="invoice-workspace-preview-line invoice-workspace-preview-line-head"><strong>Ürün</strong><strong>Miktar</strong><strong>Birim fiyat</strong><strong>İndirim</strong><strong>KDV</strong><strong>Toplam</strong></div>{item.lines.map((line, index) => <div role="row" className="invoice-workspace-preview-line" key={`${line.sku ?? line.description}-${index}`}><span className="invoice-workspace-preview-product"><PreviewLineImage sku={line.sku} name={line.description} /><span><strong>{line.description}</strong><small>{line.sku || 'SKU yok'} · %{line.vatRate} KDV</small></span></span><span>{line.quantity} {line.unit}</span><span>{money(line.unitPrice, item.currency)}</span><span>{money(line.discountAmount, item.currency)}</span><span>{money(line.vatAmount, item.currency)}</span><strong>{money(line.total, item.currency)}</strong></div>)}</div>
              <div className="invoice-workspace-preview-total"><span>Ara toplam <strong>{money(item.taxExclusiveTotal, item.currency)}</strong></span><span>KDV <strong>{money(item.taxTotal, item.currency)}</strong></span><span>İndirim <strong>{money(item.discountTotal, item.currency)}</strong></span><span>Fatura toplamı <strong>{money(item.payableTotal, item.currency)}</strong></span></div>
              {item.blockedReason && <p className="invoice-workspace-preview-blocked" role="status">{item.blockedReason}</p>}
            </article>
          })}
          {results && <section className="invoice-workspace-preview-results" aria-live="polite"><h3>İşlem sonuçları</h3>{results.items.map(result => <p key={result.packageId}><strong>{result.status} · {result.action}</strong><span>{result.message}</span></p>)}</section>}
        </>}
      </div>
      <footer className="invoice-detail-footer"><label className="invoice-workspace-internet-sales-toggle" title={internetSalesInfoLocked ? 'Bu kayıtta mali fatura daha önce oluşturulmuş; seçenek mevcut faturayı değiştirmez.' : 'Kapalıysa e-Arşiv faturasına web sitesi, ödeme ve aracı hizmet bilgileri eklenmez. Kargo bilgisi bundan etkilenmez.'}><input type="checkbox" checked={includeInternetSalesInfo} disabled={loading || submitting || Boolean(results) || internetSalesInfoLocked} onChange={event => setIncludeInternetSalesInfo(event.target.checked)} /><span>İnternet Satış Bilgilerini Doldur</span></label><button type="button" className="secondary" onClick={onClose}>{results ? 'Kapat' : 'Vazgeç'}</button>{!results && <button type="button" disabled={loading || submitting || eligibleCount === 0} onClick={confirm}>{submitting ? 'İşlem kuyruğa alınıyor…' : eligibleCount > 1 ? `Onayla ve Faturayı Kes (${eligibleCount})` : previews[0]?.nextAction === 'DELIVERY_ONLY' ? 'Platforma İletimi Başlat' : 'Onayla ve Faturayı Kes'}</button>}</footer>
    </section>
  </div>
}
