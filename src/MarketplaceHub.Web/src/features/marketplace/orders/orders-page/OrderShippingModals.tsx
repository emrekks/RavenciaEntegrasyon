import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { useEffect, useState, type CSSProperties, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../../shared/api'
import { cargoCarrier, cargoCarriers, cargoLabel, cargoMatches } from '../../../../shared/components'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../../shared/idempotency'
import { RrBadge, RrError, RrIcon, RrLoading, RrLogo, RrModal } from '../../../../shared/ui/rr-components'
import { loadPrintedShippingLabels, shippingLabelAddressLines, shippingLabelBlockCatalog, shippingLabelPageCss, shippingLabelPages, shippingLabelSizeMm, printedShippingLabelKey, ShippingLabelFit, ShippingLabelSurface, type ShippingLabelFormat, type ShippingLabelSettings, type ShippingLabelValues } from '../../../shipping'
import { marketplacePlatformLabel } from '../../marketplace-platform-support'
import { formatPackageLabel } from '../../../../shared/package-label'
import { labelPersonalData, loadOrderLabelDetails, type OrderLabelDetail } from '../shipping-label-data'
import { recordShippingLabelsPrinted } from '../label-printed'
import { Connection, Order, OrderAddress, Shipment, displayOrderNumber, orderDisplayShipment } from '../../shared'
import { queryKeys } from '../../../../shared/api/query-keys'
import { isPendingAction, addressDataText, customerDisplayName, canChangeOrderCargo, RrHintCheck } from './order-page-helpers'

function listAddressText(address: OrderAddress | null | undefined, platformCode = 'TRENDYOL') { return addressDataText((address ?? {}) as Record<string, unknown>, platformCode) }

export function CourierChangeModal({ item, items, onClose, onConfirmed }: { item: Order; items?: Order[]; onClose: () => void; onConfirmed: (shipments: Shipment[]) => void }) {
  // Packages of other marketplaces are left out before anything is sent.
  const targets = (items?.length ? items : [item]).filter(canChangeOrderCargo).flatMap(order => { const shipment = orderDisplayShipment(order); return shipment ? [{ order, shipment }] : [] })
  const shipment = orderDisplayShipment(item)
  const currentCarrier = cargoCarriers.find(carrier => cargoMatches(shipment?.cargoProviderName ?? item.cargoProviderName, carrier))?.label ?? cargoCarriers[0].label
  const [selectedCarrier, setSelectedCarrier] = useState<string>(currentCarrier)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMsg, setErrorMsg] = useState('')
  const [progressMsg, setProgressMsg] = useState('')
  const client = useQueryClient()

  async function handleCarrierChange() {
    if (!targets.length) {
      setErrorMsg('Paket kaydı bulunamadı.')
      return
    }
    const chosen = cargoCarriers.find(c => c.label === selectedCarrier)
    if (!chosen) return
    setIsSubmitting(true)
    setErrorMsg('')
    setProgressMsg('Kargo firması Trendyol’a anlık olarak gönderiliyor…')
    // Every package is tried; one refused package does not stop the others, and the result says
    // exactly which changed, which are still waiting for Trendyol and which were refused.
    const updatedShipments: Shipment[] = []
    const pending: string[] = []
    const failed: Array<{ orderNumber: string; message: string }> = []
    try {
      for (const target of targets) {
        try {
          const updatedShipment = await sendWithVersionedIdempotencyKey(`instant-cargo:${target.shipment.id}:v${target.shipment.version}:${chosen.code}`, key => hubApi<ApiAnswer<'postShipmentsByIdInstantCargoProvider'>>(`/shipments/${target.shipment.id}/instant-cargo-provider`, {
            method: 'POST',
            headers: { 'Idempotency-Key': key, 'If-Match': `"v${target.shipment.version}"` },
            body: JSON.stringify({ action: 'CHANGE_CARGO_PROVIDER', payloadJson: JSON.stringify({ cargoProvider: chosen.code }) })
          }))
          updatedShipments.push(updatedShipment)
        } catch (err) {
          if (isPendingAction(err)) pending.push(err.message)
          else failed.push({ orderNumber: displayOrderNumber(target.order.orderNumber), message: err instanceof Error ? err.message : 'Bilinmeyen hata.' })
        }
      }
      if (updatedShipments.length) onConfirmed(updatedShipments)
      void client.invalidateQueries({ queryKey: queryKeys.orders() })
      if (!failed.length && !pending.length) { onClose(); return }
      const changed = updatedShipments.length ? `${updatedShipments.length} paket güncellendi. ` : ''
      if (failed.length) {
        const first = failed[0]
        setErrorMsg(targets.length === 1
          ? `Kargo firması güncellenemedi: ${first.message}`
          : `${changed}${failed.length} paket güncellenemedi (ilki ${first.orderNumber}: ${first.message})${pending.length ? `; ${pending.length} paket Trendyol yanıtını bekliyor` : ''}.`)
      } else setProgressMsg(targets.length === 1 ? pending[0] : `${changed}${pending.length} paket Trendyol yanıtını bekliyor; sonuç geldiğinde panel güncellenir.`)
    } finally {
      setIsSubmitting(false)
    }
  }

  const currentLabel = cargoCarriers.find(carrier => cargoMatches(shipment?.cargoProviderName ?? item.cargoProviderName, carrier))?.label
  return <RrModal crumb={targets.length > 1 ? `${targets.length} seçili paket` : displayOrderNumber(item.orderNumber)} title="Paketi Hangi Firma ile Göndermek İstiyorsunuz?" hint="İstek doğrudan Trendyol’a gönderilir; onay gelirse panel anında güncellenir." size="lg" locked={isSubmitting} onClose={onClose} foot={<><span className="rr-note">Seçili: <b>{selectedCarrier}</b></span><button type="button" className="rr-btn" onClick={onClose} disabled={isSubmitting}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={isSubmitting || !targets.length} onClick={handleCarrierChange}>{isSubmitting ? 'İşleniyor…' : 'İşlemi Yap'}</button></>}>
    {targets.length > 1 && <div className="rr-callout"><RrIcon name="info" /><span><b>{targets.length} paket değiştirilecek.</b> Seçtiğiniz kargo firması uygun tüm seçili paketlere uygulanır.</span></div>}
    <div className="rr-h3">Standart Kargo Firmaları (0–30 Desi)</div>
    <div className="rr-cgrid" role="radiogroup">{cargoCarriers.map(carrier => { const icon = cargoCarrier(carrier.label)?.iconUrl; return <label key={carrier.code} className={`rr-ccard${selectedCarrier === carrier.label ? ' rr-on' : ''}`}><input type="radio" name="courier" value={carrier.label} checked={selectedCarrier === carrier.label} onChange={() => setSelectedCarrier(carrier.label)} /><span className="rr-ci">{icon ? <img className="rr-cargo-img" src={icon} alt="" referrerPolicy="no-referrer" /> : <span className="rr-cmono">{carrier.label.split(' ').map(word => word[0]).join('').slice(0, 2).toLocaleUpperCase('tr-TR')}</span>}</span><b>{carrier.label}</b>{carrier.label === currentLabel && <small>Şu anki firma</small>}</label> })}</div>
    {progressMsg && !errorMsg && <div className="rr-callout" role="status"><RrIcon name="clock" size={16} /><span>{progressMsg}</span></div>}
    {errorMsg && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{errorMsg}</span></div>}
  </RrModal>
}
export function ShippingLabelModal({ item, settings, format, onClose, onPrinted, inline = false, detail: suppliedDetail }: { item: Order; settings: ShippingLabelSettings; format?: ShippingLabelFormat; onClose: () => void; onPrinted?: () => void; inline?: boolean; detail?: OrderLabelDetail }) {
  format = format === 'sticker' ? 'sticker' : settings.defaultFormat
  const client = useQueryClient()
  const [overflowing, setOverflowing] = useState<string[]>([])
  // The buyer's e-mail (and name and address) are read from the order detail when the label is printed.
  const detailQuery = useQuery({ queryKey: queryKeys.orderLabelDetail(item.id), queryFn: ({ signal }) => hubApi<ApiAnswer<'getOrdersById'>>(`/orders/${item.id}`, { signal }), enabled: !inline && !suppliedDetail, staleTime: 30_000 })
  const detail = suppliedDetail ?? detailQuery.data
  const personal = labelPersonalData(item, detail)
  const shipment = orderDisplayShipment(item)
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? ''
  const packageNumber = formatPackageLabel(shipment?.externalPackageId) ?? ''
  const values: ShippingLabelValues = {
    trackingNumber,
    packageNumber: packageNumber || '—',
    orderNumber: displayOrderNumber(item.orderNumber),
    customerName: customerDisplayName({ ...item, customerName: personal.customerName, shipmentAddress: personal.shipmentAddress }),
    // One part a line (street, neighbourhood, district / city), as the label prints it.
    address: shippingLabelAddressLines(personal.shipmentAddress, listAddressText(personal.shipmentAddress, item.platformCode)).join('\n'),
    cargoProvider: cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName),
    senderName: settings.senderName,
    senderAddress: settings.senderAddress.split(/\r?\n| · /).join('\n'),
    customerEmail: personal.customerEmail
  }
  // A package without its own number scans as the order number, as before.
  const barcodes = { trackingNumber, packageNumber: packageNumber || item.orderNumber }
  const labelSurface = <ShippingLabelSurface settings={settings} format={format} values={values} barcodes={barcodes} onOverflow={inline ? undefined : setOverflowing} />
  if (inline) return labelSurface
  const detailReady = Boolean(detail)
  const size = shippingLabelSizeMm(format, settings)
  const blockNames = overflowing.map(id => settings.layout[format].find(block => block.id === id)).filter(block => block != null).map(block => shippingLabelBlockCatalog.find(entry => entry.kind === block.kind && block.kind !== 'custom')?.label ?? block.title)
  return <RrModal crumb="Yazdırma Önizlemesi" title={format === 'a4' ? 'A4 Kargo Etiketi' : 'Sticker Kargo Etiketi'} desc={`${displayOrderNumber(item.orderNumber)} · ${cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)} · ${size.width} × ${size.height} mm`} size="md" layerClass="shipping-label-backdrop" hint="Önizleme, yazıcıya giden etiketin aynısıdır; ekrana sığması için küçültülür." modalClass={`shipping-label-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={!detailReady} onClick={() => { window.print(); void recordShippingLabelsPrinted([item.id], format, () => onPrinted?.(), client) }}><RrIcon name="print" />Yazdır</button></>}>
    <style>{shippingLabelPageCss(format, settings)}</style>
    {blockNames.length > 0 && <div className="rr-callout rr-screen-only" role="status"><RrIcon name="alert" /><span><b>Sığmayan Yazı:</b> {blockNames.join(', ')} bloğunda yazının bir kısmı kesiliyor. Sistem Ayarları › Etiket Tasarımı’ndan bloğu büyütün ya da yazı boyutunu küçültün.</span></div>}
    {detailQuery.isError && !detailReady ? <RrError error={detailQuery.error} onRetry={() => void detailQuery.refetch()} /> : !detailReady ? <RrLoading text="Alıcı bilgileri yükleniyor…" /> : <div className="shipping-label-preview-wrap rr-label-wrap"><ShippingLabelFit widthMm={size.width} heightMm={size.height} maxHeight={Math.max(320, window.innerHeight - 280)}>{labelSurface}</ShippingLabelFit></div>}
  </RrModal>
}
export function ShippingLabelBatchModal({ items, settings, format, onClose, onPrinted }: { items: Order[]; settings: ShippingLabelSettings; format: ShippingLabelFormat; onClose: () => void; onPrinted: () => void }) {
  const client = useQueryClient()
  // Each selected order's detail is read (a few at a time) for the personal fields the labels print.
  const ids = items.map(item => item.id)
  const batchPages = shippingLabelPages(items, format, settings)
  // WEB-ALTYAPI-5: the server's first-print time (labelPrintedAt) counts on every computer; this device's list, read once
  // for the window, covers a print whose server call failed (label-printed.ts).
  const printedHere = new Set(loadPrintedShippingLabels())
  const details = useQuery({ queryKey: queryKeys.orderLabelDetails(ids), queryFn: ({ signal }) => loadOrderLabelDetails(ids, id => hubApi<ApiAnswer<'getOrdersById'>>(`/orders/${id}`, { signal })), staleTime: 30_000 })
  function printLabels() {
    if (!details.data) return
    window.print()
    void recordShippingLabelsPrinted(items.map(item => item.id), format, onPrinted, client)
  }
  return <RrModal crumb="Toplu Yazdırma" title="Kargo Stickerlarını Yazdır" desc={`${items.length} etiket · Takip numarası bulunan seçili paketler`} size="md" layerClass="shipping-label-backdrop" modalClass={`shipping-label-modal shipping-label-batch-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note">{details.data ? `${items.length} etiket yazdırılmaya hazır.` : details.isError ? 'Alıcı bilgileri alınamadı.' : 'Alıcı bilgileri yükleniyor…'}</span><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={!details.data} onClick={printLabels}><RrIcon name="print" />Yazdır</button></>}>
    <style>{shippingLabelPageCss(format, settings)}</style>
    {details.isError && <RrError error={details.error} onRetry={() => void details.refetch()} />}
    <div className="rr-sum rr-screen-only">{items.map(item => { const shipment = orderDisplayShipment(item); return <div key={item.id}><RrLogo code={item.platformCode} /><span>{displayOrderNumber(item.orderNumber)} · {customerDisplayName(item)}<small>{cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)} · {shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? '—'}</small></span><RrBadge label={item.labelPrintedAt || (['a4', 'sticker'] as ShippingLabelFormat[]).some(value => printedHere.has(printedShippingLabelKey(item.id, value))) ? 'Yazdırıldı' : 'Bekliyor'} glyph={undefined} /></div> })}</div>
    {details.data && <div className="shipping-label-batch-preview rr-print-only">{batchPages.pages.map((page, index) => <div key={index} className="shipping-label-page" style={{ '--labels-across': batchPages.across } as CSSProperties}>{page.map(item => <ShippingLabelModal key={item.id} item={item} settings={settings} format={format} onClose={onClose} inline detail={details.data.get(item.id)} />)}</div>)}</div>}
  </RrModal>
}
export function SingleOrderSyncModal({ activeConnection: activeConnections, onClose, onSuccess }: { activeConnection: Connection[]; onClose: () => void; onSuccess: (connectionCount: number, orderNumber: string) => void }) {
  const [orderNumber, setOrderNumber] = useState('')
  const [packageNumber, setPackageNumber] = useState('')
  const [selectedConnectionIds, setSelectedConnectionIds] = useState<string[]>(() => activeConnections.map(connection => connection.id))
  const [connectionsInitialized, setConnectionsInitialized] = useState(() => activeConnections.length > 0)
  const [syncMode, setSyncMode] = useState<'changes' | 'single'>('changes')
  const [fullScan, setFullScan] = useState(false)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMsg, setErrorMsg] = useState('')
  const selectedSyncConnection = selectedConnectionIds.length === 1
    ? activeConnections.find(connection => connection.id === selectedConnectionIds[0])
    : undefined
  const supportsPackageLookup = selectedSyncConnection?.platformCode === 'TRENDYOL' || selectedSyncConnection?.platformCode === 'HEPSIBURADA'

  useEffect(() => {
    if (connectionsInitialized || !activeConnections.length) return
    setSelectedConnectionIds(activeConnections.map(connection => connection.id))
    setConnectionsInitialized(true)
  }, [activeConnections, connectionsInitialized])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    const trimmed = orderNumber.trim()
    const selectedConnections = activeConnections.filter(connection => selectedConnectionIds.includes(connection.id))
    if (syncMode === 'single' && !trimmed) {
      setErrorMsg('Tekil sipariş çekmek için sipariş numarası girin.')
      return
    }
    if (!selectedConnections.length) {
      setErrorMsg('En az bir aktif bağlantı seçin.')
      return
    }
    const packageLookup = syncMode === 'single'
      && selectedConnections.length === 1
      && ['TRENDYOL', 'HEPSIBURADA'].includes(selectedConnections[0].platformCode)
      && Boolean(packageNumber.trim())
    setIsSubmitting(true)
    setErrorMsg('')
    try {
      await Promise.all(selectedConnections.map(connection => hubApi(`/connections/${connection.id}/order-sync-jobs`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify({ externalOrderId: syncMode === 'single' ? trimmed : null, full: syncMode === 'changes' && fullScan, packageNumber: packageLookup ? packageNumber.trim() : null })
      })))
      onSuccess(selectedConnections.length, trimmed)
      onClose()
    } catch (err) {
      setErrorMsg(err instanceof Error ? err.message : 'Sipariş senkronizasyonu başlatılamadı. Seçili bağlantıların durumunu kontrol edin.')
    } finally {
      setIsSubmitting(false)
    }
  }


  return <RrModal crumb="Sipariş Kaynakları" title="Sipariş Senkronizasyonu"size="md" locked={isSubmitting} onClose={onClose} foot={<><span className="rr-note">{selectedConnectionIds.length} bağlantı seçili</span><button type="button" className="rr-btn" onClick={onClose} disabled={isSubmitting}>İptal</button><button type="submit" form="order-sync-form" className="rr-btn rr-primary" disabled={isSubmitting || !selectedConnectionIds.length}><RrIcon name="refresh" />{isSubmitting ? 'Senkronize Ediliyor…' : syncMode === 'single' ? 'Tekil Siparişi Çek' : 'Siparişleri Çek'}</button></>}>
    <form id="order-sync-form" style={{ display: 'contents' }} onSubmit={handleSubmit}>
      {!activeConnections.length && <div className="rr-callout rr-warn" role="alert"><RrIcon name="alert" /><span>Aktif Trendyol, Shopify, Hepsiburada veya N11 bağlantısı bulunamadı. Entegrasyonlar sayfasından bağlantınızı etkinleştirin.</span></div>}
      <div className="rr-h3">Sipariş Kaynakları</div>
      <div className="rr-srcs">{activeConnections.map(connection => { const on = selectedConnectionIds.includes(connection.id); return <label key={connection.id} className={`rr-src${on ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={on} onChange={event => setSelectedConnectionIds(current => event.target.checked ? [...current, connection.id] : current.filter(id => id !== connection.id))} /><RrLogo code={connection.platformCode} /><span><b>{connection.displayName}</b><small>{marketplacePlatformLabel(connection.platformCode)} · {connection.environment} · Mağaza {connection.externalStoreId}</small></span></label> })}</div>
      <div className="rr-seg" role="group" aria-label="Senkronizasyon türü"><button type="button" className={syncMode === 'changes' ? 'rr-on' : undefined} onClick={() => { setSyncMode('changes'); setErrorMsg('') }} disabled={isSubmitting}>Yeni Siparişleri Çek</button><button type="button" className={syncMode === 'single' ? 'rr-on' : undefined} onClick={() => { setSyncMode('single'); setErrorMsg('') }} disabled={isSubmitting}>Tekil Sipariş Çek</button></div>
      {syncMode === 'single' ? <div className="rr-dgrid"><label className="rr-fld"><small>Pazar Yeri Sipariş Numarası</small><input className="rr-in" type="text" value={orderNumber} onChange={e => setOrderNumber(e.target.value)} placeholder="Örn. 1010000001 veya Shopify sipariş adı" disabled={isSubmitting} data-autofocus /></label>{selectedSyncConnection && supportsPackageLookup && <label className="rr-fld"><small>Paket Numarası (İsteğe Bağlı)</small><input className="rr-in" type="text" value={packageNumber} onChange={e => setPackageNumber(e.target.value)} placeholder={selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Örn. 419000001' : 'Örn. 5430000003'} disabled={isSubmitting} /><em>{selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Sipariş numarası bulunamazsa bu paket numarasıyla salt okunur olarak okunur.' : 'Eski teslimat durumunu salt okunur olarak yeniler.'}</em></label>}</div> : <RrHintCheck checked={fullScan} onChange={setFullScan} label="Erişilebilir tüm siparişleri tara" hint="Kapalıyken yalnız yeni değişiklikler ve güncellemeler alınır." />}
      {errorMsg && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{errorMsg}</span></div>}
    </form>
  </RrModal>
}
