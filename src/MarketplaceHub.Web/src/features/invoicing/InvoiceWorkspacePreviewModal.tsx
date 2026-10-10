import { useEffect, useMemo, useState } from 'react'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, UiIcon } from '../../shared/components'

export type InvoicePreviewTarget = { orderId: string; packageId: string; providerConnectionId: string }
type PreviewLine = { description: string; sku: string | null; quantity: number; unit: string; vatRate: number; unitPrice: number; discountAmount: number; vatAmount: number; total: number }
type PreviewItem = {
  orderId: string; packageId: string; providerConnectionId: string; orderNumber: string; platformCode: string; platformName: string; environment: string
  customerType: string; customerName: string; taxIdentityNumber: string; invoiceAddressJson: string; invoiceType: string; currency: string
  taxExclusiveTotal: number; discountTotal: number; taxTotal: number; payableTotal: number; lines: PreviewLine[]; canConfirm: boolean; blockedReason: string | null
  previewDigest: string; existingInvoiceId: string | null; existingInvoiceStatus: string | null; nextAction: string | null
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
function actionLabel(action: string | null) {
  return action === 'DELIVERY_ONLY' ? 'Yalnız platforma iletim'
    : action === 'SUBMIT_RETRY' ? 'Kesin reddedilmiş mali isteği tekrar sıraya al'
      : action === 'CREATE_AND_SUBMIT' ? 'Mali faturayı oluştur ve platforma ilet'
        : action === 'WAIT' ? 'Mevcut işlem sonuçlanıyor' : action === 'NONE' ? 'İşlem tamamlandı' : 'İnceleme gerekiyor'
}

export function InvoiceWorkspacePreviewModal({ targets, onClose, onComplete }: { targets: InvoicePreviewTarget[]; onClose: () => void; onComplete: () => void }) {
  const [previews, setPreviews] = useState<PreviewItem[]>([])
  const [results, setResults] = useState<ConfirmResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const targetPayload = useMemo(() => JSON.stringify(targets), [targets])

  useEffect(() => {
    let active = true
    setLoading(true); setError(null); setResults(null)
    void hubApi<PreviewItem[]>('/invoice-workspace/preview', { method: 'POST', body: JSON.stringify({ items: JSON.parse(targetPayload) as InvoicePreviewTarget[] }) })
      .then(items => { if (active) setPreviews(items) })
      .catch(reason => { if (active) setError(reason) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [targetPayload])

  async function confirm() {
    setSubmitting(true); setError(null)
    try {
      const eligible = previews.filter(item => item.canConfirm)
      const response = await hubApi<ConfirmResult>('/invoice-workspace/confirm', {
        method: 'POST',
        headers: { 'Idempotency-Key': `invoice-workspace-confirm:${crypto.randomUUID()}` },
        body: JSON.stringify({ items: eligible.map(item => ({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: item.providerConnectionId, previewDigest: item.previewDigest })) })
      })
      setResults(response)
      onComplete()
    } catch (reason) { setError(reason) }
    finally { setSubmitting(false) }
  }

  const eligibleCount = previews.filter(item => item.canConfirm).length
  return <div className="invoice-detail-backdrop invoice-workspace-preview-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal invoice-detail-modal invoice-workspace-preview-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-preview-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Salt okunur fatura kontrolü</p><h2 id="invoice-workspace-preview-title">Fatura oluşturma önizlemesi</h2><p>Bilgileri kontrol edip onayladığınızda işlem kuyruğa alınır.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Önizlemeyi kapat"><UiIcon name="close" /></button></header>
      <div className="invoice-detail-body">
        {loading ? <Busy text="Sipariş ve fatura verileri okunuyor…" /> : error && !previews.length ? <ErrorBox error={error} /> : <>
          {error && <ErrorBox error={error} />}
          {previews.map(item => {
            const address = addressLines(item.invoiceAddressJson)
            return <article className="invoice-workspace-preview-card" key={item.packageId}>
              <div className="invoice-workspace-preview-heading"><div><strong>{item.platformName} · #{item.orderNumber}</strong><span>{item.environment} · {item.customerType} · {item.invoiceType}</span></div><span className={`badge ${item.canConfirm ? 'good' : 'warn'}`}><i aria-hidden="true" />{actionLabel(item.nextAction)}</span></div>
              <dl className="invoice-workspace-preview-facts"><div><dt>Alıcı / unvan</dt><dd>{item.customerName || '—'}</dd></div><div><dt>TC / vergi no</dt><dd>{item.taxIdentityNumber || '—'}</dd></div><div><dt>Fatura adresi</dt><dd>{address.length ? address.join(', ') : 'Adres bilgisi yok'}</dd></div><div><dt>Fatura türü</dt><dd>{item.invoiceType}</dd></div></dl>
              <div className="invoice-workspace-preview-lines" role="table" aria-label={`Sipariş ${item.orderNumber} fatura kalemleri`}><div role="row" className="invoice-workspace-preview-line invoice-workspace-preview-line-head"><strong>Ürün</strong><strong>Miktar</strong><strong>Birim fiyat</strong><strong>İndirim</strong><strong>KDV</strong><strong>Toplam</strong></div>{item.lines.map((line, index) => <div role="row" className="invoice-workspace-preview-line" key={`${line.sku ?? line.description}-${index}`}><span><strong>{line.description}</strong><small>{line.sku || 'SKU yok'} · %{line.vatRate} KDV</small></span><span>{line.quantity} {line.unit}</span><span>{money(line.unitPrice, item.currency)}</span><span>{money(line.discountAmount, item.currency)}</span><span>{money(line.vatAmount, item.currency)}</span><strong>{money(line.total, item.currency)}</strong></div>)}</div>
              <div className="invoice-workspace-preview-total"><span>Ara toplam <strong>{money(item.taxExclusiveTotal, item.currency)}</strong></span><span>KDV <strong>{money(item.taxTotal, item.currency)}</strong></span><span>İndirim <strong>{money(item.discountTotal, item.currency)}</strong></span><span>Fatura toplamı <strong>{money(item.payableTotal, item.currency)}</strong></span></div>
              {item.blockedReason && <p className="invoice-workspace-preview-blocked" role="status">{item.blockedReason}</p>}
            </article>
          })}
          {results && <section className="invoice-workspace-preview-results" aria-live="polite"><h3>İşlem sonuçları</h3>{results.items.map(result => <p key={result.packageId}><strong>{result.status} · {result.action}</strong><span>{result.message}</span></p>)}</section>}
        </>}
      </div>
      <footer className="invoice-detail-footer"><button type="button" className="secondary" onClick={onClose}>{results ? 'Kapat' : 'Vazgeç'}</button>{!results && <button type="button" disabled={loading || submitting || eligibleCount === 0} onClick={() => void confirm()}>{submitting ? 'İşlem kuyruğa alınıyor…' : eligibleCount > 1 ? `Onayla ve sıraya al (${eligibleCount})` : previews[0]?.nextAction === 'DELIVERY_ONLY' ? 'Platforma iletimi sıraya al' : 'Onayla ve sıraya al'}</button>}</footer>
    </section>
  </div>
}
