import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { RrIcon, RrModal } from '../marketplace/returns-ui'
import { handSetInvoiceStatus, handSetInvoiceStatusNote, setManualInvoiceStatus, type ManualInvoiceStatus } from './manual-invoice-status'

// The one "Fatura Durumunu Değiştir" window for a Shopify order, opened from the invoice page and the orders page
// (audit W13). It only records the status by hand: no invoice is issued and nothing is sent to Shopify. "Faturası
// Yüklendi" needs no document, since the seller uploads the invoice in Shopify himself (owner, 6 Nov 2026).
export const shopifyInvoiceStatusHint = 'Durum elle işaretlenir: yalnızca paneldeki fatura durumu değişir. Fatura kesilmez; Shopify’a fatura ya da belge gönderilmez.'
export const shopifyUploadedNote = 'Faturayı Shopify’da kendiniz yüklediyseniz belge eklemeden kaydedebilirsiniz. Durum her ekranda “Elle İşaretlendi” notuyla görünür.'

export function ShopifyInvoiceStatusDialog({ orderNumber, invoiceStatus, manualInvoiceStatus, resolvePackageId, onClose, onFeedback }: {
  orderNumber: string
  invoiceStatus: string
  manualInvoiceStatus?: string | null
  // The Shopify package the status belongs to; it may need the order detail first.
  resolvePackageId: () => Promise<string> | string
  onClose: () => void
  onFeedback: (message: string, kind: 'success' | 'error') => void
}) {
  const client = useQueryClient()
  const handSet = handSetInvoiceStatus(manualInvoiceStatus)
  // A hand-set "uploaded" reads as FATURA_KONTROLDE ("Faturası Mevcut") on every screen.
  const [status, setStatus] = useState<ManualInvoiceStatus>(handSet ?? (['FATURA_YUKLENDI', 'FATURA_KONTROLDE'].includes(invoiceStatus) ? 'UPLOADED' : 'PENDING'))
  const [message, setMessage] = useState('')
  const update = useMutation({
    mutationFn: async () => setManualInvoiceStatus([await resolvePackageId()], status),
    onSuccess: async () => {
      await Promise.all([['orders'], ['invoice-viewer'], ['invoice'], ['invoice-workspace'], ['invoice-workspace-summary'], ['invoice-workspace-page']].map(queryKey => client.invalidateQueries({ queryKey })))
      onFeedback(status === 'UPLOADED' ? 'Shopify fatura durumu “Faturası yüklendi” olarak elle işaretlendi.' : 'Shopify fatura durumu “Fatura bekliyor” olarak elle işaretlendi.', 'success')
      onClose()
    },
    onError: error => {
      const errorMessage = error instanceof Error ? error.message : 'Fatura durumu güncellenemedi.'
      setMessage(errorMessage)
      onFeedback(errorMessage, 'error')
    }
  })
  return <RrModal crumb="Shopify" title="Fatura Durumunu Değiştir" desc={orderNumber} hint={shopifyInvoiceStatusHint} size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Kaydediliyor…' : 'Durumu Kaydet'}</button></>}>
    <label className="rr-fld"><small>Yeni Fatura Durumu</small><select className="rr-in" aria-label="Yeni fatura durumu" value={status} onChange={event => setStatus(event.target.value as ManualInvoiceStatus)} disabled={update.isPending}><option value="PENDING">Fatura Bekliyor</option><option value="UPLOADED">Faturası Yüklendi</option></select></label>
    {handSet && <div className="rr-callout" role="note"><RrIcon name="info" /><span>Mevcut durum: <b>{handSet === 'UPLOADED' ? 'Faturası Yüklendi' : 'Fatura Bekliyor'}</b> · {handSetInvoiceStatusNote}</span></div>}
    {status === 'UPLOADED' && <div className="rr-callout" role="status"><RrIcon name="info" /><span>{shopifyUploadedNote}</span></div>}
    {message && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{message}</span></div>}
  </RrModal>
}
