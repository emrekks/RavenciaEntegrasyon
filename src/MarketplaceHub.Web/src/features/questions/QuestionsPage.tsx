import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { Badge, Button, EmptyState, LoadingState, Modal, PageHeader, Tabs, UiIcon } from '../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../shared/platform-logos'
import { questionDeadline } from './question-time'

type QuestionConversation = { author: string; text: string; createdAt: string; rejectionReason?: string | null }
type MarketplaceQuestion = {
  id: string; connectionId: string; platformCode: string; storeName: string; externalQuestionId: string; kind: string; status: string
  questionText: string; productName?: string | null; productImageUrl?: string | null; productSku?: string | null; productBarcode?: string | null
  productModelCode?: string | null; customerName?: string | null; externalOrderNumber?: string | null; conversations: QuestionConversation[]
  createdAt: string; expiresAt?: string | null; lastRemoteModifiedAt: string; lastSyncedAt: string; version: number
}
type Template = { id: string; title: string; text: string; version: number; updatedAt: string }
type Connection = { id: string; platformCode: string; displayName: string; status: string }
type SyncState = { connectionId: string; platformCode: string; storeName: string; historyImported: boolean; historyStartedAt?: string | null; progressStatus: string; importedCount: number; lastRunStartedAt?: string | null; lastSuccessAt?: string | null; lastError?: string | null }
type QuestionListPage = { items: MarketplaceQuestion[]; page: number; limit: number; totalCount: number }
type QuestionStatus = 'WAITING' | 'ANSWERED' | 'EXPIRED' | 'OTHER' | 'ALL'
type WorkspaceTab = 'PRODUCT' | 'ORDER' | 'TEMPLATES'

const statusFilters: Array<{ value: QuestionStatus; label: string }> = [
  { value: 'WAITING', label: 'Cevap bekleyenler' }, { value: 'ANSWERED', label: 'Cevaplananlar' },
  { value: 'EXPIRED', label: 'Süresi dolanlar' }, { value: 'OTHER', label: 'Diğer durumlar' }, { value: 'ALL', label: 'Tümü' },
]

function questionStatus(status: string) {
  const normalized = status.toUpperCase()
  if (normalized === 'WAITING_FOR_ANSWER') return { label: 'Cevap bekliyor', tone: 'warning' as const }
  if (normalized === 'ANSWER_SUBMITTED') return { label: 'Cevap gönderildi · doğrulanıyor', tone: 'info' as const }
  if (normalized === 'ANSWERED') return { label: 'Cevaplandı', tone: 'success' as const }
  if (normalized === 'EXPIRED' || normalized === 'UNANSWERED' || normalized === 'AUTOCLOSED') return { label: 'Süresi doldu', tone: 'neutral' as const }
  if (normalized === 'REJECTED') return { label: 'Reddedildi', tone: 'danger' as const }
  if (normalized === 'REPORTED') return { label: 'İnceleniyor', tone: 'info' as const }
  return { label: status, tone: 'neutral' as const }
}

function formatDate(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date).replace(' ', ' · ')
}

function displayPlatform(code: string) { return code === 'HEPSIBURADA' ? 'Hepsiburada' : 'Trendyol' }

type ProductImageSearch = { items: Array<{ title: string; primaryImageUrl: string | null; familyMediaUrls?: string[]; variants: Array<{ sku: string; barcode?: string | null; modelCode?: string | null }> }> }

function QuestionProductImage({ src, sku, barcode, modelCode, productName }: { src?: string | null; sku?: string | null; barcode?: string | null; modelCode?: string | null; productName: string }) {
  const searchTerm = sku || barcode || modelCode || ''
  const [sourceFailed, setSourceFailed] = useState(!src)
  const [fallbackFailed, setFallbackFailed] = useState(false)
  useEffect(() => { setSourceFailed(!src); setFallbackFailed(false) }, [src])
  const catalogImage = useQuery({
    queryKey: ['questions', 'catalog-image', searchTerm],
    queryFn: () => hubApi<ProductImageSearch>(`/products?status=ACTIVE&limit=10&search=${encodeURIComponent(searchTerm)}`),
    enabled: sourceFailed && !fallbackFailed && Boolean(searchTerm),
    staleTime: 5 * 60_000,
    retry: false,
  })
  const match = catalogImage.data?.items.find(item => item.variants.some(variant => [sku, barcode, modelCode].some(value => value && [variant.sku, variant.barcode, variant.modelCode].some(candidate => candidate?.toLocaleLowerCase('tr-TR') === value.toLocaleLowerCase('tr-TR')))))
  const imageUrl = sourceFailed ? match?.primaryImageUrl || match?.familyMediaUrls?.[0] : src
  return imageUrl && !fallbackFailed
    ? <img src={imageUrl} alt={`${productName} görseli`} loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => { if (sourceFailed) setFallbackFailed(true); else setSourceFailed(true) }} />
    : <span className="rv-question-product-placeholder" aria-label="Ürün görseli bulunamadı"><UiIcon name="products" size={20} /></span>
}

function syncSummary(item: SyncState) {
  if (item.historyImported) return `${item.importedCount.toLocaleString('tr-TR')} kayıt · Geçmiş tamamlandı`
  if (item.progressStatus === 'INITIAL') return `${item.importedCount.toLocaleString('tr-TR')} kayıt · Geçmiş aktarılıyor`
  if (item.progressStatus === 'FAILED') return 'Son aktarım başarısız'
  if (item.progressStatus === 'QUEUED') return 'İlk aktarım sırada'
  return 'İlk aktarım bekleniyor'
}

export function QuestionsPage() {
  const client = useQueryClient()
  const [tab, setTab] = useState<WorkspaceTab>('PRODUCT')
  const [status, setStatus] = useState<QuestionStatus>('WAITING')
  const [platform, setPlatform] = useState('ALL')
  const [connectionId, setConnectionId] = useState('ALL')
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [replyingId, setReplyingId] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())
  const [notice, setNotice] = useState('')
  const [templateTitle, setTemplateTitle] = useState('')
  const [templateText, setTemplateText] = useState('')
  const [editingTemplate, setEditingTemplate] = useState<Template | null>(null)
  const [answerText, setAnswerText] = useState('')
  const requestedSync = useRef(false)
  const kind = tab === 'ORDER' ? 'ORDER' : 'PRODUCT'
  const connections = useQuery({ queryKey: ['questions', 'connections'], queryFn: async () => (await loadAllPages<Connection>('/connections', 100)).items.filter(item => ['TRENDYOL', 'HEPSIBURADA'].includes(item.platformCode)), staleTime: 60_000 })
  const filters = new URLSearchParams({ kind, status })
  if (platform !== 'ALL') filters.set('platform', platform)
  if (connectionId !== 'ALL') filters.set('connectionId', connectionId)
  if (dateFrom) filters.set('dateFrom', new Date(`${dateFrom}T00:00:00`).toISOString())
  if (dateTo) filters.set('dateTo', new Date(`${dateTo}T23:59:59`).toISOString())
  if (search.trim()) filters.set('search', search.trim())
  filters.set('page', String(page)); filters.set('limit', '50')
  const questions = useQuery({ queryKey: ['questions', kind, status, platform, connectionId, dateFrom, dateTo, search, page], queryFn: () => hubApi<QuestionListPage>(`/questions?${filters.toString()}`), refetchInterval: 30_000, staleTime: 10_000 })
  const templates = useQuery({ queryKey: ['question-templates'], queryFn: () => hubApi<Template[]>('/question-templates'), staleTime: 30_000 })
  const syncStates = useQuery({ queryKey: ['questions', 'sync-state'], queryFn: () => hubApi<SyncState[]>('/questions/sync-state'), refetchInterval: 15_000 })
  const detail = useQuery({ queryKey: ['questions', 'detail', selectedId], queryFn: () => hubApi<MarketplaceQuestion>(`/questions/${selectedId}`), enabled: Boolean(selectedId), refetchInterval: 15_000 })

  const sync = useMutation({
    mutationFn: (requestedKind: string) => hubApi<{ jobs: string[]; queuedConnections: number }>('/questions/sync', { method: 'POST', headers: { 'X-Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ kind: requestedKind }) }),
    onSuccess: async () => { setNotice(''); await Promise.all([client.invalidateQueries({ queryKey: ['questions'] }), client.invalidateQueries({ queryKey: ['questions', 'sync-state'] })]) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Senkronizasyon kuyruğa alınamadı.'),
  })
  const saveTemplate = useMutation({
    mutationFn: () => hubApi<Template>(editingTemplate ? `/question-templates/${editingTemplate.id}` : '/question-templates', { method: editingTemplate ? 'PUT' : 'POST', headers: editingTemplate ? { 'If-Match': `"v${editingTemplate.version}"` } : undefined, body: JSON.stringify({ title: templateTitle, text: templateText }) }),
    onSuccess: async () => { setTemplateTitle(''); setTemplateText(''); setEditingTemplate(null); setNotice(''); await client.invalidateQueries({ queryKey: ['question-templates'] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Hazır cevap kaydedilemedi.'),
  })
  const deleteTemplate = useMutation({ mutationFn: (item: Template) => hubApi<void>(`/question-templates/${item.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${item.version}"` } }), onSuccess: async () => { setNotice(''); await client.invalidateQueries({ queryKey: ['question-templates'] }) }, onError: error => setNotice(error instanceof Error ? error.message : 'Hazır cevap silinemedi.') })
  const sendAnswer = useMutation({
    mutationFn: ({ id, version, text }: { id: string; version: number; text: string }) => hubApi<MarketplaceQuestion>(`/questions/${id}/answer`, { method: 'POST', headers: { 'X-Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ text, version }) }),
    onSuccess: async (_result, variables) => { setNotice(''); setReplyingId(current => current === variables.id ? null : current); setAnswerText(''); await Promise.all([client.invalidateQueries({ queryKey: ['questions'] }), client.invalidateQueries({ queryKey: ['questions', 'detail', variables.id] })]) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Cevap gönderilemedi.'),
  })

  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 30_000); return () => window.clearInterval(timer) }, [])
  useEffect(() => { setPage(1) }, [tab, status, platform, connectionId, dateFrom, dateTo, search])
  useEffect(() => { if (selectedId) setAnswerText('') }, [selectedId])
  useEffect(() => {
    if (tab === 'TEMPLATES' || requestedSync.current || !connections.isSuccess) return
    requestedSync.current = true
    sync.mutate('ALL')
  }, [tab, kind, connections.isSuccess])

  const rows = questions.data?.items ?? []
  const selected = detail.data
  const connectionNames = connections.data ?? []
  const questionStatusView = selected ? questionStatus(selected.status) : null
  const answerMin = selected?.platformCode === 'TRENDYOL' ? 10 : 1

  return <section className="content rv-questions-page">
    <PageHeader eyebrow="Müşteri iletişimi" title="Ürün soruları" description="Trendyol ürün sorularını ve Hepsiburada ürün/sipariş sorularını yönetin." actions={<Button variant="secondary" onClick={() => sync.mutate(kind)} loading={sync.isPending}><UiIcon name="refresh" size={17} /> Yenile</Button>} />
    <section className="rv-questions-sync" aria-label="Soru aktarım durumu">
      {(syncStates.data ?? []).filter(item => platform === 'ALL' || item.platformCode === platform).map(item => <div className="rv-questions-sync-item" data-platform={item.platformCode.toLowerCase()} key={item.connectionId}><img className={`rv-question-platform-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? undefined} alt="" aria-hidden="true" /><strong>{item.storeName}</strong><span title={item.historyStartedAt ? `Aktarım başlangıcı: ${formatDate(item.historyStartedAt)}` : undefined}>{syncSummary(item)}</span><small>{item.lastSuccessAt ? `Son aktarım ${formatDate(item.lastSuccessAt)}` : item.lastError ? item.lastError : ''}</small></div>)}
      {!syncStates.data?.length && <span>Soru geçmişi bağlantılarınızdan arka planda alınır. İlk senkronizasyon sonrası aktarım durumu burada görünür.</span>}
    </section>
    <Tabs className="rv-questions-tabs" ariaLabel="Soru çalışma alanı" value={tab} onChange={value => { setTab(value as WorkspaceTab); setConnectionId('ALL'); if (value === 'ORDER') setPlatform('HEPSIBURADA'); else if (tab === 'ORDER') setPlatform('ALL') }} items={[{ value: 'PRODUCT', label: 'Ürün soruları' }, { value: 'ORDER', label: 'Sipariş soruları' }, { value: 'TEMPLATES', label: 'Hazır cevaplar' }]} />
    {tab !== 'TEMPLATES' ? <>
      <section className="rv-filter-bar rv-questions-filters" aria-label="Soru filtreleri">
        <Tabs className="rv-questions-status-tabs" ariaLabel="Soru durum filtresi" value={status} onChange={value => setStatus(value as QuestionStatus)} items={statusFilters.map(item => ({ value: item.value, label: item.label }))} />
        <div className="rv-filter-fields rv-questions-filter-grid">{tab === 'ORDER' ? <div className="rv-question-platform-fixed"><small>Platform</small><Badge tone="info">Hepsiburada</Badge></div> : <label>Platform<select value={platform} onChange={event => { setPlatform(event.target.value); setConnectionId('ALL') }}><option value="ALL">Tüm platformlar</option><option value="TRENDYOL">Trendyol</option><option value="HEPSIBURADA">Hepsiburada</option></select></label>}<label>Mağaza<select value={connectionId} onChange={event => setConnectionId(event.target.value)}><option value="ALL">Tüm mağazalar</option>{connectionNames.filter(item => tab === 'ORDER' || platform === 'ALL' || item.platformCode === platform).filter(item => tab !== 'ORDER' || item.platformCode === 'HEPSIBURADA').map(item => <option value={item.id} key={item.id}>{item.displayName}</option>)}</select></label><label>Başlangıç<input type="date" value={dateFrom} onChange={event => setDateFrom(event.target.value)} /></label><label>Bitiş<input type="date" value={dateTo} onChange={event => setDateTo(event.target.value)} /></label><label className="rv-questions-search">Metin ara<input value={search} onChange={event => setSearch(event.target.value)} placeholder="Soru, ürün, SKU veya sipariş no" /></label></div>
      </section>
      {notice && <div className="rv-questions-notice" role="status"><span>{notice}</span><button type="button" aria-label="Bildirimi kapat" onClick={() => setNotice('')}>×</button></div>}
      <section className="rv-questions-results"><header><div><h2>{tab === 'ORDER' ? 'Hepsiburada sipariş soruları' : 'Ürün soruları'}</h2><p>{questions.isLoading ? 'Sorular yükleniyor…' : `${(questions.data?.totalCount ?? 0).toLocaleString('tr-TR')} soru`}</p></div><span>Her 30 saniyede bir yenilenir</span></header>
        {questions.isLoading ? <LoadingState>Sorular yükleniyor…</LoadingState> : questions.isError ? <EmptyState>Sorular alınamadı. Biraz sonra yeniden deneyin.</EmptyState> : rows.length === 0 ? <EmptyState>Bu filtrelerde soru bulunamadı. Geçmiş aktarımı tamamlandıkça erişilebilen sorular listelenir.</EmptyState> : <div className="rv-questions-list">{rows.map(row => {
          const badge = questionStatus(row.status)
          const deadline = row.status === 'WAITING_FOR_ANSWER' ? questionDeadline(row.expiresAt, now) : null
          return <article className="rv-question-card" key={row.id}><div className="rv-question-product"><QuestionProductImage src={row.productImageUrl} sku={row.productSku} barcode={row.productBarcode} modelCode={row.productModelCode} productName={row.productName || 'Ürün'} /><div><strong>{row.productName || (row.kind === 'ORDER' ? 'Sipariş sorusu' : 'Ürün bilgisi yok')}</strong><small>{[row.productSku && `SKU ${row.productSku}`, row.productModelCode && `Model ${row.productModelCode}`, row.productBarcode && `Barkod ${row.productBarcode}`].filter(Boolean).join(' · ') || 'Ürün kodu bilgisi yok'}</small>{row.customerName && <small>Müşteri: {row.customerName}</small>}</div></div><div className="rv-question-content"><p>{row.questionText}</p><div className="rv-question-meta"><Badge tone="info">{displayPlatform(row.platformCode)}</Badge><span>{row.storeName}</span><time dateTime={row.createdAt} aria-label={`Soru tarihi ${formatDate(row.createdAt)}`}>{formatDate(row.createdAt)}</time>{tab === 'ORDER' && row.externalOrderNumber && <Link to={`/orders?search=${encodeURIComponent(row.externalOrderNumber)}`}>Sipariş #{row.externalOrderNumber}</Link>}</div></div><div className="rv-question-actions"><Badge tone={badge.tone}>{badge.label}</Badge>{deadline ? <span className={`rv-question-deadline ${deadline.urgent ? 'is-urgent' : ''}`} title={`Son cevap tarihi: ${formatDate(row.expiresAt)}`}>{deadline.text === 'Süre doldu' ? 'Süre doldu' : `Kalan süre: ${deadline.text}`}</span> : <small className="rv-question-age">Soru tarihi: {formatDate(row.createdAt)}</small>}{row.status === 'WAITING_FOR_ANSWER' && <Button size="sm" variant={replyingId === row.id ? 'primary' : 'secondary'} onClick={() => { setReplyingId(current => current === row.id ? null : row.id); setAnswerText('') }}>{replyingId === row.id ? 'Cevabı kapat' : 'Cevapla'}</Button>}<Button size="sm" variant="secondary" onClick={() => setSelectedId(row.id)}>Geçmiş</Button></div>{replyingId === row.id && <section className="rv-question-inline-compose" aria-label={`#${row.externalQuestionId} için cevap`}>
            {templates.data?.length ? <div className="rv-question-bubbles" aria-label="Hazır cevaplar">{templates.data.map(item => <button type="button" key={item.id} title={item.text} onClick={() => setAnswerText(item.text)}>{item.title}</button>)}</div> : null}
            <textarea rows={3} maxLength={2000} value={answerText} onChange={event => setAnswerText(event.target.value)} placeholder="Cevabınızı sorunun altında yazın…" aria-label="Soruya cevap" />
            <div className="rv-question-compose-footer"><small>{answerText.trim().length}/{row.platformCode === 'TRENDYOL' ? '10–2000' : '1–2000'} karakter</small><Button onClick={() => sendAnswer.mutate({ id: row.id, version: row.version, text: answerText })} loading={sendAnswer.isPending} disabled={answerText.trim().length < (row.platformCode === 'TRENDYOL' ? 10 : 1) || answerText.trim().length > 2000}>Gönder</Button></div>
            {sendAnswer.isError && <p className="rv-question-send-error" role="alert">{sendAnswer.error instanceof Error ? sendAnswer.error.message : 'Cevap gönderilemedi.'}</p>}
          </section>}</article>
        })}</div>}
        {(questions.data?.totalCount ?? 0) > 50 && <nav className="rv-questions-pagination" aria-label="Soru sayfaları"><Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage(value => Math.max(1, value - 1))}>Önceki</Button><span>Sayfa {page} / {Math.max(1, Math.ceil((questions.data?.totalCount ?? 0) / 50))}</span><Button size="sm" variant="secondary" disabled={page >= Math.ceil((questions.data?.totalCount ?? 0) / 50)} onClick={() => setPage(value => value + 1)}>Sonraki</Button></nav>}
      </section>
    </> : <section className="rv-question-templates"><div className="rv-question-template-editor"><div><h2>{editingTemplate ? 'Hazır cevabı düzenle' : 'Yeni hazır cevap'}</h2><p>Bu cevaplar Trendyol ve Hepsiburada için ortak kullanılır.</p></div><label>Başlık<input value={templateTitle} maxLength={120} onChange={event => setTemplateTitle(event.target.value)} placeholder="Örn. Ürün ölçü tablosu" /></label><label>Cevap metni<textarea rows={4} maxLength={2000} value={templateText} onChange={event => setTemplateText(event.target.value)} placeholder="Müşteriye gönderilecek cevabı yazın" /><small>{templateText.length}/2000</small></label><div className="rv-question-template-actions"><Button variant="secondary" onClick={() => { setEditingTemplate(null); setTemplateTitle(''); setTemplateText('') }} disabled={!editingTemplate && !templateTitle && !templateText}>Temizle</Button><Button onClick={() => saveTemplate.mutate()} disabled={!templateTitle.trim() || !templateText.trim()} loading={saveTemplate.isPending}>{editingTemplate ? 'Değişiklikleri kaydet' : 'Hazır cevabı oluştur'}</Button></div></div>
        <div className="rv-question-template-list"><header><h2>Kaydedilmiş cevaplar</h2><span>{templates.data?.length ?? 0}</span></header>{templates.isLoading ? <LoadingState>Hazır cevaplar yükleniyor…</LoadingState> : templates.data?.length ? templates.data.map(item => <article className="rv-question-template-card" key={item.id}><div><strong>{item.title}</strong><p>{item.text}</p><small>Güncellendi: {formatDate(item.updatedAt)}</small></div><div><Button size="sm" variant="secondary" onClick={() => { setEditingTemplate(item); setTemplateTitle(item.title); setTemplateText(item.text); window.scrollTo({ top: 0, behavior: 'smooth' }) }}>Düzenle</Button><Button size="sm" variant="danger" onClick={() => deleteTemplate.mutate(item)} loading={deleteTemplate.isPending}>Sil</Button></div></article>) : <EmptyState>Henüz hazır cevap oluşturulmadı.</EmptyState>}</div>
      </section>}
    {selectedId && <Modal open title="Soru geçmişi" description={selected ? `${displayPlatform(selected.platformCode)} · ${selected.storeName}` : 'Soru ayrıntısı yükleniyor'} onClose={() => setSelectedId(null)} className="rv-question-detail-modal" footer={<div className="rv-question-modal-footer"><span>{selected ? questionStatusView?.label : ''}</span><Button variant="secondary" onClick={() => setSelectedId(null)}>Kapat</Button></div>}>
      {!selected ? <LoadingState>Soru geçmişi yükleniyor…</LoadingState> : <div className="rv-question-detail"><section className="rv-question-detail-product"><QuestionProductImage src={selected.productImageUrl} sku={selected.productSku} barcode={selected.productBarcode} modelCode={selected.productModelCode} productName={selected.productName || 'Ürün'} /><div><h3>{selected.productName || 'Sipariş sorusu'}</h3><p>{[selected.productSku && `SKU ${selected.productSku}`, selected.productModelCode && `Model ${selected.productModelCode}`, selected.externalOrderNumber && `Sipariş #${selected.externalOrderNumber}`].filter(Boolean).join(' · ')}</p><small>Soru tarihi: {formatDate(selected.createdAt)}</small></div>{questionStatusView && <Badge tone={questionStatusView.tone}>{questionStatusView.label}</Badge>}</section>
        <section className="rv-question-conversation" aria-label="Mesaj geçmişi">{(selected.conversations ?? []).length ? [...selected.conversations].sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()).map((message, index) => <article className={`rv-question-message ${message.author.toLowerCase().includes('merchant') || message.author.toLowerCase().includes('seller') ? 'is-seller' : 'is-customer'}`} key={`${message.createdAt}-${index}`}><header><strong>{message.author.toLowerCase().includes('merchant') || message.author.toLowerCase().includes('seller') ? 'Satıcı cevabı' : message.author}</strong><time dateTime={message.createdAt}>{formatDate(message.createdAt)}</time></header><p>{message.text}</p>{message.rejectionReason && <small className="rv-question-rejection">Ret nedeni: {message.rejectionReason}</small>}</article>) : <p className="rv-question-history-empty">Platformdan alınan yazışma geçmişi bulunamadı.</p>}</section>
        {selected.status === 'WAITING_FOR_ANSWER' ? <section className="rv-question-compose"><header><h3>Cevap yaz</h3><button type="button" onClick={() => client.invalidateQueries({ queryKey: ['questions', 'detail', selectedId] })}>Geçmişi yenile</button></header>{templates.data?.length ? <div className="rv-question-bubbles" aria-label="Hazır cevaplar">{templates.data.map(item => <button type="button" key={item.id} title={item.text} onClick={() => setAnswerText(item.text)}>{item.title}</button>)}</div> : null}<textarea rows={4} maxLength={2000} value={answerText} onChange={event => setAnswerText(event.target.value)} placeholder="Cevabınızı yazın…" /><div className="rv-question-compose-footer"><small>{answerText.trim().length}/{selected.platformCode === 'TRENDYOL' ? '10–2000' : '1–2000'} karakter</small><Button onClick={() => sendAnswer.mutate({ id: selected.id, version: selected.version, text: answerText })} loading={sendAnswer.isPending} disabled={answerText.trim().length < answerMin || answerText.trim().length > 2000}>Gönder</Button></div>{sendAnswer.isError && <p className="rv-question-send-error" role="alert">{sendAnswer.error instanceof Error ? sendAnswer.error.message : 'Cevap gönderilemedi.'}</p>}</section> : selected.status === 'ANSWER_SUBMITTED' ? <div className="rv-question-awaiting-confirmation" role="status">Cevap gönderildi ve pazaryeri durumu doğrulanıyor. Sonuç kesinleşene kadar tekrar gönderim kapalıdır.</div> : null}
      </div>}
    </Modal>}
  </section>
}
