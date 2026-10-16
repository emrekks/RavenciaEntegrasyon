import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { marketplaceProductSearchUrl } from './marketplace-product-link'
import { Button, EmptyState, LoadingState, Tabs, UiIcon } from '../../shared/components'
import { platformLogoClass, platformLogoSource } from '../../shared/platform-logos'
import { productImageFallbackUrls } from '../marketplace/product-image-lookups'
import { questionDeadline } from './question-time'
import { visibleQuestionConversation, type QuestionConversation } from './question-conversation'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { Modal } from '../../shared/components'
import { MessageToast } from '../../shared/components/NoticeToast'
import './questions-inbox.css'

type MarketplaceQuestion = {
  id: string; connectionId: string; platformCode: string; storeName: string; externalQuestionId: string; kind: string; status: string
  questionText: string; productName?: string | null; productImageUrl?: string | null; productSku?: string | null; productBarcode?: string | null
  productModelCode?: string | null; productUrl?: string | null; customerName?: string | null; externalOrderNumber?: string | null; conversations: QuestionConversation[]
  createdAt: string; expiresAt?: string | null; lastRemoteModifiedAt: string; lastSyncedAt: string; version: number
}
type Template = { id: string; title: string; text: string; version: number; updatedAt: string }
type Connection = { id: string; platformCode: string; displayName: string; status: string }
type SyncState = { connectionId: string; platformCode: string; storeName: string; historyImported: boolean; historyStartedAt?: string | null; progressStatus: string; importedCount: number; productQuestionCount?: number; orderQuestionCount?: number; lastRunStartedAt?: string | null; lastSuccessAt?: string | null; lastError?: string | null }
type QuestionListPage = { items: MarketplaceQuestion[]; page: number; limit: number; totalCount: number }
type QuestionStatus = 'WAITING' | 'ANSWERED' | 'EXPIRED' | 'OTHER' | 'ALL'
// Hepsiburada has its own order questions; Pazarama marks a question that names an order. Trendyol and n11 only ask about products.
const ORDER_QUESTION_PLATFORMS = ['HEPSIBURADA', 'PAZARAMA']

type WorkspaceTab = 'PRODUCT' | 'ORDER' | 'TEMPLATES'

type Glyph = 'done' | 'partial' | 'none' | 'bad'
const statusFilters: Array<{ value: QuestionStatus; label: string; glyph: Glyph }> = [
  { value: 'WAITING', label: 'Cevap Bekleyenler', glyph: 'partial' }, { value: 'ANSWERED', label: 'Cevaplananlar', glyph: 'done' },
  { value: 'EXPIRED', label: 'Süresi Dolanlar', glyph: 'bad' }, { value: 'OTHER', label: 'Diğer Durumlar', glyph: 'none' }, { value: 'ALL', label: 'Tümü', glyph: 'none' },
]

// The shared state marks of the Sade layout: filled check, clock arc, dashed circle and a cross for rejections.
function StateGlyph({ state }: { state: Glyph }) {
  if (state === 'done') return <svg className="qi-glyph is-done" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'partial') return <svg className="qi-glyph" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  if (state === 'bad') return <svg className="qi-glyph is-bad" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  return <svg className="qi-glyph" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
}

function dateKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function lastDays(days: number) { const end = new Date(); const start = new Date(); start.setDate(start.getDate() - (days - 1)); return { from: dateKey(start), to: dateKey(end) } }

function questionStatus(status: string) {
  const normalized = status.toUpperCase()
  if (normalized === 'WAITING_FOR_ANSWER') return { label: 'Cevap Bekliyor', glyph: 'partial' as Glyph }
  if (normalized === 'ANSWER_SUBMITTED') return { label: 'Cevap Gönderildi · Doğrulanıyor', glyph: 'partial' as Glyph }
  if (normalized === 'ANSWERED') return { label: 'Cevaplandı', glyph: 'done' as Glyph }
  if (normalized === 'EXPIRED' || normalized === 'UNANSWERED' || normalized === 'AUTOCLOSED') return { label: 'Süresi Doldu', glyph: 'none' as Glyph }
  if (normalized === 'REJECTED') return { label: 'Reddedildi', glyph: 'bad' as Glyph }
  if (normalized === 'REPORTED') return { label: 'İnceleniyor', glyph: 'partial' as Glyph }
  return { label: status, glyph: 'none' as Glyph }
}

function formatDate(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date).replace(' ', ' · ')
}

function displayPlatform(code: string) { return code === 'HEPSIBURADA' ? 'Hepsiburada' : code === 'N11' ? 'N11' : code === 'PAZARAMA' ? 'Pazarama' : 'Trendyol' }
function QuestionProductImage({ src, connectionId, sku, barcode, modelCode, productName }: { src?: string | null; connectionId: string; sku?: string | null; barcode?: string | null; modelCode?: string | null; productName: string }) {
  const sources = [...new Set([src, ...productImageFallbackUrls([barcode, sku, modelCode], connectionId, productName), ...(productName.trim() ? productImageFallbackUrls([], connectionId, productName) : [])].filter((value): value is string => Boolean(value)))]
  const sourceKey = sources.join('\u0000')
  const [sourceIndex, setSourceIndex] = useState(0)
  useEffect(() => setSourceIndex(0), [sourceKey])
  const imageUrl = sources[sourceIndex]
  return imageUrl
    ? <img src={imageUrl} alt={`${productName} görseli`} loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => setSourceIndex(current => current === sourceIndex ? current + 1 : current)} />
    : <span className="rv-question-product-placeholder" aria-label="Ürün görseli bulunamadı"><UiIcon name="image" size={20} /></span>
}

// The count follows the open tab: product questions on the product tab, order questions on the order tab.
// A store named differently from its marketplace also shows the marketplace; a passive connection says so.
function storeLabel(item: Connection) {
  const platform = displayPlatform(item.platformCode)
  const name = item.displayName.toLocaleLowerCase('tr-TR').includes(platform.toLocaleLowerCase('tr-TR')) ? item.displayName : `${item.displayName} · ${platform}`
  return item.status === 'ACTIVE' || item.status === 'VERIFIED' ? name : `${name} (pasif)`
}

function syncSummary(item: SyncState, tab: WorkspaceTab) {
  const count = (tab === 'ORDER' ? item.orderQuestionCount : item.productQuestionCount) ?? item.importedCount
  const records = `${count.toLocaleString('tr-TR')} kayıt`
  if (item.historyImported) return { records, state: 'Geçmiş tamamlandı', glyph: 'done' as Glyph }
  if (item.progressStatus === 'INITIAL') return { records, state: 'Geçmiş aktarılıyor', glyph: 'partial' as Glyph }
  if (item.progressStatus === 'FAILED') return { records, state: 'Son aktarım başarısız', glyph: 'bad' as Glyph }
  if (item.progressStatus === 'QUEUED') return { records, state: 'İlk aktarım sırada', glyph: 'partial' as Glyph }
  return { records, state: 'İlk aktarım bekleniyor', glyph: 'none' as Glyph }
}

export function QuestionsPage() {
  const client = useQueryClient()
  const [tab, setTab] = useState<WorkspaceTab>('PRODUCT')
  const [templatesOpen, setTemplatesOpen] = useState(false)
  const [filterOpen, setFilterOpen] = useState(false)
  const [statusOpen, setStatusOpen] = useState(false)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detailOpen, setDetailOpen] = useState(false)
  const filterRef = useRef<HTMLDivElement>(null)
  const statusRef = useRef<HTMLDivElement>(null)
  const [status, setStatus] = useState<QuestionStatus>('WAITING')
  const [platform, setPlatform] = useState('ALL')
  const [connectionId, setConnectionId] = useState('ALL')
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState('NEWEST')
  const [page, setPage] = useState(1)
  const [answerDrafts, setAnswerDrafts] = useState<Record<string, string>>({})
  const [now, setNow] = useState(Date.now())
  const [notice, setNotice] = useState('')
  const [templateTitle, setTemplateTitle] = useState('')
  const [templateText, setTemplateText] = useState('')
  const [editingTemplate, setEditingTemplate] = useState<Template | null>(null)
  const requestedSync = useRef(false)
  const kind = tab === 'ORDER' ? 'ORDER' : 'PRODUCT'
  const connections = useQuery({ queryKey: ['questions', 'connections'], queryFn: async () => (await loadAllPages<Connection>('/connections', 100)).items.filter(item => ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA'].includes(item.platformCode)), staleTime: 60_000 })
  const filters = new URLSearchParams({ kind, status })
  if (platform !== 'ALL') filters.set('platform', platform)
  if (connectionId !== 'ALL') filters.set('connectionId', connectionId)
  if (dateFrom) filters.set('dateFrom', new Date(`${dateFrom}T00:00:00`).toISOString())
  if (dateTo) filters.set('dateTo', new Date(`${dateTo}T23:59:59`).toISOString())
  if (search.trim()) filters.set('search', search.trim())
  filters.set('sort', sort)
  filters.set('page', String(page)); filters.set('limit', '50')
  const questions = useQuery({ queryKey: ['questions', kind, status, platform, connectionId, dateFrom, dateTo, search, sort, page], queryFn: () => hubApi<QuestionListPage>(`/questions?${filters.toString()}`), refetchInterval: 30_000, staleTime: 10_000 })
  const countQuery = (countKind: string, countStatus: QuestionStatus, scoped: boolean) => {
    const params = new URLSearchParams({ kind: countKind, status: countStatus, page: '1', limit: '1', sort })
    if (scoped) {
      if (platform !== 'ALL') params.set('platform', platform)
      if (connectionId !== 'ALL') params.set('connectionId', connectionId)
      if (dateFrom) params.set('dateFrom', new Date(`${dateFrom}T00:00:00`).toISOString())
      if (dateTo) params.set('dateTo', new Date(`${dateTo}T23:59:59`).toISOString())
      if (search.trim()) params.set('search', search.trim())
    }
    return hubApi<QuestionListPage>(`/questions?${params.toString()}`).then(result => result.totalCount)
  }
  const statusCounts = useQueries({ queries: statusFilters.map(item => ({ queryKey: ['questions', 'count', kind, item.value, platform, connectionId, dateFrom, dateTo, search], queryFn: () => countQuery(kind, item.value, true), enabled: tab !== 'TEMPLATES', refetchInterval: 30_000, staleTime: 10_000 })) })
  const waitingCounts = useQueries({ queries: (['PRODUCT', 'ORDER'] as const).map(countKind => ({ queryKey: ['questions', 'count', 'waiting', countKind], queryFn: () => countQuery(countKind, 'WAITING', false), refetchInterval: 30_000, staleTime: 10_000 })) })
  const formatCount = (value: number | undefined) => value === undefined ? undefined : value.toLocaleString('tr-TR')
  const templates = useQuery({ queryKey: ['question-templates'], queryFn: () => hubApi<Template[]>('/question-templates'), staleTime: 30_000 })
  const syncStates = useQuery({ queryKey: ['questions', 'sync-state'], queryFn: () => hubApi<SyncState[]>('/questions/sync-state'), refetchInterval: 15_000 })

  const sync = useMutation({
    mutationFn: (request: { kind: string; platformCode?: string }) => hubApi<{ jobs: string[]; queuedConnections: number }>('/questions/sync', { method: 'POST', headers: { 'X-Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify(request) }),
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
    onSuccess: async (_data, variables) => { setNotice(''); setAnswerDrafts(current => ({ ...current, [variables.id]: '' })); await client.invalidateQueries({ queryKey: ['questions'] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Cevap gönderilemedi.'),
  })

  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 30_000); return () => window.clearInterval(timer) }, [])
  useEffect(() => { setPage(1) }, [tab, status, platform, connectionId, dateFrom, dateTo, search, sort])
  useEffect(() => {
    if (tab === 'TEMPLATES' || requestedSync.current || !connections.isSuccess) return
    requestedSync.current = true
    sync.mutate({ kind: 'ALL' })
  }, [tab, kind, connections.isSuccess])

  useEffect(() => {
    if (!filterOpen && !statusOpen) return
    const close = (event: MouseEvent) => {
      if (filterOpen && filterRef.current && !filterRef.current.contains(event.target as Node) && !(event.target as Element).closest?.('.date-range-picker-popover')) setFilterOpen(false)
      if (statusOpen && statusRef.current && !statusRef.current.contains(event.target as Node)) setStatusOpen(false)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [filterOpen, statusOpen])

  const rows = questions.data?.items ?? []
  const connectionNames = connections.data ?? []
  const selected = rows.find(item => item.id === selectedId) ?? rows[0]
  const totalCount = questions.data?.totalCount ?? 0
  const pageCount = Math.max(1, Math.ceil(totalCount / 50))
  const currentStatus = statusFilters.find(item => item.value === status) ?? statusFilters[0]
  const currentStatusIndex = statusFilters.indexOf(currentStatus)
  const activeFilterCount = (connectionId !== 'ALL' ? 1 : 0) + (dateFrom || dateTo ? 1 : 0) + (sort !== 'NEWEST' ? 1 : 0)
  const presets = [{ key: 'ALL', label: 'Tümü', title: 'Tüm Tarihler', range: { from: '', to: '' } }, { key: 'TODAY', label: 'Bugün', title: 'Bugün', range: lastDays(1) }, { key: '7', label: '7 Gün', title: 'Son 7 Gün', range: lastDays(7) }, { key: '30', label: '30 Gün', title: 'Son 30 Gün', range: lastDays(30) }]
  const productHrefOf = (row: MarketplaceQuestion) => row.kind === 'PRODUCT' || row.platformCode === 'HEPSIBURADA' ? row.productUrl || marketplaceProductSearchUrl(row.platformCode, row.productBarcode, row.productSku, row.productModelCode, row.productName) : null
  const codesOf = (row: MarketplaceQuestion) => [row.productSku && `SKU ${row.productSku}`, row.productModelCode && `Model ${row.productModelCode}`, row.productBarcode && `Barkod ${row.productBarcode}`].filter(Boolean).join(' · ') || 'Ürün kodu bilgisi yok'
  const isSeller = (author: string) => author.toLowerCase().includes('merchant') || author.toLowerCase().includes('seller')
  const openQuestion = (id: string) => { setSelectedId(id); setDetailOpen(true) }
  const nextWaiting = (id: string) => { const waiting = rows.filter(item => item.status === 'WAITING_FOR_ANSWER'); const index = waiting.findIndex(item => item.id === id); return waiting[index + 1] ?? waiting.find(item => item.id !== id) }

  const detail = (() => {
    if (!selected) return <div className="qi-empty"><EmptyState>{questions.isLoading ? 'Sorular yükleniyor…' : 'Soldan bir soru seçin.'}</EmptyState></div>
    const row = selected
    const state = questionStatus(row.status)
    const waiting = row.status === 'WAITING_FOR_ANSWER'
    const deadline = waiting ? questionDeadline(row.expiresAt, now) : null
    const previousMessages = visibleQuestionConversation(row.conversations ?? [], row.questionText)
    const productHref = productHrefOf(row)
    const draft = answerDrafts[row.id] ?? ''
    const minLength = row.platformCode === 'TRENDYOL' ? 10 : 1
    const length = draft.trim().length
    const send = () => { if (length >= minLength && length <= 2000 && !sendAnswer.isPending) { if (row.platformCode === 'N11' && !window.confirm('N11’de bir soruya yalnızca bir kez cevap verilir ve sonradan değiştirilemez. Cevap gönderilsin mi?')) return; if (row.platformCode === 'PAZARAMA' && !window.confirm('Pazarama’da cevap önce Pazarama onayına düşer; onaylanmadan müşteriye görünmez ve reddedilebilir. Cevap gönderilsin mi?')) return; sendAnswer.mutate({ id: row.id, version: row.version, text: draft }) } }
    const next = nextWaiting(row.id)
    return <article className="qi-detail" aria-label="Seçili soru">
      <div className="qi-detail-head">
        <button type="button" className="qi-back" onClick={() => setDetailOpen(false)}><UiIcon name="chevronLeft" size={16} />Sorular</button>
        <div className="qi-dcard">
          <div className="qi-product rv-question-product"><QuestionProductImage src={row.productImageUrl} connectionId={row.connectionId} sku={row.productSku} barcode={row.productBarcode} modelCode={row.productModelCode} productName={row.productName || 'Ürün'} /><div><strong>{productHref ? <a className="rv-question-product-link" href={productHref} target="_blank" rel="noopener noreferrer">{row.productName || 'Ürün'}</a> : row.productName || (row.kind === 'ORDER' ? 'Sipariş Sorusu' : 'Ürün bilgisi yok')}</strong><small>{codesOf(row)}</small></div><div className="qi-platform rv-question-platform-context"><img className={platformLogoClass(row.platformCode)} src={platformLogoSource(row.platformCode) ?? undefined} alt="" aria-hidden="true" /><span>{row.storeName || displayPlatform(row.platformCode)}</span></div></div>
          <dl className="qi-facts"><div><dt>Durum</dt><dd><span className="qi-tag"><StateGlyph state={state.glyph} />{state.label}</span></dd></div><div><dt>{deadline ? 'Kalan Süre' : 'Son Güncelleme'}</dt><dd className={deadline?.urgent ? 'is-urgent' : undefined}>{deadline ? <span className="rv-question-deadline" title={`Son cevap tarihi: ${formatDate(row.expiresAt)}`}>{deadline.text}</span> : formatDate(row.lastRemoteModifiedAt)}</dd></div><div><dt>{row.kind === 'ORDER' && row.externalOrderNumber ? 'Sipariş' : 'Soru No'}</dt><dd>{row.kind === 'ORDER' && row.externalOrderNumber ? <span className="rv-question-meta"><Link to={`/orders?search=${encodeURIComponent(row.externalOrderNumber)}`}>Sipariş #{row.externalOrderNumber}</Link></span> : `#${row.externalQuestionId}`}</dd></div><div><dt>Soru Tarihi</dt><dd className="rv-question-age">{formatDate(row.createdAt)}</dd></div></dl>
        </div>
      </div>
      <div className="qi-detail-body rv-question-content" aria-label="Soru ve konuşmalar">
        <div className="rv-question-thread-message is-customer is-question"><header><strong>{row.customerName || 'Müşteri Sorusu'}</strong><time dateTime={row.createdAt}>{formatDate(row.createdAt)}</time></header><p>{row.questionText}</p></div>
        {previousMessages.length > 0 && <div className="rv-question-conversation" aria-label="Konuşma geçmişi">{previousMessages.map((message, index) => { const seller = isSeller(message.author); return <article className={`rv-question-thread-message ${seller ? 'is-seller' : 'is-customer'}`} key={`${message.createdAt}-${index}`}><header><strong>{seller ? 'Satıcı Cevabı' : message.author || 'Müşteri'}</strong><time dateTime={message.createdAt}>{formatDate(message.createdAt)}</time></header><p>{message.text}</p>{message.rejectionReason && <small className="rv-question-rejection">Ret nedeni: {message.rejectionReason}</small>}</article> })}</div>}
        <p className="qi-note"><UiIcon name="info" size={14} />Cevabınız yalnızca soruyu soran müşteri tarafından görüntülenir.</p>
      </div>
      {waiting ? <div className="qi-composer rv-answer-composer" aria-label={`#${row.externalQuestionId} için cevap`}>
        <div className="qi-templates-row"><span>Hazır Cevap</span>{templates.data?.length ? <div className="rv-question-bubbles" aria-label="Hazır cevaplar">{templates.data.map(item => <button type="button" key={item.id} title={item.text} onClick={() => setAnswerDrafts(current => ({ ...current, [row.id]: item.text }))}>{item.title}</button>)}</div> : <small>Kayıtlı hazır cevap yok</small>}<button type="button" className="qi-link" onClick={() => setTemplatesOpen(true)}><UiIcon name="edit" size={14} />Yönet</button></div>
        <textarea rows={3} maxLength={2000} value={draft} onChange={event => setAnswerDrafts(current => ({ ...current, [row.id]: event.target.value }))} onKeyDown={event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); send() } }} placeholder="Cevabınızı yazın…" aria-label="Soruya cevap" />
        <div className="qi-composer-foot"><span className="rv-answer-bar-meta">{length}/{row.platformCode === 'TRENDYOL' ? '10–2000' : '1–2000'} karakter · Ctrl+Enter ile gönderin{row.platformCode === 'N11' ? ' · N11’de cevap bir kez verilir, değiştirilemez' : ''}</span><div>{next && <Button variant="secondary" onClick={() => setSelectedId(next.id)}>Sonraki Soru</Button>}<Button onClick={send} loading={sendAnswer.isPending} disabled={length < minLength || length > 2000}>Gönder</Button></div></div>
        {sendAnswer.isError && sendAnswer.variables?.id === row.id && <p className="rv-question-send-error" role="alert">{sendAnswer.error instanceof Error ? sendAnswer.error.message : 'Cevap gönderilemedi.'}</p>}
      </div> : <div className="qi-closed"><StateGlyph state={state.glyph} />{state.label}{row.status === 'ANSWER_SUBMITTED' ? ' · Pazaryeri onayı bekleniyor' : ''}</div>}
    </article>
  })()

  return <section className="content rv-questions-page qi-page">
    <header className="qi-head"><div><p className="qi-crumb">Müşteri İletişimi</p><h1>Ürün Soruları</h1><p>Ürün sorularını ve Hepsiburada ile Pazarama sipariş sorularını yönetin.</p></div><div className="qi-acts"><Button variant="secondary" onClick={() => setTemplatesOpen(true)}><UiIcon name="list" size={16} />Hazır Cevaplar<span className="qi-count">{templates.data?.length ?? 0}</span></Button><Button variant="secondary" onClick={() => sync.mutate({ kind })} loading={sync.isPending}><UiIcon name="refresh" size={16} /> Yenile</Button></div></header>
    <section className="rv-questions-sync qi-sync" aria-label="Soru aktarım durumu">
      {(syncStates.data ?? []).filter(item => tab !== 'ORDER' || ORDER_QUESTION_PLATFORMS.includes(item.platformCode)).map(item => { const summary = syncSummary(item, tab); return <div className="rv-questions-sync-item" data-platform={item.platformCode.toLowerCase()} key={item.connectionId} title={item.historyStartedAt ? `Aktarım başlangıcı: ${formatDate(item.historyStartedAt)}` : undefined}><img className={`rv-question-platform-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? undefined} alt="" aria-hidden="true" /><div><b>{item.storeName} <em>{summary.records}</em></b><small><StateGlyph state={summary.glyph} />{summary.state}</small><small className="qi-sync-time">{item.lastSuccessAt ? `Son aktarım ${formatDate(item.lastSuccessAt)}` : item.lastError ?? ''}</small></div></div> })}
      {!syncStates.data?.length && <span className="qi-sync-empty">Soru geçmişi bağlantılarınızdan arka planda alınır. İlk senkronizasyon sonrası aktarım durumu burada görünür.</span>}
    </section>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <div className={`qi-inbox${detailOpen ? ' is-detail' : ''}`}>
      <div className="qi-left">
        <Tabs className="rv-questions-tabs qi-tabs" ariaLabel="Soru türü" value={tab} onChange={value => { setTab(value as WorkspaceTab); setConnectionId('ALL'); setPlatform('ALL'); setStatus('WAITING'); setSelectedId(null); setDetailOpen(false) }} items={[{ value: 'PRODUCT', label: 'Ürün Soruları', count: formatCount(waitingCounts[0].data) }, { value: 'ORDER', label: 'Sipariş Soruları', count: formatCount(waitingCounts[1].data) }]} />
        <div className="qi-tools" aria-label="Soru filtreleri">
          <div className="qi-tools-row">
            <label className="rv-questions-search qi-search"><UiIcon name="search" size={15} /><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Soru, ürün, SKU veya sipariş no" aria-label="Metin ara" /></label>
            <div className="qi-filter" ref={filterRef}>
              <button type="button" className={`qi-filter-btn${filterOpen || activeFilterCount ? ' is-on' : ''}`} aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><UiIcon name="filter" size={15} />Filtre{activeFilterCount > 0 && <span className="qi-count">{activeFilterCount}</span>}</button>
              {filterOpen && <div className="qi-popover rv-questions-filter-grid" role="dialog" aria-label="Filtreler">
                <label>Mağaza<select value={connectionId} onChange={event => setConnectionId(event.target.value)}><option value="ALL">Tüm Mağazalar</option>{connectionNames.filter(item => tab !== 'ORDER' || ORDER_QUESTION_PLATFORMS.includes(item.platformCode)).map(item => <option value={item.id} key={item.id}>{storeLabel(item)}</option>)}</select></label>
                <div className="qi-field"><span>Tarih Aralığı</span><div className="qi-range"><div className="qi-seg" role="radiogroup" aria-label="Hazır tarih aralığı">{presets.map(item => { const on = dateFrom === item.range.from && dateTo === item.range.to; return <button type="button" role="radio" aria-checked={on} className={on ? 'is-on' : ''} key={item.key} title={item.title} onClick={() => { setDateFrom(item.range.from); setDateTo(item.range.to) }}>{item.label}</button> })}</div><DateRangePicker className="qi-range-picker" from={dateFrom} to={dateTo} onChange={range => { setDateFrom(range.from); setDateTo(range.to) }} label="Tarih aralığı" /></div></div>
                <label>Sıralama<select value={sort} onChange={event => setSort(event.target.value)}><option value="NEWEST">En Yeni</option><option value="OLDEST">En Eski</option><option value="UPDATED">Son Güncellenen</option></select></label>
                <footer><button type="button" className="qi-link" onClick={() => { setConnectionId('ALL'); setDateFrom(''); setDateTo(''); setSort('NEWEST') }}>Temizle</button><Button onClick={() => setFilterOpen(false)}>Uygula</Button></footer>
              </div>}
            </div>
          </div>
          <div className="qi-status" ref={statusRef}>
            <button type="button" className={`qi-status-btn${statusOpen ? ' is-on' : ''}`} aria-haspopup="listbox" aria-expanded={statusOpen} onClick={() => setStatusOpen(value => !value)}><StateGlyph state={currentStatus.glyph} /><b>{currentStatus.label}</b><span className={`qi-count${currentStatus.value === 'WAITING' && statusCounts[currentStatusIndex]?.data ? ' is-warn' : ''}`}>{formatCount(statusCounts[currentStatusIndex]?.data) ?? '…'}</span><UiIcon name="chevronDown" size={15} /></button>
            {statusOpen && <div className="qi-status-menu" role="listbox" aria-label="Soru durumu">{statusFilters.map((item, index) => <button type="button" role="option" aria-selected={status === item.value} className={status === item.value ? 'is-on' : ''} key={item.value} onClick={() => { setStatus(item.value); setStatusOpen(false); setSelectedId(null) }}><span><StateGlyph state={item.glyph} />{item.label}</span><span className={`qi-count${item.value === 'WAITING' && statusCounts[index]?.data ? ' is-warn' : ''}`}>{formatCount(statusCounts[index]?.data) ?? '…'}</span></button>)}</div>}
          </div>
        </div>
        <div className="qi-list">
          {questions.isLoading ? <LoadingState>Sorular yükleniyor…</LoadingState> : questions.isError ? <EmptyState>Sorular alınamadı. Biraz sonra yeniden deneyin.</EmptyState> : rows.length === 0 ? <EmptyState>Bu filtrelerde soru bulunamadı. Geçmiş aktarımı tamamlandıkça erişilebilen sorular listelenir.</EmptyState> : rows.map(row => {
            const state = questionStatus(row.status)
            const deadline = row.status === 'WAITING_FOR_ANSWER' ? questionDeadline(row.expiresAt, now) : null
            return <button type="button" className={`qi-item rv-question-card${selected?.id === row.id ? ' is-on' : ''}`} key={row.id} aria-current={selected?.id === row.id ? 'true' : undefined} onClick={() => openQuestion(row.id)}>
              <span className="qi-item-logo"><img className={platformLogoClass(row.platformCode)} src={platformLogoSource(row.platformCode) ?? undefined} alt={displayPlatform(row.platformCode)} /></span>
              <span className="qi-item-top"><b>{row.customerName || 'Müşteri'}</b><span>{row.productName || (row.kind === 'ORDER' ? 'Sipariş Sorusu' : 'Ürün')}</span></span>
              <time dateTime={row.createdAt}>{formatDate(row.createdAt).split(' · ')[1] ?? ''}</time>
              <span className="qi-item-text">{row.questionText}</span>
              <span className="qi-item-meta">{deadline ? <span className={`qi-deadline${deadline.urgent ? ' is-urgent' : ''}`}><UiIcon name="clock" size={13} />{deadline.text === 'Süre doldu' ? 'Süre doldu' : `${deadline.text} kaldı`}</span> : <span className="qi-item-state"><StateGlyph state={state.glyph} />{state.label}</span>}{row.kind === 'ORDER' && row.externalOrderNumber && <span>Sipariş #{row.externalOrderNumber}</span>}{(answerDrafts[row.id] ?? '').trim() && <span className="qi-draft">Taslak</span>}</span>
            </button>
          })}
        </div>
        <footer className="qi-foot"><span>{questions.isLoading ? 'Yükleniyor…' : `${totalCount.toLocaleString('tr-TR')} soru`}</span>{totalCount > 50 && <nav className="rv-questions-pagination" aria-label="Soru sayfaları"><Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage(value => Math.max(1, value - 1))}>Önceki</Button><span>Sayfa {page} / {pageCount}</span><Button size="sm" variant="secondary" disabled={page >= pageCount} onClick={() => setPage(value => value + 1)}>Sonraki</Button></nav>}<span className="qi-live"><i aria-hidden="true" />30 sn’de bir yenilenir</span></footer>
      </div>
      <div className="qi-right">{detail}</div>
    </div>
    <Modal open={templatesOpen} title="Hazır Cevaplar" description={`${templates.data?.length ?? 0} kayıtlı cevap · Tüm pazaryerlerinde ortak kullanılır.`} className="qi-templates-modal" onClose={() => { setTemplatesOpen(false); setEditingTemplate(null); setTemplateTitle(''); setTemplateText('') }}>
      <section className="rv-question-templates">
        <div className="rv-question-template-editor"><h3>{editingTemplate ? 'Hazır Cevabı Düzenle' : 'Yeni Hazır Cevap'}</h3><label>Başlık<input value={templateTitle} maxLength={120} onChange={event => setTemplateTitle(event.target.value)} placeholder="Örn. Ürün ölçü tablosu" /></label><label>Cevap Metni<textarea rows={4} maxLength={2000} value={templateText} onChange={event => setTemplateText(event.target.value)} placeholder="Müşteriye gönderilecek cevabı yazın" /><small>{templateText.length}/2000</small></label><div className="rv-question-template-actions"><Button variant="secondary" onClick={() => { setEditingTemplate(null); setTemplateTitle(''); setTemplateText('') }} disabled={!editingTemplate && !templateTitle && !templateText}>Temizle</Button><Button onClick={() => saveTemplate.mutate()} disabled={!templateTitle.trim() || !templateText.trim()} loading={saveTemplate.isPending}>{editingTemplate ? 'Değişiklikleri Kaydet' : 'Hazır Cevabı Oluştur'}</Button></div></div>
        <div className="rv-question-template-list"><header><h3>Kayıtlı Cevaplar</h3><span className="qi-count">{templates.data?.length ?? 0}</span></header>{templates.isLoading ? <LoadingState>Hazır cevaplar yükleniyor…</LoadingState> : templates.data?.length ? templates.data.map(item => <article className="rv-question-template-card" key={item.id}><div><strong>{item.title}</strong><p>{item.text}</p><small>Güncellendi: {formatDate(item.updatedAt)}</small></div><div><Button size="sm" variant="secondary" onClick={() => { setEditingTemplate(item); setTemplateTitle(item.title); setTemplateText(item.text) }}>Düzenle</Button><Button size="sm" variant="danger" onClick={() => deleteTemplate.mutate(item)} loading={deleteTemplate.isPending}>Sil</Button></div></article>) : <EmptyState>Henüz hazır cevap oluşturulmadı.</EmptyState>}</div>
      </section>
    </Modal>
  </section>
}
