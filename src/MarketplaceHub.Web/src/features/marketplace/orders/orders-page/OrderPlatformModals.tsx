import { PanelSelect } from '../../../../shared/PanelSelect'
import { useState, type ChangeEvent, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../../shared/idempotency'
import { setManualInvoiceStatus, type ManualInvoiceStatus } from '../../../invoicing/manual-invoice-status'
import { shopifyInvoiceStatusHint, shopifyUploadedNote } from '../../../invoicing/ShopifyInvoiceStatusDialog'
import { statusLabel } from '../../../../shared/status-labels'
import { RrIcon, RrLogo, RrModal, RrThumb, titleTr } from '../../../../shared/ui/rr-components'
import { orderStatusLabel } from '../../order-status-tabs'
import { formatPackageLabel } from '../../../../shared/package-label'
import { Connection, Order, OrderLine, displayOrderNumber, orderDisplayShipment, productLineQuantity, returnDateText } from '../../shared'
import { queryKeys } from '../../../../shared/api/query-keys'
import { type OrderDetail, type IntegrationFeedbackHandler, customerDisplayName } from './order-page-helpers'

// An optional package field left empty is sent as null, so Hepsiburada's request leaves it out.
function optionalText(value: FormDataEntryValue | null) {
  return typeof value === 'string' && value.trim() ? value.trim() : null
}

export function HepsiburadaPackageCreateModal({ item, onClose, onCreated }: { item: Order; onClose: () => void; onCreated: (packageNumber: string) => void }) {
  // A cancelled product is not packed.
  const lines = (item.lines ?? []).filter(line => productLineQuantity(line) > 0)
  const availableQuantity = (line: OrderLine) => Math.max(0, line.orderedQuantity - line.cancelledQuantity - (line.allocatedQuantity ?? 0))
  const firstLine = lines.find(line => availableQuantity(line) > 0)
  const [selected, setSelected] = useState<Record<string, number>>(() => firstLine ? { [firstLine.id]: availableQuantity(firstLine) } : {})
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const selectedCount = Object.keys(selected).filter(id => selected[id] > 0).length

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selectedCount || saving) return
    const data = new FormData(event.currentTarget)
    const lineItems = Object.entries(selected).filter(([, quantity]) => quantity > 0).map(([orderLineId, quantity]) => ({ orderLineId, quantity }))
    setSaving(true)
    setError('')
    const body = JSON.stringify({
      barcode: optionalText(data.get('barcode')),
      cargoCompany: optionalText(data.get('cargoCompany')),
      carrier: optionalText(data.get('carrier')),
      creationReason: optionalText(data.get('creationReason')),
      deci: Number(data.get('deci')),
      parcelQuantity: Number(data.get('parcelQuantity')),
      shippingAddressLabel: optionalText(data.get('shippingAddressLabel')),
      shippingModel: optionalText(data.get('shippingModel')),
      lineItems,
    })
    try {
      // The same key for a resend of the same form on the same order version (a lost answer reaches Hepsiburada once);
      // a corrected form, or any attempt after the server answered with an error (a 422), goes out with a new key.
      const result = await sendWithVersionedIdempotencyKey(versionedIdempotencyKey('hb-package', item.id, item.version, body), key => hubApi<{ packageNumber: string }>(`/orders/${item.id}/packages`, {
        method: 'POST',
        headers: { 'Idempotency-Key': key, 'If-Match': `"v${item.version}"` },
        body,
      }))
      onCreated(result.packageNumber)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Hepsiburada paketi oluşturulamadı.')
    } finally {
      setSaving(false)
    }
  }

  const facts: Array<[string, string]> = [['Müşteri', customerDisplayName(item)], ['Sipariş Tarihi', returnDateText(item.orderedAt)], ['Paket No', formatPackageLabel(orderDisplayShipment(item)?.externalPackageId) ?? 'Henüz paketlenmedi']]
  return <RrModal crumb="Hepsiburada Siparişi" title="Kargo Paketi Oluştur" desc={`${displayOrderNumber(item.orderNumber)} · ${customerDisplayName(item)}`} hint="Paket oluşturma Hepsiburada’ya veri gönderir; uygun sipariş kalemleri önce doğrulanır. Bağlantı veya yazma ayarları kapalıysa işlem reddedilir." size="lg" locked={saving} onClose={onClose} foot={<><span className="rr-note">Seçili kalem: <b>{selectedCount}</b></span><button type="button" className="rr-btn" onClick={onClose} disabled={saving}>Vazgeç</button><button type="submit" form="hb-package-form" className="rr-btn rr-primary" disabled={saving || !selectedCount || !lines.length}><RrIcon name="box" />{saving ? 'Paket Oluşturuluyor…' : 'Hepsiburada’da Paketi Oluştur'}</button></>}>
    <form id="hb-package-form" style={{ display: 'contents' }} onSubmit={submit}>
      <section className="rr-osum" aria-label="Sipariş bilgileri"><span><RrLogo code={item.platformCode} />{displayOrderNumber(item.orderNumber)}</span><dl>{facts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl></section>
      <div className="rr-h3">Sipariş Kalemleri</div>
      {lines.length ? <div className="rr-apr" role="table"><div className="rr-apr-h" role="row"><span role="columnheader">Bilgiler</span><span role="columnheader">Pakete Girecek Adet</span></div>{lines.map(line => { const available = availableQuantity(line); const on = (selected[line.id] ?? 0) > 0; return <div className="rr-apr-r" role="row" key={line.id}><label className="rr-apr-info"><input type="checkbox" className="rr-cb" checked={on} disabled={available <= 0 || saving} onChange={event => setSelected(current => { const next = { ...current }; if (event.target.checked) next[line.id] = available; else delete next[line.id]; return next })} /><RrThumb src={line.imageUrl ?? item.primaryImageUrl} fallbackSrc={line.fallbackImageUrl} quantity={productLineQuantity(line)} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.sku} · kalan {available} adet</small></span></label><div className="rr-apr-side">{on ? <input className="rr-in rr-num" aria-label={`${line.title} paket adedi`} type="number" min="1" max={available} step="1" value={selected[line.id]} disabled={saving} onChange={event => setSelected(current => ({ ...current, [line.id]: Math.min(available, Math.max(0, Number(event.target.value) || 0)) }))} /> : <span className="rr-qty-static">{available} Adet</span>}</div></div> })}</div> : <p className="rr-lead">Bu sipariş için paketlenebilir satır bulunamadı.</p>}
      <div className="rr-dgrid">{[['barcode', 'Paket Barkodu', 'text', 128], ['cargoCompany', 'Kargo Firması Kodu', 'text', 100], ['carrier', 'Taşıyıcı', 'text', 100], ['creationReason', 'Oluşturma Nedeni', 'text', 100], ['deci', 'Desi', 'number', 0], ['parcelQuantity', 'Paket Adedi', 'number', 0], ['shippingAddressLabel', 'Gönderim Adresi Etiketi', 'text', 100], ['shippingModel', 'Gönderim Modeli', 'text', 100]].map(([name, label, type, max]) => <label key={name as string} className="rr-fld"><small>{label}</small><input className={`rr-in${type === 'number' ? ' rr-num' : ''}`} name={name as string} type={type as string} {...(type === 'number' ? { min: name === 'deci' ? 0 : 1, max: name === 'deci' ? 10000 : 100, step: 1, defaultValue: 1 } : { maxLength: max as number })} required={type === 'number'} /></label>)}</div>
      {/* The form scrolls inside the window; the refusal is brought into view so it is not missed below the fields. */}
      {error && <div className="rr-callout rr-danger" role="alert" ref={node => node?.scrollIntoView({ block: 'nearest' })}><RrIcon name="alert" /><span>{error}</span></div>}
    </form>
  </RrModal>
}
const shopifyOrderStatuses = ['NEW', 'PROCESSING', 'ON_HOLD', 'READY_TO_SHIP', 'SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'CANCELLED'] as const
type ShopifyOrderCsvImportResult = { fileRows: number; fileOrders: number; matchedOrders: number; updatedOrders: number; updatedLines: number; unmatchedOrders: number; ambiguousOrders: number; unmatchedLines: number; unmatchedOrderNumbers: string[]; issues: string[] }
export function ShopifyOrderCsvImportModal({ connections, onClose, onFeedback }: { connections: Connection[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [connectionId, setConnectionId] = useState(() => connections.length === 1 ? connections[0].id : '')
  const [result, setResult] = useState<ShopifyOrderCsvImportResult | null>(null)
  const [fileError, setFileError] = useState('')
  const upload = useMutation({
    mutationFn: async () => {
      if (!file) throw new Error('Önce Shopify CSV dosyasını seçin.')
      const body = new FormData()
      body.append('file', file)
      if (connectionId) body.append('connectionId', connectionId)
      return hubApi<ShopifyOrderCsvImportResult>('/orders/shopify-csv-import', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body })
    },
    onSuccess: async value => {
      setResult(value)
      onFeedback(`${value.updatedOrders} Shopify siparişi ve ${value.updatedLines} ürün satırı CSV dışa aktarımından güncellendi.`, value.unmatchedOrders || value.issues.length ? 'info' : 'success')
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      await client.invalidateQueries({ queryKey: queryKeys.orders('summary') })
    },
    onError: reason => onFeedback(reason instanceof Error ? reason.message : 'Shopify CSV aktarımı tamamlanamadı.', 'error')
  })

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const selected = event.currentTarget.files?.[0] ?? null
    setResult(null)
    setFileError('')
    if (selected && !selected.name.toLowerCase().endsWith('.csv')) { setFile(null); setFileError('Yalnız .csv uzantılı Shopify sipariş dışa aktarım dosyası kabul edilir.'); return }
    if (selected && selected.size > 10 * 1024 * 1024) { setFile(null); setFileError('Dosya en fazla 10 MiB olabilir.'); return }
    setFile(selected)
  }

  return <RrModal crumb="Shopify" title="Shopify Siparişlerini Aktar" hint="Shopify’dan indirdiğiniz sipariş CSV dosyasını (en fazla 10 MiB) seçin; satırlar sipariş numarasıyla mevcut siparişlere eşlenir. Tutar, alıcı/adres ve ürün satırı bilgileri aktarılır; durum, miktar ve kargo bilgisi korunur, yeni sipariş açılmaz." size="md" locked={upload.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={upload.isPending}>Kapat</button><button type="button" className="rr-btn rr-primary" onClick={() => upload.mutate()} disabled={!file || connections.length === 0 || upload.isPending}>{upload.isPending ? 'Aktarılıyor…' : 'Shopify Siparişlerini Aktar'}</button></>}>
    {connections.length > 1 && <label className="rr-fld" htmlFor="shopify-order-connection"><small>Shopify Bağlantısı</small><PanelSelect id="shopify-order-connection" ariaLabel="Shopify bağlantısı" value={connectionId} onChange={setConnectionId} options={[{ value: '', label: 'Bağlantılar Arasında Eşleştir' }, ...connections.map(connection => ({ value: connection.id, label: `${connection.displayName} · ${connection.externalStoreId}` }))]} /></label>}
    {connections.length === 0 && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span>Shopify bağlantısı görünmüyor. Sipariş içe aktarmak için önce bir Shopify bağlantısı kurun.</span></div>}
    <div className="rr-fld"><small>Shopify Sipariş CSV Dosyası</small><label className="rr-drop"><input type="file" hidden accept=".csv,text/csv" onChange={chooseFile} disabled={upload.isPending} /><RrIcon name="upload" /><b>{file ? file.name : 'Dosya Seç veya Sürükle'}</b><small>{file ? `${(file.size / 1024).toLocaleString('tr-TR', { maximumFractionDigits: 0 })} KB` : 'CSV · en fazla 10 MiB'}</small></label></div>
    {fileError && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{fileError}</span></div>}
    {result && <section className="rr-dsec" aria-live="polite"><header><div className="rr-h3">Aktarım Sonucu</div><p>{result.fileRows} CSV satırı / {result.fileOrders} sipariş · {result.unmatchedOrders} eşleşmeyen · {result.ambiguousOrders} belirsiz · {result.unmatchedLines} eşleşmeyen ürün satırı</p></header><div className="rr-facts"><div><small>Eşleşen Sipariş</small><b className="rr-num">{result.matchedOrders}</b></div><div><small>Güncellenen Sipariş</small><b className="rr-num">{result.updatedOrders}</b></div><div><small>Güncellenen Ürün Satırı</small><b className="rr-num">{result.updatedLines}</b></div></div>{(result.unmatchedOrderNumbers.length > 0 || result.issues.length > 0) && <dl className="rr-kv">{result.unmatchedOrderNumbers.length > 0 && <><dt>Eşleşmeyen Siparişler</dt><dd className="rr-num">{result.unmatchedOrderNumbers.join(', ')}</dd></>}{result.issues.length > 0 && <><dt>Notlar</dt><dd>{result.issues.join(' · ')}</dd></>}</dl>}</section>}
  </RrModal>
}
export function ShopifyOrderStatusModal({ item, onClose, onFeedback }: { item: Order; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<(typeof shopifyOrderStatuses)[number]>(() => shopifyOrderStatuses.includes(item.derivedStatus as (typeof shopifyOrderStatuses)[number]) ? item.derivedStatus as (typeof shopifyOrderStatuses)[number] : 'NEW')
  const update = useMutation({
    mutationFn: () => hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ status }) }),
    onSuccess: async () => {
      const message = `Shopify sipariş durumu “${orderStatusLabel(status)}” olarak güncellendi.`
      onFeedback(message, 'success')
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      onClose()
    },
    onError: reason => onFeedback(reason instanceof Error ? reason.message : 'Shopify sipariş durumu güncellenemedi.', 'error')
  })
  return <RrModal crumb="Shopify" title="Sipariş Durumunu Değiştir" desc={`${displayOrderNumber(item.orderNumber)} · ${customerDisplayName(item)}`} hint="Durum yalnız panelde güncellenir; Shopify’daki durum değişmez. Shopify’dan sonradan gelen durum seçiminizin yerine geçer." size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Kaydediliyor…' : 'Durumu Kaydet'}</button></>}>
    <label className="rr-fld" htmlFor="shopify-order-status"><small>Yeni Sipariş Durumu</small><PanelSelect id="shopify-order-status" ariaLabel="Yeni sipariş durumu" value={status} onChange={setStatus} options={shopifyOrderStatuses.map(value => ({ value, label: titleTr(orderStatusLabel(value)) }))} /></label>
  </RrModal>
}
export function ShopifyBulkStatusModal({ kind, items, onClose, onFeedback, onUpdated }: { kind: 'order' | 'invoice'; items: Order[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler; onUpdated: (orderIds: string[]) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState('')
  const isInvoice = kind === 'invoice'
  const update = useMutation({
    mutationFn: async () => {
      if (!status) throw new Error('Uygulanacak durumu seçin.')
      const results: Array<{ item: Order; ok: boolean; error?: string }> = new Array(items.length)
      let nextIndex = 0
      const worker = async () => {
        while (true) {
          const index = nextIndex++
          if (index >= items.length) return
          const item = items[index]
          try {
            if (item.platformCode !== 'SHOPIFY') throw new Error('Bu işlem yalnızca Shopify siparişlerinde kullanılabilir.')
            if (isInvoice) {
              const order = await hubApi<OrderDetail>(`/orders/${item.id}`)
              if (order.platformCode !== 'SHOPIFY') throw new Error('Sipariş Shopify bağlantısına ait değil.')
              const packageId = orderDisplayShipment(order)?.id
              if (!packageId) throw new Error('Fatura durumu için Shopify sipariş paketi bulunamadı.')
              // in the invoice mode the window offers only PENDING and UPLOADED
              await setManualInvoiceStatus([packageId], status as ManualInvoiceStatus)
            } else {
              await hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': `shopify-order-status:${item.id}:${status}:${newIdempotencyKey()}`, 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ status }) })
            }
            results[index] = { item, ok: true }
          } catch (error) {
            results[index] = { item, ok: false, error: error instanceof Error ? error.message : 'İstek başarısız oldu.' }
          }
        }
      }
      await Promise.all(Array.from({ length: Math.min(4, items.length) }, () => worker()))
      return results
    },
    onSuccess: async results => {
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      const succeeded = results.filter(result => result.ok)
      const failed = results.filter(result => !result.ok)
      onUpdated(succeeded.map(result => result.item.id))
      const statusName = isInvoice
        ? status === 'UPLOADED' ? 'Faturası yüklendi' : 'Fatura bekliyor'
        : orderStatusLabel(status)
      const noun = isInvoice ? 'fatura durumu' : 'sipariş durumu'
      const prefix = `${succeeded.length}/${items.length} Shopify siparişinin ${noun} “${statusName}” olarak güncellendi.`
      const failureDetails = failed.slice(0, 4).map(result => `${displayOrderNumber(result.item.orderNumber)}: ${result.error}`).join('; ')
      const remainingFailures = failed.length > 4 ? `; ${failed.length - 4} sipariş daha` : ''
      onFeedback(failed.length ? `${prefix} ${failed.length} sipariş işlenemedi: ${failureDetails}${remainingFailures}.` : prefix, failed.length === 0 ? 'success' : 'error')
      onClose()
    }
  })
  const validStatus = isInvoice ? status === 'PENDING' || status === 'UPLOADED' : shopifyOrderStatuses.includes(status as (typeof shopifyOrderStatuses)[number])
  return <RrModal crumb="Shopify" title={isInvoice ? 'Toplu Fatura Durumu Değiştir' : 'Toplu Sipariş Durumu Değiştir'} desc={`${items.length} seçili Shopify siparişi`} hint={isInvoice ? `${shopifyInvoiceStatusHint} Shopify dışındaki kayıtlar etkilenmez.` : 'Shopify dışındaki kayıtlar etkilenmez. Bu işlem yalnızca panel sipariş durumunu günceller; Shopify’daki sipariş veya kargo durumu değişmez.'} size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={!validStatus || update.isPending}>{update.isPending ? 'Güncelleniyor…' : `${items.length} Siparişi Güncelle`}</button></>}>
    <label className="rr-fld" htmlFor="order-bulk-status"><small>{isInvoice ? 'Yeni Fatura Durumu' : 'Yeni Sipariş Durumu'}</small><PanelSelect id="order-bulk-status" ariaLabel={isInvoice ? 'Yeni fatura durumu' : 'Yeni sipariş durumu'} value={status} onChange={setStatus} disabled={update.isPending} placeholder="Durum Seçin" options={isInvoice ? [{ value: 'PENDING', label: 'Fatura Bekliyor' }, { value: 'UPLOADED', label: 'Faturası Yüklendi' }] : shopifyOrderStatuses.map(value => ({ value: value as string, label: titleTr(statusLabel(value)) }))} /></label>
    {isInvoice && status === 'UPLOADED' && <div className="rr-callout" role="status"><RrIcon name="info" /><span>{shopifyUploadedNote}</span></div>}
  </RrModal>
}
