// Gönderiler (/shipments, /shipments/:id).
import { Fragment, useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { Busy, ErrorBox, Pagination, UiIcon, useConfirmDialog } from '../../../shared/components'
import { versionedIdempotencyKey } from '../../../shared/idempotency'
import { MessageToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { appendNotification } from '../../../shared/notifications'
import { formatDate } from '../../../shared/format-date'
import { formatMoney } from '../../../shared/format-money'
import { Badge, DateText, Empty, Page, Shipment, notificationKindFromMessage } from '../shared'

type ShipmentDetail = { package: Shipment; allowedActions: string[]; supportedLabelFormats: string[]; isStageConnection: boolean; platformCode: string; documents: Array<{ id: string; documentKind: string; format: string; source: string; documentVersion: number; createdAt: string; expiresAt: string | null }> }
type RemoteCargoCompany = { shortName: string; name: string }
// First controlled n11 write: one stock code at a time, then everything that
// changed. Both go through the regular price-stock job and its n11 task check.
// Every Pazarama package action changes the live store; the dialog says what it will do before it is queued.
function pazaramaActionWarning(action: string) {
  switch (action) {
    case 'PICKING': return 'Sipariş Pazarama’da “Hazırlanıyor (12)” durumuna alınacak. Anlaşmalı kargo süreci başlar; kargoya verilmezse otomatik iade olur ve satıcı puanı düşer.'
    case 'NOT_SUPPLIED': return 'Ürünler “Tedarik Edilemedi” olarak işaretlenecek ve müşteriye para iadesi yapılacak. Bu işlem geri alınamaz.'
    case 'CANCEL_APPROVE': return 'Siparişin karar bekleyen iptal talepleri onaylanacak; müşteriye para iadesi yapılır.'
    case 'CANCEL_REJECT': return 'Siparişin karar bekleyen iptal talepleri reddedilecek; talep Pazarama yönetimine düşer.'
    default: return 'Bu işlem Pazarama’ya gönderilecek.'
  }
}
type PazaramaFinance = { readAt: string; transactions: Array<{ transactionCode: string; status: string; amount: number; commission: number; allowance: number; couponDiscount: number; integrationAmount: number; cargoDebt: number; stoppage: number; installments: number; transactionDate: string | null; transferredDate: string | null }>; commissionInvoices: Array<{ orderNumber: string; invoiceNumber: string | null; invoiceDate: string | null; vatAmount: number; operationStatus: string | null }> }
// Pazarama finance of one order (commission, allowance, transfer date): a live read when asked for, never stored.
function PazaramaFinancePanel({ orderId }: { orderId: string }) {
  const [requested, setRequested] = useState(false)
  const finance = useQuery({ queryKey: ['pazarama-finance', orderId], queryFn: () => hubApi<PazaramaFinance>(`/orders/${orderId}/finance`), enabled: requested, retry: false, staleTime: 60_000 })
  const money = (value: number) => formatMoney(value, 'TRY')
  const day = (value: string | null) => value ? formatDate(value) : '—'
  return <article className="panel">
    <h2>Pazarama finans</h2>
    <p>Komisyon, hakediş ve aktarım tarihi Pazarama’dan o anda okunur; panelde saklanmaz.</p>
    {!requested && <button type="button" onClick={() => setRequested(true)}>Finans bilgisini getir</button>}
    {requested && finance.isLoading && <Busy />}
    {requested && finance.isError && <ErrorBox error={finance.error} onRetry={() => void finance.refetch()} />}
    {finance.data && (finance.data.transactions.length === 0
      ? <p role="status">Pazarama bu sipariş için henüz finans hareketi döndürmedi.</p>
      : <dl className="details">{finance.data.transactions.map(row => <Fragment key={row.transactionCode}>
        <dt>{row.status} · {day(row.transactionDate)}</dt>
        <dd>Tutar {money(row.amount)} · Komisyon {money(row.commission)} · Hakediş {money(row.allowance)} · Aktarım {row.transferredDate ? day(row.transferredDate) : 'bekliyor'}<br /><small>Kargo {money(row.cargoDebt)} · Entegrasyon {money(row.integrationAmount)} · Stopaj {money(row.stoppage)} · Kupon {money(row.couponDiscount)}</small></dd>
      </Fragment>)}</dl>)}
    {finance.data && finance.data.commissionInvoices.length > 0 && <>
      <h3>Komisyon faturaları</h3>
      <ul className="plain-list">{finance.data.commissionInvoices.map(invoice => <li key={invoice.invoiceNumber ?? invoice.orderNumber}><strong>{invoice.invoiceNumber ?? '—'}</strong><span>{invoice.operationStatus ?? ''} · KDV {money(invoice.vatAmount)} · {day(invoice.invoiceDate)}</span></li>)}</ul>
    </>}
  </article>
}
function ListPage({ eyebrow, title, description: _description, children }: { eyebrow: string; title: string; description: string; children: React.ReactNode }) { return <section className="content f3"><div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1></div></div><div className="panel">{children}</div></section> }
export function ShipmentsPage() {
  // The server pages by cursor and does not count; the cursors seen so far let the person step back.
  const [pageSize, setPageSize] = useState(20); const [cursors, setCursors] = useState<Array<string | null>>([null]); const [pageIndex, setPageIndex] = useState(0)
  const cursor = cursors[pageIndex] ?? null
  const query = useQuery({ queryKey: ['shipments', 'page', pageSize, cursor], queryFn: () => hubApi<Page<Shipment>>(`/shipments?${new URLSearchParams({ limit: String(pageSize), ...(cursor ? { after: cursor } : {}) }).toString()}`), placeholderData: keepPreviousData })
  const items = query.data?.items ?? []; const nextCursor = query.data?.hasMore ? query.data.nextCursor : null
  const pageNumber = pageIndex + 1; const knownPages = Math.max(cursors.length, pageNumber + (nextCursor ? 1 : 0))
  function goToPage(target: number) {
    const index = Math.max(0, target - 1)
    if (index <= pageIndex || index < cursors.length) { setPageIndex(Math.min(index, cursors.length - 1)); return }
    if (!nextCursor) return
    setCursors(current => [...current.slice(0, pageIndex + 1), nextCursor]); setPageIndex(pageIndex + 1)
  }
  return <ListPage eyebrow="Operasyon" title="Gönderiler" description="Paket durumu geriye alınmaz; desteklenmeyen işlem ve etiket biçimleri gösterilmez.">{query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} onRetry={() => void query.refetch()} /> : !items.length && pageIndex === 0 ? <Empty>Siparişler alındıkça paketleri burada görünür.</Empty> : <><div className="data-table" role="table">{items.map(item => <Link role="row" to={`/shipments/${item.id}`} key={item.id}><span><strong>{item.orderNumber}</strong><small>Paket {item.externalPackageId}</small></span><Badge value={item.status} /><span>{item.cargoTrackingNumber ?? '—'}</span><span><DateText value={item.statusOccurredAt} /></span></Link>)}</div><div className="order-pagination"><label>Sayfa başına <select aria-label="Sayfa başına gönderi" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setCursors([null]); setPageIndex(0) }}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label><span>Bu sayfada {items.length.toLocaleString('tr-TR')} gönderi</span><Pagination className="pagination-controls" page={pageNumber} totalPages={knownPages} hasNext={Boolean(nextCursor)} onPageChange={goToPage} onPrevious={() => goToPage(pageNumber - 1)} onNext={() => goToPage(pageNumber + 1)} disabled={query.isFetching} /></div></>}</ListPage>
}
export function ShipmentDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selectedCargoShortName, setSelectedCargoShortName] = useState(''); const query = useQuery({ queryKey: ['shipment', id], queryFn: () => hubApi<ShipmentDetail>(`/shipments/${id}`) })
  const carriers = useQuery({ queryKey: ['shipment-carriers', id], queryFn: () => hubApi<RemoteCargoCompany[]>(`/shipments/${id}/changeable-cargo-companies`), enabled: query.data?.platformCode === 'HEPSIBURADA' && query.data.allowedActions.includes('CHANGE_CARGO_PROVIDER'), retry: false })
  useEffect(() => { if (carriers.data?.length && !carriers.data.some(item => item.shortName === selectedCargoShortName)) setSelectedCargoShortName(carriers.data[0].shortName) }, [carriers.data, selectedCargoShortName])
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvShipmentFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const [confirm, confirmDialog] = useConfirmDialog()
  // Every package request goes through one mutation: the buttons wait while one is running, and the key is derived from
  // the package version and the request, so a double click or a resend reaches the marketplace once.
  const packageRequest = useMutation({
    mutationFn: ({ path, body, version }: { path: string; body: string; version: number }) => hubApi(path, { method: 'POST', headers: { 'Idempotency-Key': versionedIdempotencyKey(`shipment${path.slice(path.lastIndexOf('/'))}`, id, version, body), 'If-Match': `"v${version}"` }, body }),
  })
  const busy = packageRequest.isPending
  // Pending state reaches the buttons on the next render; this guard also stops a second send within the same moment.
  const requestInFlight = useRef(false)
  async function sendPackageRequest(path: string, payload: unknown, success: string, failure: string, refresh: unknown[][] = [['shipment', id]]) {
    if (!query.data || requestInFlight.current) return
    requestInFlight.current = true
    try {
      await packageRequest.mutateAsync({ path, body: JSON.stringify(payload), version: query.data.package.version })
      setNotice(success)
      await Promise.all(refresh.map(queryKey => client.invalidateQueries({ queryKey })))
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : failure) }
    finally { requestInFlight.current = false }
  }
  async function action(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!query.data || busy) return
    const data = new FormData(event.currentTarget)
    const actionName = String(data.get('action') ?? '')
    const payloadJson = String(data.get('payloadJson') || '{}')
    try { JSON.parse(payloadJson) } catch { setNotice('İşlem ayrıntıları geçerli JSON değil. Örneklerdeki biçimi kullanın.'); return }
    const n11Cancels = query.data.platformCode === 'N11' && actionName === 'SPLIT_BY_QUANTITY' && payloadJson.includes('"cancelledItems"')
    if (query.data.platformCode === 'N11' && !(await confirm(n11Cancels
      ? { title: 'Ürünler İptal Edilsin mi?', message: 'Bu işlem N11’de ürünleri iptal eder ve geri alınamaz.', confirmLabel: 'Ürünleri İptal Et', destructive: true }
      : { title: 'N11’e Gönderilsin mi?', message: 'Bu işlem N11’e gönderilecek.', confirmLabel: 'Gönder' }))) return
    // Trendyol actions that cannot be taken back ask first (audit 2026-10-20 S7).
    const trendyolDestructive: Record<string, { title: string; message: string; confirmLabel: string }> = {
      CANCEL_ITEMS: { title: 'Ürünler İptal Edilsin mi?', message: 'Seçilen ürünler Trendyol’da iptal edilir; bu işlem geri alınamaz.', confirmLabel: 'Ürünleri İptal Et' },
      MANUAL_DELIVER: { title: 'Teslim Edildi Olarak İşaretlensin mi?', message: 'Paket Trendyol’da teslim edildi olarak işaretlenir; bu işlem geri alınamaz.', confirmLabel: 'Teslim Edildi İşaretle' },
      MANUAL_RETURN: { title: 'İade Edildi Olarak İşaretlensin mi?', message: 'Paket Trendyol’da iade edildi olarak işaretlenir; bu işlem geri alınamaz.', confirmLabel: 'İade Edildi İşaretle' }
    }
    const trendyolConfirm = query.data.platformCode === 'TRENDYOL' ? trendyolDestructive[actionName] : undefined
    if (trendyolConfirm && !(await confirm({ ...trendyolConfirm, destructive: true }))) return
    if (query.data.platformCode === 'PAZARAMA' && !(await confirm({ title: 'Pazarama’ya Gönderilsin mi?', message: pazaramaActionWarning(actionName), confirmLabel: 'Gönder', destructive: actionName === 'NOT_SUPPLIED' }))) return
    await sendPackageRequest(`/shipments/${id}/actions`, { action: actionName, payloadJson }, 'Paket işlemi sıraya alındı; sonucu pazaryerinden yeniden okunarak doğrulanacak.', 'İşlem başlatılamadı.')
  }
  async function hbAction(actionName: 'UNPACK' | 'CHANGE_CARGO_PROVIDER', payloadJson = '{}') {
    if (actionName === 'UNPACK' && !(await confirm({ title: 'Paket Açılsın mı?', message: 'Bu paket Hepsiburada’da açılacak; kalemler yeniden paketlenmeyi bekler.', confirmLabel: 'Paketi Aç', destructive: true }))) return
    await sendPackageRequest(`/shipments/${id}/actions`, { action: actionName, payloadJson }, `${actionName === 'UNPACK' ? 'Paket açma' : 'Kargo değişikliği'} sıraya alındı; Hepsiburada’nın yanıtı sipariş yeniden okunarak doğrulanacak.`, 'Paket işlemi başlatılamadı.', [['shipment', id], ['shipment-carriers', id]])
  }
  async function label(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!query.data) return
    const data = new FormData(event.currentTarget)
    const hepsiburada = query.data.platformCode === 'HEPSIBURADA'
    const requestedFormat = hepsiburada ? String(data.get('format') ?? 'ZPL').toUpperCase() : undefined
    await sendPackageRequest(`/shipments/${id}/common-label-jobs`, { boxQuantity: hepsiburada ? 1 : Number(data.get('boxQuantity')), volumetricHeight: hepsiburada ? 0 : Number(data.get('volumetricHeight')), ...(requestedFormat ? { format: requestedFormat } : {}) }, hepsiburada ? `Hepsiburada ${requestedFormat} etiketi isteği sıraya alındı.` : 'Kargo etiketi isteği sıraya alındı.', 'Etiket isteği başlatılamadı.')
  }
  async function labelProbe(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!query.data) return
    const data = new FormData(event.currentTarget)
    await sendPackageRequest(`/shipments/${id}/label-capability-probes`, { capabilityCode: data.get('capabilityCode'), boxQuantity: Number(data.get('boxQuantity')), volumetricHeight: Number(data.get('volumetricHeight')) }, 'Test ortamı etiket denemesi sıraya alındı; sonucu İşlem Takibi’nde görünür.', 'Test ortamı etiket denemesi başlatılamadı.')
  }
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} onRetry={() => void query.refetch()} /></section>; const item = query.data
  const hasLabelTarget = item.platformCode === 'HEPSIBURADA' ? Boolean(item.package.externalPackageId) : Boolean(item.package.cargoTrackingNumber)
  return <section className="content f3">{confirmDialog}<Link to="/shipments" className="back"><UiIcon name="arrowLeft" /> Gönderiler</Link><div className="page-heading"><div><p className="eyebrow">{item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada gönderisi' : 'Gönderi detayı'}</p><h1>{item.package.orderNumber}</h1><p className="lede">Paket {item.package.externalPackageId}</p></div><Badge value={item.package.status} /></div>{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}<div className="split"><article className="panel"><h2>Paket</h2><dl className="details"><dt>Platform durumu</dt><dd>{statusLabel(item.package.rawStatus)}</dd><dt>Kargo firması</dt><dd>{item.package.cargoProviderName ?? '—'}</dd><dt>Takip numarası</dt><dd>{item.package.cargoTrackingNumber ?? '—'}</dd><dt>Güncelleme</dt><dd><DateText value={item.package.statusOccurredAt} /></dd></dl><h3>Belgeler</h3>{item.documents.length ? <ul className="plain-list">{item.documents.map(document => <li key={document.id}><strong>{document.documentKind} · {document.format}</strong><span>v{document.documentVersion} · {document.source}</span><a href={`/api/v1/shipments/${item.package.id}/documents/${document.id}/content`} download>Belgeyi indir</a></li>)}</ul> : <p>Henüz belge yok.</p>}</article><article className="panel"><h2>{item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada paket işlemleri' : item.platformCode === 'N11' ? 'N11 paket aksiyonu' : item.platformCode === 'PAZARAMA' ? 'Pazarama paket aksiyonu' : 'Trendyol paket aksiyonu'}</h2>{item.platformCode === 'HEPSIBURADA' ? <>{item.allowedActions.includes('CHANGE_CARGO_PROVIDER') && <form className="inline-form" onSubmit={event => { event.preventDefault(); void hbAction('CHANGE_CARGO_PROVIDER', JSON.stringify({ CargoCompanyShortName: selectedCargoShortName })) }}><label>Bu pakete izin verilen kargo firması<select value={selectedCargoShortName} onChange={event => setSelectedCargoShortName(event.target.value)} disabled={carriers.isLoading || !carriers.data?.length}><option value="">Kargo firması seçin</option>{(carriers.data ?? []).map(carrier => <option key={carrier.shortName} value={carrier.shortName}>{carrier.name} · {carrier.shortName}</option>)}</select></label><button type="submit" disabled={busy || !selectedCargoShortName || carriers.isLoading}>Kargo Firmasını Değiştir</button></form>}{carriers.isError && <ErrorBox error={carriers.error} onRetry={() => void carriers.refetch()} />}{carriers.isSuccess && !carriers.data.length && <p role="status">Hepsiburada bu paket için değiştirilebilir kargo firması döndürmedi.</p>}{item.allowedActions.includes('UNPACK') && <button type="button" className="secondary" disabled={busy} onClick={() => void hbAction('UNPACK')}>Paketi Aç</button>}{!item.allowedActions.length && <div className="unknown"><strong>Bu pakette yapılabilecek işlem yok</strong><p>Bağlantının yazma izni ya da paketin durumu bu işleme şu anda izin vermiyor.</p></div>}<p className="notice">Kargo seçeneği Hepsiburada’nın bu paket için döndürdüğü izinli firmalardan gelir.</p></> : item.allowedActions.length ? <form className="form-grid" onSubmit={action}><label>İşlem<select name="action">{item.allowedActions.map(value => <option key={value} value={value}>{statusLabel(value)}</option>)}</select></label><label>İşlem Ayrıntıları (JSON)<textarea name="payloadJson" defaultValue="{}" required /></label>{item.platformCode === 'PAZARAMA' && <small className="n11-action-hint">Pazarama — İşleme alma ve tedarik edilemedi için {'{}'} yeterli (paketteki tüm kalemler). Paket böl / güncelle: {'{"packages":[{"orderLineIds":["kalem-guid"],"sellerAddressId":null,"cargoCompanyId":null}]}'} · İşçilik: {'{"items":[{"orderLineId":"kalem-guid","laborCost":100,"processCount":1}]}'} · İptal talebi kararı için {'{}'} (siparişin karar bekleyen talepleri). Anlaşmalı kargoda 12’den sonra süreç Pazarama’da kendiliğinden ilerler.</small>}{item.platformCode === 'N11' && <small className="n11-action-hint">N11 örnekleri — Miktarla böl / iptal: {'{"splitPackages":[{"packageDetails":[{"orderLineId":1,"quantities":1}]}],"cancelledItems":[{"cancelReasonId":61,"orderLineId":1,"quantity":1}]}'} · İşçilik: {'{"laborCostDetails":[{"orderLineId":1,"totalLaborCostExcludingVAT":100,"laborVatRate":20}]}'} · Toplama talebi (HLZ/CEVA/BL): {'{"collectionRequestDetails":[{"id":"paketKodu","orderLineId":1,"boxQuantity":1,"desi":10,"shipmentCompany":"HLZ"}]}'}. İptal yalnız bölmeyle birlikte yapılır; geri alınamaz.</small>}<button type="submit" disabled={busy}>{busy ? 'Gönderiliyor…' : 'İşlemi Başlat'}</button></form> : <div className="unknown"><strong>Bu pakette yapılabilecek işlem yok</strong><p>Paketin durumu ya da canlı bağlantının yazma ayarı bu işleme şu anda izin vermiyor.</p></div>}<h2>Ortak kargo etiketi</h2>{hasLabelTarget ? <>{item.supportedLabelFormats.length > 0 && <form className="inline-form" onSubmit={label}>{item.platformCode === 'HEPSIBURADA' && <label>Etiket biçimi<select name="format" defaultValue={item.supportedLabelFormats.includes('ZPL') ? 'ZPL' : item.supportedLabelFormats[0]}>{item.supportedLabelFormats.map(format => <option key={format} value={format}>{format === 'BASE64ZPL' ? 'Base64 ZPL' : format}</option>)}</select></label>}{item.platformCode !== 'HEPSIBURADA' && <><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0" max="10000" step="0.01" defaultValue="1" required /></label></>}<button type="submit" disabled={busy}>{item.platformCode === 'HEPSIBURADA' ? 'Etiketi Al' : 'Etiket Oluştur'}</button></form>}{item.isStageConnection && item.platformCode === 'TRENDYOL' && <details className="operation-details"><summary>Stage etiket testi</summary><form className="inline-form" onSubmit={labelProbe}><p>Yalnız test bağlantısındaki uygun pakette etiket okuma veya oluşturma sonucunu doğrular.</p><label>Test türü<select name="capabilityCode"><option value="LABEL_READ">Etiketi oku</option><option value="LABEL_WRITE">Etiket oluşturmayı dene</option></select></label><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0.01" max="10000" step="0.01" required /></label><button type="submit" disabled={busy}>Test Etiketi Dene</button></form></details>}</> : <p>Etiket için paket kimliği gerekir.</p>}</article>{item.platformCode === 'PAZARAMA' && <PazaramaFinancePanel orderId={item.package.orderId} />}</div></section>
}
