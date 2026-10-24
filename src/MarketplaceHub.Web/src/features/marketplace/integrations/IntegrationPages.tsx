// Entegrasyonlar (/integrations, /integrations/:id).
import { Fragment, useEffect, useEffectEvent, useLayoutEffect, useRef, useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll, loadAllPages } from '../../../shared/api'
import { connectionEnvironmentLabel, credentialRequestBody, integrationPlatformFilters, readIntegrationListState, stageTestOrderAvailable, trendyolUserAgentNotice, writeIntegrationListState, type IntegrationListState } from './integration-helpers'
import { Busy, ErrorBox, Tabs, UiIcon, useConfirmDialog, DialogSurface } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { NoticeToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { initialImportSummary, type InitialImportView } from '../../../shared/data-freshness'
import { webhookSetupText, type WebhookSetup } from '../webhook-setup'
import { RrModal } from '../returns-ui'
import { externalWriteEvidenceState, externalWriteGroupState } from '../external-write-group-state'
import { lockPageScroll } from '../../../shared/hooks/useScrollLock'
import { appendNotification } from '../../../shared/notifications'
import { supportsSyncPolicy, supportsSyncPolicyManagement, syncIntervalText, syncPolicyIntervalChoices } from '../marketplace-platform-support'
import { syncPolicyHealthPresentation } from '../sync-policy-health'
import { SyncPolicyStalledAlert } from '../sync-policy-stalled-alert'
import { connectionDataResetGroups, connectionDataResetOptions, type ConnectionDataResetScope } from '../connection-data-reset'
import { shopifyStoreDisplayName } from '../shopify-store-name'
import { shouldLoadConnectionCapabilities } from '../connection-capability-query'
import { viewTabLabel } from '../../../shared/view-tabs'
import { formatDateTime, formatIstanbul } from '../../../shared/format-date'
import { Badge, Connection, DateText, Empty, FieldState, IntegrationFeedback, MappingStateGlyph } from '../shared'
import { activationErrorMessage, ConnectionAuthAlert, ConnectionAuthBadge, connectionAuthSummary } from './connection-auth-alert'
// Styles of this route only (their rules are scoped to its own containers).
import '../n11-price-stock-send.css'
import '../integrations-sade.css'
import '../integration-settings-sade.css'

type Capability = { code: string; supportLevel: string; environment: string; storeScope: string; apiVersion: string; sourceUrl: string | null; requiredScope?: string | null; evidenceNote?: string | null; verifiedAt: string | null; verifiedForConnection: boolean }
type SyncPolicy = { id: string; resourceType: string; intervalSeconds: number; overlapSeconds: number; jitterSeconds: number; enabled: boolean; version: number; lastSuccessAt: string | null; lastModifiedWatermark: string | null; lastCursorAdvancedAt?: string | null; cursorStagnantSince?: string | null; cursorProgressStatus?: string; healthStatus?: string; recoveryGapStatus?: string; recoveryGapDays?: number | null; lastAttemptAt?: string | null; lastDurationMs?: number | null; consecutiveFailureCount?: number; lastRequestCount?: number; lastReceivedCount?: number; lastChangedCount?: number; lastInsertedCount?: number; lastUpdatedCount?: number; lastSkippedCount?: number; lastFailedCount?: number; lastRetryCount?: number; lastRateLimitCount?: number; connectionBacklogCount?: number; connectionOldestBacklogAt?: string | null; connectionManualReviewCount?: number; connectionDeadJobCount24h?: number; requiresExternalWrites?: boolean }
function IntegrationFeedbackToast({ feedback, onClose }: { feedback: IntegrationFeedback | null; onClose: () => void }) {
  useEffect(() => {
    if (feedback) appendNotification(feedback.message, feedback.kind, feedback.target, feedback.details)
  }, [feedback])
  if (!feedback) return null
  return <NoticeToast key={`${feedback.kind}:${feedback.message}`} kind={feedback.kind} message={feedback.message} onClose={onClose} durationMs={5000} />
}
const activePlatformCodes = new Set(['TRENDYOL', 'TRENDYOL_EFATURAM', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM'])
// Ravencia's integrator user on the Hepsiburada Partner panel: the form starts with it and keeps it after a save or reload.
const hepsiburadaDefaultIntegratorUsername = 'ravencia_dev'
function credentialLabel(item: Connection) { return item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'Yenileme gerekli' : item.hasCredential ? 'Şifreli kayıtlı' : 'Bekleniyor' }
function hepsiburadaCredentialHelp(item: Connection) {
  if (item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_UNVERIFIED') return 'Bu ortamda HTTP Basic Auth etkin değil; dış istek gönderilmedi.'
  if (item.lastErrorCode === 'HEPSIBURADA_CREDENTIALS_REJECTED' || item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_FAILED') return 'Hepsiburada HTTP 401 verdi: entegratör kullanıcı adı ve servis anahtarı eşleşmiyor veya canlı ortam için değil. Aynı aktif Entegratörlerim kaydındaki kullanıcı adı ile canlı servis anahtarını birlikte yeniden şifreli kaydedin.'
  if (item.lastErrorCode === 'HEPSIBURADA_ACCESS_FORBIDDEN') return 'Hepsiburada erişimi reddetti (403). Mağaza ID’sini ve entegratörün bu mağaza için API yetkisini kontrol edin.'
  return item.hasCredential
    ? 'Hepsiburada HTTP Basic Auth için entegratör kullanıcı adı ile aynı kaydın servis anahtarını kullanır. Mağaza ID’si ayrı kapsam bilgisidir; gizli değerler şifreli saklanır.'
    : 'Hepsiburada servis anahtarı bekleniyor.'
}
function credentialHelp(item: Connection) { return item.platformCode === 'HEPSIBURADA' ? hepsiburadaCredentialHelp(item) : item.platformCode === 'PTTAVM' ? item.hasCredential ? 'API kullanıcı adı ve şifresi şifreli kaydedildi; değerler tekrar gösterilmez.' : 'PTT AVM API kullanıcı adı ve şifresi bekleniyor. PTT AVM satıcı panelinde Hesap Yönetimi → Entegrasyon Bilgileri ekranında yer alır.' : item.platformCode === 'PAZARAMA' ? item.hasCredential ? 'Client Id ve Client Secret şifreli kaydedildi; değerler tekrar gösterilmez.' : 'Pazarama API bilgileri bekleniyor. Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri altından alınır.' : item.platformCode === 'N11' ? item.hasCredential ? 'App Key ve App Secret şifreli kaydedildi; değerler tekrar gösterilmez.' : 'N11 API anahtarı bekleniyor. N11 satıcı panelinde Hesabım → API Hesapları altından alınır.' : item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'E-Faturam hesap e-postası ve parolasıyla şifreli kaydı yenileyin.' : item.platformCode === 'SHOPIFY' ? item.hasCredential ? 'Uygulama tokenı şifreli kaydedildi; değer tekrar gösterilmez. Son 60 günden eski siparişler için read_all_orders izni gerekir.' : 'Shopify uygulama tokenı bekleniyor.' : item.hasCredential ? 'Şifreli kaydedildi; değerler tekrar gösterilmez.' : 'API bilgileri bekleniyor.' }
function connectionTestHelp(item: Connection) {
  if (item.platformCode === 'HEPSIBURADA' && (item.lastErrorCode === 'HEPSIBURADA_CREDENTIALS_REJECTED' || item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_FAILED')) return 'Hepsiburada HTTP 401 verdi. Satıcı Paneli’nde Bilgilerim → Entegrasyon → Entegratör Bilgileri bölümündeki aynı aktif kaydın kullanıcı adı ve canlı servis anahtarını kullanın; servis anahtarını yenilediyseniz burada tekrar şifreli kaydedin.'
  if (item.platformCode === 'HEPSIBURADA' && item.lastErrorCode === 'HEPSIBURADA_ACCESS_FORBIDDEN') return 'Hepsiburada HTTP 403 verdi. Mağaza ID’sini ve entegratörün bu mağazaya/API uçlarına erişim yetkisini kontrol edin.'
  if (item.platformCode === 'HEPSIBURADA' && item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_UNVERIFIED') return 'Kimlik doğrulama yapılandırması etkin olmadığı için istek gönderilmedi.'
  if (item.platformCode === 'HEPSIBURADA') return item.lastErrorCode ?? 'Bağlantı testi en fazla bir sipariş kaydı okur; pazaryerine yazma yapmaz.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_CREDENTIALS_REJECTED') return 'N11 anahtarı reddetti. Satıcı panelinde Hesabım → API Hesapları ekranındaki güncel App Key ve App Secret’ı kaydedip yeniden test edin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_CREDENTIAL_FORMAT_INVALID') return 'Kayıtlı App Key veya App Secret içinde geçersiz karakter var. Değerleri N11 satıcı panelinden yeniden kopyalayıp kaydedin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_REST_ACCESS_DENIED') return 'Anahtarlar doğru ama bu API hesabında N11’in yeni (REST) servisleri kapalı. sellerintegration@n11.com adresine yazıp REST API erişiminin açılmasını isteyin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_STORE_MISMATCH') return 'Bu API anahtarı başka bir N11 mağazasına ait. Mağaza adını N11’deki gibi yazın ya da doğru mağazanın anahtarını girin.'
  if (item.platformCode === 'N11') return item.lastErrorCode ?? 'Bağlantı testi ürün ve sipariş listesinden en fazla birer kayıt okur; N11’e yazma yapmaz.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_CREDENTIALS_REJECTED') return 'Pazarama Client Id ve Client Secret çiftini kabul etmedi. Satıcı panelinde Hesabım → Entegrasyon Bilgileri’ndeki güncel değerleri kaydedip yeniden test edin; orada yeni anahtar üretildiyse eskisi geçersiz olur.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_CREDENTIAL_FORMAT_INVALID') return 'Kayıtlı Client Id veya Client Secret değerinde geçersiz karakter var. Değerleri Pazarama satıcı panelinden yeniden kopyalayıp kaydedin.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_SCOPE_DENIED') return 'Pazarama bu API hesabına entegrasyon yetkisi vermiyor. Pazarama destekle API erişimini açtırın.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_GATEWAY_BLOCKED') return 'Pazarama’nın güvenlik geçidi istekleri engelliyor (API yerine HTML sayfa dönüyor); kimlik bilgileri geçerli. Okumalar 15 dakikada bir yeniden denenir. Sürerse Pazarama destekten sunucu adresinin izinli listeye alınmasını isteyin.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_ACCESS_DENIED') return 'Pazarama bu hesabın API erişimini reddetti (403); Client Id ve Client Secret geçerli, token veriliyor. Erişimi Pazarama destek açar. Okumalar 15 dakikada bir yeniden denenir.'
  if (item.platformCode === 'PAZARAMA') return item.lastErrorCode ?? 'Bağlantı testi ürün, sipariş ve marka listesinden en fazla birer kayıt okur; Pazarama’ya yazma yapmaz.'
  if (item.platformCode === 'PTTAVM' && item.lastErrorCode === 'PTTAVM_CREDENTIALS_REJECTED') return 'PTT AVM API kullanıcı adı ve şifresini kabul etmedi. Satıcı panelinde Hesap Yönetimi → Entegrasyon Bilgileri ekranındaki API Kullanıcı Adı ve API Şifresi’ni olduğu gibi kopyalayıp kaydedin, ardından yeniden test edin; şifre değiştirildiyse eskisi geçersiz olur.'
  if (item.platformCode === 'PTTAVM' && item.lastErrorCode === 'PTTAVM_STORE_MISMATCH') return 'Bu API kullanıcısı başka bir PTT AVM mağazasına ait. Entegrasyon Bilgileri ekranındaki Mağaza ID’yi girin ya da o mağazanın API kullanıcısını kullanın.'
  if (item.platformCode === 'PTTAVM' && item.lastErrorCode === 'PTTAVM_STORE_ID_INVALID') return 'Mağaza ID yalnız rakamlardan oluşmalıdır. Değeri Entegrasyon Bilgileri ekranından kopyalayın.'
  if (item.platformCode === 'PTTAVM' && item.lastErrorCode === 'PTTAVM_GATEWAY_BLOCKED') return 'PTT AVM’nin güvenlik geçidi istekleri engelliyor (servis yerine HTML sayfa dönüyor). Geçici olabilir; okumalar 15 dakika sonra yeniden denenir.'
  if (item.platformCode === 'PTTAVM' && item.lastErrorCode === 'PTTAVM_REQUIRED_READ_CAPABILITY') return 'Kimlik bilgileri kabul edildi ama ürün veya sipariş okuması doğrulanamadı; bağlantı etkinleştirilemez. Ayrıntı yetenek listesinde.'
  if (item.platformCode === 'PTTAVM') return item.lastErrorCode ?? 'Bağlantı testi mağaza bilgisini, ana kategori listesini, son iki günün siparişlerini ve stok listesinin ilk sayfasını okur; PTT AVM’ye yazma yapmaz.'
  if (item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_ACCESS_TOKEN_REJECTED') return 'Giriş başarılı; sağlayıcı sign-in yanıtındaki taze JWT tokenını korumalı fatura API’sinde geçersiz veya süresi dolmuş olarak reddetti.'
  if (item.platformCode === 'SHOPIFY' && item.lastErrorCode === 'SHOPIFY_REQUIRED_READ_SCOPE') return 'Shopify uygulama tokenında read_products, read_inventory, read_orders ve read_locations okuma izinleri açık olmalı. Fiyat/stok dış yazması için write_products ve write_inventory gerekir. Son 60 günden eski siparişler için ayrıca read_all_orders erişimi gerekir. İzinleri açtıktan sonra yeni token oluşturup burada şifreli kaydedin.'
  if (item.platformCode === 'SHOPIFY') return item.lastErrorCode ? `Shopify doğrulaması başarısız: ${item.lastErrorCode}` : 'Mağaza, izinler, para birimi ve depolar son testte kontrol edilir.'
  return item.lastErrorCode ?? 'Hata kaydı yok'
}
function ReferenceActionDialog({ id, title, description, confirmLabel, confirmDisabled, destructive = false, onClose, onConfirm, children }: { id: string; title: string; description: string; confirmLabel: string; confirmDisabled?: boolean; destructive?: boolean; onClose: () => void; onConfirm: () => void; children?: ReactNode }) {
  return <div className="workspace-modal-backdrop reference-action-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal reference-action-modal" role="alertdialog" aria-labelledby={`${id}-title`} onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">BAĞLANTI İŞLEMİ</p><h2 id={`${id}-title`}>{title}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="reference-action-body"><p>{description}</p>{children}</div><footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" className={destructive ? 'destructive' : undefined} onClick={() => onConfirm()} disabled={confirmDisabled}>{confirmLabel}</button></footer></DialogSurface></div>
}
function ConnectionDataResetDialog({ item, scopes, confirmation, busy, onToggleScope, onChangeConfirmation, onClose, onConfirm }: { item: Connection; scopes: ConnectionDataResetScope[]; confirmation: string; busy: boolean; onToggleScope: (scope: ConnectionDataResetScope, checked: boolean) => void; onChangeConfirmation: (value: string) => void; onClose: () => void; onConfirm: () => void }) {
  const groups = connectionDataResetGroups(item.platformCode)
  return <div className="workspace-modal-backdrop reference-action-backdrop" role="presentation" onMouseDown={() => { if (!busy) onClose() }}>
    <DialogSurface as="section" onClose={() => { if (!busy) onClose() }} className="workspace-modal reference-action-modal connection-data-reset-modal" role="alertdialog" aria-labelledby={`reset-data-${item.id}-title`} onMouseDown={event => event.stopPropagation()}>
      <header><div><p className="eyebrow">BAĞLANTI VERİLERİ</p><h2 id={`reset-data-${item.id}-title`}>Verileri Sıfırla</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat" disabled={busy}><UiIcon name="close" /></button></header>
      <div className="reference-action-body">
        <p><strong>{item.displayName}</strong> bağlantısında temizlenecek veri türlerini seçin. İşlem yalnız bu bağlantının yerel kayıtlarını etkiler; pazaryerine yazma yapmaz.</p>
        <fieldset className="connection-data-reset-options">
          <legend>Sıfırlanacak verileri seçin</legend>
          {groups.map(group => <section className="connection-data-reset-group" key={group.label}><div className="connection-data-reset-group-heading"><strong>{group.label}</strong><small>{group.description}</small></div>{group.options.map(option => <label key={option.scope} className="connection-data-reset-option"><input type="checkbox" checked={scopes.includes(option.scope)} onChange={event => onToggleScope(option.scope, event.target.checked)} disabled={busy} /><span><strong>{option.label}</strong><small>{option.description}</small></span></label>)}</section>)}
        </fieldset>
        <label className="reference-action-confirmation"><span>Onay için <b>{item.displayName}</b> yazın</span><input value={confirmation} onChange={event => onChangeConfirmation(event.target.value)} disabled={busy} autoComplete="off" /></label>
      </div>
      <footer><button type="button" className="secondary" onClick={onClose} disabled={busy}>Vazgeç</button><button type="button" className="destructive" onClick={onConfirm} disabled={!scopes.length || confirmation !== item.displayName || busy}>{busy ? 'Sıfırlanıyor…' : 'Seçilen Verileri Sıfırla'}</button></footer>
    </DialogSurface>
  </div>
}
function ReferenceConnectionCard({ item, onSaved, onFeedback }: { item: Connection; onSaved: () => void; onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const efaturam = item.platformCode === 'TRENDYOL_EFATURAM'
  const shopify = item.platformCode === 'SHOPIFY'
  const [menuOpen, setMenuOpen] = useState(false)
  const [disconnectOpen, setDisconnectOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [resetOpen, setResetOpen] = useState(false)
  const [resetScopes, setResetScopes] = useState<ConnectionDataResetScope[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const referenceMenuRef = useRef<HTMLDivElement>(null)
  const referenceMenuPopoverRef = useRef<HTMLDivElement>(null)
  const [menuPlacement, setMenuPlacement] = useState<'down' | 'up'>('down')
  const [menuMaxHeight, setMenuMaxHeight] = useState<number | null>(null)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const connected = item.status === 'ACTIVE' || item.status === 'VERIFIED'
  const hidden = item.status === 'HIDDEN'
  const supportsInitialDataSync = item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'N11'
  // While a first import runs, the card says how far each kind of data has come.
  const initialImport = useQuery({
    queryKey: ['initial-import', item.id],
    queryFn: () => hubPoll<InitialImportView>(`/connections/${item.id}/initial-import`),
    enabled: connected && !hidden && ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM'].includes(item.platformCode),
    refetchInterval: query => query.state.data?.running ? 10_000 : 120_000
  })
  const initialImportText = initialImport.data?.running ? initialImportSummary(initialImport.data.parts) : null
  useEffect(() => {
    if (!settingsOpen) return
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setSettingsOpen(false) }
    const unlockScroll = lockPageScroll()
    document.addEventListener('keydown', closeOnEscape)
    return () => { unlockScroll(); document.removeEventListener('keydown', closeOnEscape) }
  }, [settingsOpen])
  useEffect(() => {
    if (!menuOpen) return
    const closeOnOutsidePointer = (event: globalThis.PointerEvent) => {
      if (!referenceMenuRef.current?.contains(event.target as Node)) setMenuOpen(false)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setMenuOpen(false) }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [menuOpen])
  const test = useMutation({ mutationFn: () => hubApi<{ succeeded?: boolean; errorCode?: string; errorSummary?: string }>(`/connections/${item.id}/test-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: async result => { onFeedback({ kind: result.succeeded ? 'success' : 'error', message: result.succeeded ? 'Bağlantı testi başarılı; yetenek kontrolleri yenilendi. Dış yazma yapılmadı.' : `Bağlantı testi başarısız${result.errorCode ? `: ${result.errorCode}` : '.'}${result.errorSummary ? ` — ${result.errorSummary}` : ''}` }); await Promise.all([client.invalidateQueries({ queryKey: ['connection', item.id] }), client.invalidateQueries({ queryKey: ['capabilities', item.id] }), client.invalidateQueries({ queryKey: ['sync-policies', item.id] })]); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı testi başlatılamadı.' }) })
  const initialDataSync = useMutation({ mutationFn: () => hubApi<{ jobId: string }>(`/connections/${item.id}/initial-data-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: '{}' }), onSuccess: async () => { onFeedback({ kind: 'success', message: `${item.displayName} için platformun izin verdiği en geniş geçmiş aralığını tarayan veri çekimi kuyruğa alındı.` }); await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['returns'] }), client.invalidateQueries({ queryKey: ['products'] }), client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] })]) }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'İlk veri çekimi başlatılamadı.' }) })
  const disconnect = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: false }) }), onSuccess: () => { setDisconnectOpen(false); onFeedback({ kind: 'success', message: 'Bağlantı pasife alındı.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı pasife alınamadı.' }) })
  const activate = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: true }) }), onSuccess: () => { onFeedback({ kind: 'success', message: 'Bağlantı etkinleştirildi.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: activationErrorMessage(reason) }) })
  const deepDelete = useMutation({ mutationFn: () => hubApi(`/connections/${item.id}/deep-delete`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation: deleteConfirmation }) }), onSuccess: () => { setDeleteOpen(false); setDeleteConfirmation(''); onFeedback({ kind: 'success', message: 'Bağlantı ve bağlı veriler silindi.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı ve bağlı veriler silinemedi.' }) })
  const resetData = useMutation({ mutationFn: () => hubApi(`/connections/${item.id}/data-reset`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ scopes: resetScopes, confirmation: resetConfirmation }) }), onSuccess: () => { const labels = connectionDataResetOptions(item.platformCode).filter(option => resetScopes.includes(option.scope)).map(option => option.label.toLocaleLowerCase('tr-TR')).join(', '); setResetOpen(false); setResetScopes([]); setResetConfirmation(''); onFeedback({ kind: 'success', message: `${item.displayName} bağlantısında seçilen veriler sıfırlandı: ${labels}. Otomatik eşitleme açıksa kayıtlar yeniden çekilebilir.` }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Seçilen bağlantı verileri sıfırlanamadı.' }) })
  // A Production connection that passed its test after a change of settings is VERIFIED: it reads, but the marketplace
  // takes nothing from it (invoices, stock, prices) until it is activated, so the menu offers that as well.
  const awaitingActivation = item.status === 'VERIFIED' && item.environment !== 'STAGE'
  const menuActions = [
    ...(awaitingActivation ? [{ key: 'activate', label: 'Bağlantıyı Aktifleştir', disabled: disconnect.isPending || activate.isPending, run: () => { setMenuOpen(false); activate.mutate() } }] : []),
    { key: 'settings', label: 'Bağlantı Ayarlarını Aç', disabled: false, run: () => { setMenuOpen(false); setSettingsOpen(true) } },
    { key: 'connection', label: connected ? 'Bağlantıyı Pasife Al' : 'Bağlantıyı Aktifleştir', disabled: disconnect.isPending || activate.isPending, run: () => { setMenuOpen(false); if (connected) setDisconnectOpen(true); else activate.mutate() } },
    { key: 'test', label: 'Bağlantıyı Kontrol Et', displayLabel: test.isPending ? 'Kontrol ediliyor…' : 'Bağlantıyı Kontrol Et', disabled: test.isPending, run: () => { setMenuOpen(false); test.mutate() } },
    { key: 'delete', label: 'Bağlantıyı Sil', disabled: false, run: () => { setMenuOpen(false); setDeleteOpen(true) } },
    ...(supportsInitialDataSync ? [{ key: 'initial-sync', label: 'Verileri Çek', displayLabel: initialDataSync.isPending ? 'Kuyruğa alınıyor…' : 'Verileri Çek', disabled: !connected || !item.hasCredential || initialDataSync.isPending, run: () => { setMenuOpen(false); initialDataSync.mutate() } }] : []),
    { key: 'reset', label: 'Verileri Sıfırla', disabled: resetData.isPending, run: () => { setMenuOpen(false); setResetScopes([]); setResetConfirmation(''); setResetOpen(true) } },
  ].sort((left, right) => left.label.localeCompare(right.label, 'tr-TR'))
  function updateMenuPlacement(trigger: HTMLButtonElement) {
    const triggerRect = trigger.getBoundingClientRect()
    const estimatedHeight = Math.min(420, menuActions.length * 40 + 12)
    const gap = 8
    const viewportPadding = 12
    const below = Math.max(0, window.innerHeight - triggerRect.bottom - gap - viewportPadding)
    const above = Math.max(0, triggerRect.top - gap - viewportPadding)
    const next = below >= estimatedHeight ? 'down' : above >= estimatedHeight ? 'up' : below >= above ? 'down' : 'up'
    const available = next === 'up' ? above : below
    setMenuPlacement(next)
    setMenuMaxHeight(Math.min(420, available))
  }
  const placeMenu = useEffectEvent((trigger: HTMLButtonElement) => updateMenuPlacement(trigger))
  useLayoutEffect(() => {
    if (!menuOpen) {
      setMenuMaxHeight(null)
      return
    }
    const updatePlacement = () => {
      const trigger = referenceMenuRef.current?.querySelector<HTMLButtonElement>('.reference-menu-trigger')
      if (trigger) placeMenu(trigger)
    }
    updatePlacement()
    window.addEventListener('resize', updatePlacement)
    window.addEventListener('scroll', updatePlacement, true)
    return () => {
      window.removeEventListener('resize', updatePlacement)
      window.removeEventListener('scroll', updatePlacement, true)
    }
  }, [menuOpen])
  function toggleReferenceMenu(event: MouseEvent<HTMLButtonElement>) {
    if (menuOpen) {
      setMenuOpen(false)
      return
    }
    updateMenuPlacement(event.currentTarget)
    setMenuOpen(true)
  }
  const privateDataHidden = hidden || !connected
  const authSummary = connectionAuthSummary(item)
  return <article className={`integration-card reference-integration-card ${hidden ? 'reference-integration-card-hidden' : connected ? 'reference-integration-card-active' : 'reference-integration-card-error'}${menuOpen ? ' reference-integration-card-menu-open' : ''}`} onClickCapture={event => { const target = event.target; if (target instanceof Element && target.closest('.reference-menu-popover a')) { event.preventDefault(); setMenuOpen(false); setSettingsOpen(true) } }}>
    <div className="reference-integration-accent" aria-hidden="true" />
    <header className="reference-integration-header">
      <div className="reference-integration-brand"><img className={`reference-provider-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /><div><h2>{item.displayName}</h2>{authSummary && !hidden ? <ConnectionAuthBadge item={item} /> : <span className={`reference-connection-state ${hidden ? 'hidden' : connected ? 'connected' : 'inactive'}`}><MappingStateGlyph state={connected && !hidden ? 'done' : 'none'} />{hidden ? 'Veriler gizli' : connected ? 'Bağlı ve aktif' : 'Pasif'}</span>}</div></div>
      <div className="reference-integration-menu" ref={referenceMenuRef}><button type="button" className="reference-menu-trigger" aria-label="Entegrasyon seçenekleri" aria-expanded={menuOpen} onClick={toggleReferenceMenu}><UiIcon name="moreVertical" /></button>{menuOpen && <div ref={referenceMenuPopoverRef} className={`reference-menu-popover opens-${menuPlacement}`} style={menuMaxHeight === null ? undefined : { maxHeight: `${menuMaxHeight}px` }} role="menu">{menuActions.map(action => <button type="button" key={action.key} role="menuitem" disabled={action.disabled} onClick={action.run}>{action.displayLabel ?? action.label}</button>)}</div>}</div>
    </header>
      {initialImportText && !privateDataHidden && <p className="reference-initial-import" role="status">{initialImportText}</p>}
      {privateDataHidden ? <div className="reference-integration-body is-passive" role="status" aria-label={hidden ? 'Veriler gizli' : 'Bağlantı pasif; bilgiler gizli'}><div className="reference-data-point"><span>{efaturam ? 'VKN / TCKN' : shopify ? 'Shopify mağazası' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada mağaza ID' : 'Mağaza Kimliği (Seller ID)'}</span><strong>Gizli</strong></div><div className="reference-data-point"><span>Kimlik bilgileri</span><strong>Gizli</strong></div><div className="reference-data-point"><span>Ortam</span><strong>{connectionEnvironmentLabel(item.platformCode, item.environment)}</strong></div><div className="reference-data-point"><span>Dış yazma</span><strong>Kapalı</strong></div></div> : <div className="reference-integration-body"><div className="reference-data-point"><span>{efaturam ? 'VKN / TCKN' : shopify ? 'Shopify mağazası' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada mağaza ID' : 'Mağaza Kimliği (Seller ID)'}</span><strong>{shopify ? shopifyStoreDisplayName(item.externalStoreId) || 'Tanımlanmadı' : item.externalStoreId || 'Tanımlanmadı'}</strong></div><div className="reference-data-point"><span>Kimlik bilgileri</span><strong>{credentialLabel(item)}</strong></div><div className="reference-data-point"><span>Ortam</span><strong>{connectionEnvironmentLabel(item.platformCode, item.environment)}</strong></div><div className="reference-data-point"><span>Dış yazma</span><strong>{item.externalWritesEnabled ? 'Açık' : 'Salt-okunur'}</strong></div></div>}
    <footer className="reference-integration-footer"><span>{shopify ? 'E-Ticaret' : efaturam ? 'E-Fatura' : 'Pazaryeri'}</span><div className="reference-footer-actions">{authSummary && item.hasCredential && <button type="button" className="reference-action reference-action-test" onClick={() => test.mutate()} disabled={test.isPending}>{test.isPending ? 'Test Ediliyor…' : 'Test Et'}</button>}<button type="button" className="reference-action" onClick={() => setSettingsOpen(true)} disabled={disconnect.isPending || activate.isPending || test.isPending}><UiIcon name="settings" /> Yönet</button></div></footer>
    {disconnectOpen && <ReferenceActionDialog id={`disconnect-${item.id}`} title="Bağlantıyı Pasife Al" description="Bağlantı pasife alınacak ve canlı veri akışı duracaktır." confirmLabel={disconnect.isPending ? 'Pasife alınıyor…' : 'Onayla'} confirmDisabled={disconnect.isPending} onClose={() => setDisconnectOpen(false)} onConfirm={() => disconnect.mutate()} />}
     {deleteOpen && <ReferenceActionDialog id={`delete-${item.id}`} title="Bağlantıyı Sil" description="Bağlantı ve bu mağazaya bağlı sipariş, iade, fatura, ürün ve eşleştirme kayıtları kalıcı olarak silinir." confirmLabel={deepDelete.isPending ? 'Temizleniyor…' : 'Kalıcı Olarak Sil'} confirmDisabled={deleteConfirmation !== item.displayName || deepDelete.isPending} destructive onClose={() => { setDeleteOpen(false); setDeleteConfirmation('') }} onConfirm={() => deepDelete.mutate()}><label className="reference-action-confirmation">Onay için <b>{item.displayName}</b> yazın<input value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} /></label></ReferenceActionDialog>}
    {resetOpen && <ConnectionDataResetDialog item={item} scopes={resetScopes} confirmation={resetConfirmation} busy={resetData.isPending} onToggleScope={(scope, checked) => setResetScopes(current => checked ? [...new Set([...current, scope])] : current.filter(value => value !== scope))} onChangeConfirmation={setResetConfirmation} onClose={() => { setResetOpen(false); setResetScopes([]); setResetConfirmation('') }} onConfirm={() => resetData.mutate()} />}
    {settingsOpen && <div className="integration-settings-modal-backdrop" role="presentation" onMouseDown={() => setSettingsOpen(false)}><DialogSurface as="section" onClose={() => setSettingsOpen(false)} className="integration-settings-modal" aria-label={`${item.displayName} bağlantı ayarları`} onMouseDown={event => event.stopPropagation()}><IntegrationDetailWorkspace id={item.id} inline onClose={() => setSettingsOpen(false)} /></DialogSurface></div>}
  </article>
}
export function IntegrationsPage() {
  const client = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const [createPlatform, setCreatePlatform] = useState('TRENDYOL')
  const [trendyolSellerDraft, setTrendyolSellerDraft] = useState('')
  const [trendyolUserAgentDraft, setTrendyolUserAgentDraft] = useState('')
  const userAgentNotice = trendyolUserAgentNotice(trendyolUserAgentDraft, trendyolSellerDraft)
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const [filterOpen, setFilterOpen] = useState(false)
  // The view, status and platform filters live in the address (W16): Back and a refresh keep them.
  const [searchParams, setSearchParams] = useSearchParams()
  const { view, filter, platform: platformFilter } = readIntegrationListState(searchParams)
  const updateList = (patch: Partial<IntegrationListState>) => setSearchParams(current => writeIntegrationListState(current, patch), { replace: true })
  const setView = (next: IntegrationListState['view']) => updateList({ view: next })
  const setFilter = (next: IntegrationListState['filter']) => updateList({ filter: next })
  const setPlatformFilter = (next: IntegrationListState['platform']) => updateList({ platform: next })
  useEffect(() => {
    if (!feedback) return
    const timeout = window.setTimeout(() => setFeedback(null), 5000)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  useEffect(() => {
    if (!filterOpen) return
    const closeOnOutsideClick = (event: globalThis.MouseEvent) => {
      const target = event.target
      if (target instanceof Element && !target.closest('.integration-filter-wrap')) setFilterOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => document.removeEventListener('mousedown', closeOnOutsideClick)
  }, [filterOpen])
  const query = useQuery({ queryKey: ['connections'], queryFn: () => loadAllPages<Connection>('/connections') })
  const activeConnections = query.data?.items.filter(item => activePlatformCodes.has(item.platformCode)) ?? []
  const visibleConnections = activeConnections.filter(item => (view === 'ALL' || item.status === 'ACTIVE' || item.status === 'VERIFIED') && (platformFilter === 'ALL' || item.platformCode === platformFilter) && (filter === 'ALL' || (filter === 'ACTIVE' ? item.status === 'ACTIVE' || item.status === 'VERIFIED' : item.status !== 'ACTIVE' && item.status !== 'VERIFIED' || Boolean(item.lastErrorCode))))
  const filterLabel = `${view === 'ACTIVE' ? 'Bağlı Olanlar' : 'Tümü'} · ${platformFilter === 'ALL' ? 'Tüm Platformlar' : platformFilter === 'TRENDYOL' ? 'Trendyol' : platformFilter === 'SHOPIFY' ? 'Shopify' : platformFilter === 'HEPSIBURADA' ? 'Hepsiburada' : platformFilter === 'N11' ? 'N11' : platformFilter === 'PAZARAMA' ? 'Pazarama' : platformFilter === 'PTTAVM' ? 'PTT AVM' : 'Trendyol E-Faturam'} · ${filter === 'ACTIVE' ? 'Aktif Bağlantılar' : filter === 'ATTENTION' ? 'Dikkat Gerekenler' : 'Tüm Durumlar'}`
  const create = useMutation({ mutationFn: (body: object) => hubApi('/connections', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(body) }), onSuccess: () => { setCreateOpen(false); setFeedback({ kind: 'success', message: 'Bağlantı oluşturuldu. Gönderilen kimlik bilgileri varsa şifreli saklandı; doğrulama isteği ayrıca başlatılmalıdır.' }); void client.invalidateQueries({ queryKey: ['connections'] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı oluşturulamadı.' }) })
  // Only an identity that cannot be sent at all blocks the form; another format stays a warning (owner decision).
  const userAgentBlocksCreate = createPlatform === 'TRENDYOL' && userAgentNotice?.kind === 'error'
  function submitCreate(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (userAgentBlocksCreate) return; const data = new FormData(event.currentTarget); create.mutate({ displayName: data.get('displayName'), environment: createPlatform === 'SHOPIFY' || createPlatform === 'N11' || createPlatform === 'PAZARAMA' || createPlatform === 'PTTAVM' ? 'PRODUCTION' : data.get('environment'), externalStoreId: data.get('externalStoreId'), platformCode: createPlatform, apiVersion: createPlatform === 'TRENDYOL' ? 'V2' : createPlatform === 'SHOPIFY' ? '2026-10' : createPlatform === 'HEPSIBURADA' ? 'V1.0' : createPlatform === 'N11' || createPlatform === 'PAZARAMA' || createPlatform === 'PTTAVM' ? 'V1' : '1.0.0', userAgentIdentity: createPlatform === 'TRENDYOL' ? data.get('userAgentIdentity') : null, shopifyAccessToken: createPlatform === 'SHOPIFY' ? data.get('shopifyAccessToken') : null, hepsiburadaIntegratorUsername: createPlatform === 'HEPSIBURADA' ? data.get('hepsiburadaIntegratorUsername') : null, hepsiburadaServiceKey: createPlatform === 'HEPSIBURADA' ? data.get('hepsiburadaServiceKey') : null, n11AppKey: createPlatform === 'N11' ? data.get('n11AppKey') : null, n11AppSecret: createPlatform === 'N11' ? data.get('n11AppSecret') : null, pazaramaClientId: createPlatform === 'PAZARAMA' ? data.get('pazaramaClientId') : null, pazaramaClientSecret: createPlatform === 'PAZARAMA' ? data.get('pazaramaClientSecret') : null, pttAvmUsername: createPlatform === 'PTTAVM' ? data.get('pttAvmUsername') : null, pttAvmPassword: createPlatform === 'PTTAVM' ? data.get('pttAvmPassword') : null }) }
  return <section className="content f3 integrations-reference-page">
    <div className="integration-reference-heading rv-page-head"><div><p className="eyebrow">Bağlantılar</p><h1>Entegrasyonlar</h1><p className="rv-page-lead">Pazaryeri ve e-fatura bağlantılarınızı yönetin, eşitleme ayarlarını düzenleyin.</p></div><div className="integration-heading-actions"><div className="integration-filter-wrap"><button type="button" className="integration-filter-button" aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><UiIcon name="listSortAscending" /> Filtrele</button>{filterOpen && <div className="integration-filter-menu" role="menu" aria-label="Platform ve bağlantı durumu filtresi"><strong>Bağlantı Durumu</strong>{(['ALL', 'ACTIVE', 'ATTENTION'] as const).map(value => <button type="button" role="menuitemradio" aria-checked={filter === value} key={value} className={filter === value ? 'active' : ''} onClick={() => setFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm Durumlar' : value === 'ACTIVE' ? 'Aktif Bağlantılar' : 'Dikkat Gerekenler'}</button>)}<strong className="integration-filter-menu-group">Platform</strong>{integrationPlatformFilters.map(value => <button type="button" role="menuitemradio" aria-checked={platformFilter === value} key={value} className={platformFilter === value ? 'active' : ''} onClick={() => setPlatformFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm Platformlar' : value === 'TRENDYOL' ? 'Trendyol' : value === 'SHOPIFY' ? 'Shopify' : value === 'HEPSIBURADA' ? 'Hepsiburada' : value === 'N11' ? 'N11' : value === 'PAZARAMA' ? 'Pazarama' : value === 'PTTAVM' ? 'PTT AVM' : 'Trendyol E-Faturam'}</button>)}</div>}</div><button type="button" className="integration-add-button" onClick={() => setCreateOpen(true)}><UiIcon name="plus" /> Ekle</button></div></div>
    <Tabs className="integration-view-tabs rv-view-tabs" ariaLabel="Entegrasyon görünümü" value={view} onChange={nextValue => setView(nextValue as 'ALL' | 'ACTIVE')} items={[{ value: 'ALL', label: viewTabLabel('all', 'Tümü') }, { value: 'ACTIVE', label: viewTabLabel('connected', 'Bağlı Olanlar') }]} />
    {query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} onRetry={() => void query.refetch()} /> : <>
      <div className="integration-filter-summary">{filterLabel}<span>{visibleConnections.length} bağlantı</span></div>
      <div className="integration-grid">{visibleConnections.map(item => <ReferenceConnectionCard item={item} onFeedback={setFeedback} onSaved={() => { void client.invalidateQueries({ queryKey: ['connections'] }); void client.invalidateQueries({ queryKey: ['orders'] }); void client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] }); void client.invalidateQueries({ queryKey: ['dashboard'] }) }} key={item.id} />)}</div>
      {!visibleConnections.length && <Empty>Bu filtreye uyan bağlantı bulunmuyor.</Empty>}
      {createOpen && <div className="integration-modal-backdrop" role="presentation" onMouseDown={() => setCreateOpen(false)}><DialogSurface as="form" onClose={() => setCreateOpen(false)} className="integration-modal im-sade" aria-label="Yeni bağlantı" onSubmit={submitCreate} onMouseDown={event => event.stopPropagation()}>
        <div className="integration-modal-header"><div><h2>Yeni Bağlantı</h2><p>Platformu seçin, mağaza ve API bilgilerini girin.</p></div><button type="button" className="modal-close" onClick={() => setCreateOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></div>
        <div className="im-platforms" role="radiogroup" aria-label="Platform">{([['TRENDYOL', 'Trendyol'], ['SHOPIFY', 'Shopify'], ['HEPSIBURADA', 'Hepsiburada'], ['N11', 'N11'], ['PAZARAMA', 'Pazarama'], ['PTTAVM', 'PTT AVM'], ['TRENDYOL_EFATURAM', 'E-Faturam']] as const).map(([code, name]) => <button type="button" role="radio" aria-checked={createPlatform === code} key={code} className={createPlatform === code ? 'is-on' : ''} onClick={() => setCreatePlatform(code)}><img className={platformLogoClass(code)} src={platformLogoSource(code) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /><span>{name}</span></button>)}</div><div className="im-fields">
        <label>{createPlatform === 'HEPSIBURADA' ? 'Mağaza Adı' : 'Bağlantı Adı'}<input name="displayName" placeholder={createPlatform === 'TRENDYOL' ? 'Örn. Trendyol Canlı' : createPlatform === 'HEPSIBURADA' ? 'Hepsiburada mağaza adı' : undefined} required /></label>
        {createPlatform === 'SHOPIFY' || createPlatform === 'N11' || createPlatform === 'PAZARAMA' || createPlatform === 'PTTAVM' ? null : <label>Ortam<select name="environment" defaultValue="STAGE"><option value="STAGE">Test Ortamı (Stage / SIT)</option><option value="PRODUCTION">Canlı</option></select></label>}
        <label>{createPlatform === 'TRENDYOL' ? 'Mağaza Kimliği' : createPlatform === 'SHOPIFY' ? 'Shopify Mağaza Adresi' : createPlatform === 'HEPSIBURADA' ? 'Hepsiburada Mağaza ID' : createPlatform === 'N11' ? 'N11 Mağaza Adı' : createPlatform === 'PAZARAMA' ? 'Pazarama Mağaza Adı' : createPlatform === 'PTTAVM' ? 'Mağaza ID' : 'Yerel Bağlantı Kapsamı'}<input name="externalStoreId" placeholder={createPlatform === 'TRENDYOL' ? 'Mağaza ID' : createPlatform === 'SHOPIFY' ? 'magazaniz.myshopify.com' : createPlatform === 'HEPSIBURADA' ? 'Mağaza ID' : createPlatform === 'N11' ? 'N11’deki mağaza adınız' : createPlatform === 'PAZARAMA' ? 'Pazarama’daki mağaza adınız' : createPlatform === 'PTTAVM' ? 'Örn. 123456' : 'Ravencia - Ravencia'} inputMode={createPlatform === 'PTTAVM' ? 'numeric' : undefined} pattern={createPlatform === 'PTTAVM' ? '[0-9]+' : undefined} title={createPlatform === 'PTTAVM' ? 'Mağaza ID yalnız rakamlardan oluşur.' : undefined} onChange={event => setTrendyolSellerDraft(event.currentTarget.value)} required />{createPlatform === 'PAZARAMA' && <small>Panelde bağlantıyı ayırt etmek için kullanılır; Pazarama API’si mağaza adını doğrulamaz.</small>}{createPlatform === 'PTTAVM' && <small>PTT AVM satıcı paneli › Hesap Yönetimi › Entegrasyon Bilgileri ekranındaki Mağaza ID. Yalnız rakam; PTT AVM’nin test ortamı yok, bağlantı canlı mağazaya kurulur.</small>}{createPlatform === 'N11' && <small>N11’de görünen mağaza adını yazın. Bağlantı testi API anahtarının bu mağazaya ait olduğunu kontrol eder.</small>}{createPlatform === 'SHOPIFY' && <small>Mağaza kısa adı veya tam myshopify.com adresi kullanılabilir.</small>}{createPlatform === 'HEPSIBURADA' && <small>Hepsiburada Entegratör Bilgileri ekranındaki mağaza ID’yi girin. SIT ve canlı bağlantılar için ayrı kayıt oluşturun.</small>}</label>
        {createPlatform === 'SHOPIFY' && <label>Shopify Uygulama Tokenı<input name="shopifyAccessToken" type="password" autoComplete="off" placeholder="shpat_…" required /><small>Shopify Admin API tokenı şifreli saklanır ve tekrar gösterilmez.</small></label>}
        {createPlatform === 'HEPSIBURADA' && <><label>Hepsiburada Entegratör Kullanıcı Adı<input name="hepsiburadaIntegratorUsername" autoComplete="username" defaultValue={hepsiburadaDefaultIntegratorUsername} required /><small>Hepsiburada Partner panelinde bu mağaza için eklediğiniz Entegratör Bilgileri kullanıcı adı. Mağaza ID’sinden farklıdır.</small></label><label>Hepsiburada Servis Anahtarı<input name="hepsiburadaServiceKey" type="password" autoComplete="new-password" required /><small>Servis anahtarı aynı entegratör kaydından alınmalıdır. Her iki değer şifreli saklanır.</small></label></>}
        {createPlatform === 'PAZARAMA' && <><label>Pazarama Client ID<input name="pazaramaClientId" autoComplete="off" required /><small>Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri altından alınır.</small></label><label>Pazarama Client Secret<input name="pazaramaClientSecret" type="password" autoComplete="new-password" required /><small>Şifreli saklanır ve tekrar gösterilmez. Pazarama bu değeri yalnız bir kez gösterir; panelde yeni anahtar üretilirse eskisi anında geçersiz olur.</small></label></>}
        {createPlatform === 'PTTAVM' && <><label>API Kullanıcı Adı<input name="pttAvmUsername" autoComplete="off" spellCheck={false} required /><small>Aynı ekrandaki API KULLANICI ADI.</small></label><label>API Şifresi<input name="pttAvmPassword" type="password" autoComplete="new-password" required /><small>Aynı ekrandaki API ŞİFRESİ. Şifreli saklanır ve tekrar gösterilmez.</small></label></>}
        {createPlatform === 'N11' && <><label>N11 API Anahtarı (App Key)<input name="n11AppKey" autoComplete="off" required /><small>N11 satıcı panelinde Hesabım → API Hesapları altından alınır.</small></label><label>N11 API Şifresi (App Secret)<input name="n11AppSecret" type="password" autoComplete="new-password" required /><small>Her iki değer şifreli saklanır ve tekrar gösterilmez.</small></label></>}
        {createPlatform === 'TRENDYOL' && <label>User-Agent Kimliği<input name="userAgentIdentity" placeholder="SatıcıID - FirmaAdı" aria-invalid={userAgentNotice?.kind === 'error' || undefined} onChange={event => setTrendyolUserAgentDraft(event.currentTarget.value)} required /><small role={userAgentNotice ? 'status' : undefined} className={userAgentNotice ? `im-field-notice is-${userAgentNotice.kind}` : undefined}>{userAgentNotice?.message ?? 'Trendyol’un istediği biçim: “SatıcıID - FirmaAdı”, örn. 123456 - Ravencia.'}</small></label>}
        </div><div className="im-foot"><button type="button" className="secondary" onClick={() => setCreateOpen(false)}>Vazgeç</button><button type="submit" className="connection-button" disabled={create.isPending || userAgentBlocksCreate} title={userAgentBlocksCreate ? userAgentNotice?.message : undefined}>{create.isPending ? 'Oluşturuluyor…' : 'Bağlantıyı Oluştur'}</button></div>
      </DialogSurface></div>}
    </>}
     {feedback && <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />}
  </section>
}
type SyncPolicyEditorModalProps = {
  policy: SyncPolicy
  label: string
  intervals: Array<[number, string]>
  overlaps: Array<[number, string]>
  healthHelp: string
  purpose: string
  externalWriteBlocked: boolean
  saving: boolean
  running: boolean
  onClose: () => void
  onSave: (values: { intervalSeconds: number; overlapSeconds: number; enabled: boolean }) => void
  onRunNow: () => void
}
function SyncPolicyEditorModal({ policy, label, intervals, overlaps, healthHelp, purpose, externalWriteBlocked, saving, running, onClose, onSave, onRunNow }: SyncPolicyEditorModalProps) {
  const availableIntervals = syncPolicyIntervalChoices(intervals, policy.requiresExternalWrites === true)
  const minimumInterval = policy.requiresExternalWrites === true ? 0 : 30
  const [intervalSeconds, setIntervalSeconds] = useState(Math.max(minimumInterval, policy.intervalSeconds))
  const [overlapSeconds, setOverlapSeconds] = useState(policy.overlapSeconds)
  const [enabled, setEnabled] = useState(policy.enabled)
  const effectiveEnabled = enabled && !externalWriteBlocked
  const number = (value: number | undefined) => (value ?? 0).toLocaleString('tr-TR')
  const timeLabel = (value: string | null | undefined) => value ? <DateText value={value ?? null} /> : 'Henüz yok'
  const recoveryGapLabel = ({ OK: 'Normal', WARNING: 'Uyarı', CRITICAL: 'Kritik', UNKNOWN: 'Bilinmiyor' } as Record<string, string>)[policy.recoveryGapStatus ?? 'UNKNOWN'] ?? 'Bilinmiyor'
  const cursorProgressLabel = ({ UNKNOWN: 'Henüz ölçülemedi', NO_DATA: 'Bu kontrolde yeni kayıt yok', CURRENT: 'İmleç ilerliyor', STALLED: 'Kayıt geliyor, imleç ilerlemedi' } as Record<string, string>)[policy.cursorProgressStatus ?? 'UNKNOWN'] ?? 'Henüz ölçülemedi'
  const cursorStalled = policy.cursorProgressStatus?.trim().toUpperCase() === 'STALLED'
  return <div className="workspace-modal-backdrop sync-policy-modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><DialogSurface as="section" onClose={() => { onClose() }} className="workspace-modal sync-policy-modal sp-sade" aria-label={`${label} ayarları`} onMouseDown={event => event.stopPropagation()}><header><div><h2>{label}</h2><p>{purpose}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Akış ayarlarını kapat"><UiIcon name="close" /></button></header><form className="sync-policy-modal-form" onSubmit={event => { event.preventDefault(); onSave({ intervalSeconds, overlapSeconds, enabled }) }}><div className="sync-policy-modal-body"><label className="sync-policy-modal-state cs-switch-row"><span><strong>Otomatik Çalışma</strong><small>{externalWriteBlocked ? 'Dış yazma kapalı olduğu için bu akış çalıştırılmaz.' : healthHelp}</small></span><em className="cs-switch-state">{effectiveEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" className="cs-switch" checked={effectiveEnabled} onChange={event => setEnabled(event.target.checked)} disabled={externalWriteBlocked} /></label><div className="sync-policy-modal-fields"><label><span>Kontrol Sıklığı</span><select value={intervalSeconds} onChange={event => setIntervalSeconds(Number(event.target.value))} disabled={externalWriteBlocked}>{!availableIntervals.some(([value]) => value === intervalSeconds) && <option value={intervalSeconds}>{syncIntervalText(intervalSeconds)}</option>}{availableIntervals.map(([value, optionLabel]) => <option key={value} value={value}>{optionLabel}</option>)}</select></label><label><span>Tekrar Payı</span><select value={overlapSeconds} onChange={event => setOverlapSeconds(Number(event.target.value))} disabled={externalWriteBlocked}>{!overlaps.some(([value]) => value === overlapSeconds) && <option value={overlapSeconds}>{Math.round(overlapSeconds / 60)} dakika</option>}{overlaps.map(([value, optionLabel]) => <option key={value} value={value}>{optionLabel}</option>)}</select></label></div>{cursorStalled && <SyncPolicyStalledAlert cursorStagnantSince={policy.cursorStagnantSince} lastCursorAdvancedAt={policy.lastCursorAdvancedAt} />}<section className="sync-policy-modal-telemetry" aria-label="Senkronizasyon ölçümleri"><h3>Son Senkronizasyon Ölçümü</h3><dl><div><dt>Son Deneme</dt><dd>{timeLabel(policy.lastAttemptAt)}</dd></div><div><dt>Son Başarılı</dt><dd>{timeLabel(policy.lastSuccessAt)}</dd></div><div><dt>Son İmleç İlerlemesi</dt><dd>{timeLabel(policy.lastCursorAdvancedAt)}</dd></div><div><dt>Durgunluk Takibi Başlangıcı</dt><dd>{timeLabel(policy.cursorStagnantSince)}</dd></div><div><dt>İmleç Durumu</dt><dd>{cursorProgressLabel}</dd></div><div><dt>Süre</dt><dd>{policy.lastDurationMs == null ? '—' : `${number(policy.lastDurationMs)} ms`}</dd></div><div><dt>Ardışık Hata</dt><dd>{number(policy.consecutiveFailureCount)}</dd></div><div><dt>API İsteği</dt><dd>{number(policy.lastRequestCount)}</dd></div><div><dt>Alınan / Değişen</dt><dd>{number(policy.lastReceivedCount)} / {number(policy.lastChangedCount)}</dd></div><div><dt>Eklenen / Güncellenen</dt><dd>{number(policy.lastInsertedCount)} / {number(policy.lastUpdatedCount)}</dd></div><div><dt>Atlanan / Hatalı</dt><dd>{number(policy.lastSkippedCount)} / {number(policy.lastFailedCount)}</dd></div><div><dt>Tekrar / HTTP 429</dt><dd>{number(policy.lastRetryCount)} / {number(policy.lastRateLimitCount)}</dd></div><div><dt>Kurtarma Aralığı</dt><dd>{recoveryGapLabel}{policy.recoveryGapDays == null ? '' : ` · ${number(Math.round(policy.recoveryGapDays * 10) / 10)} gün`}</dd></div></dl></section><section className="sync-policy-modal-telemetry sync-policy-modal-backlog" aria-label="Bağlantı kuyruğu"><h3>Bağlantı Kuyruğu · Tüm Akışlar</h3><dl><div><dt>Açık Kuyruk İşi</dt><dd>{number(policy.connectionBacklogCount)}</dd></div><div><dt>En Eski Açık İş</dt><dd>{timeLabel(policy.connectionOldestBacklogAt)}</dd></div><div><dt>Manuel İnceleme</dt><dd>{number(policy.connectionManualReviewCount)}</dd></div><div><dt>Son 24 Saatte Ölen İş</dt><dd>{number(policy.connectionDeadJobCount24h)}</dd></div></dl></section></div><footer><button type="button" className="secondary sync-policy-run-now" disabled={running || externalWriteBlocked} title={externalWriteBlocked ? 'Pazaryerine yazma kapalı.' : 'Akışı beklemeden şimdi çalıştırır.'} onClick={onRunNow}>{running ? 'Başlatılıyor…' : 'Şimdi Çalıştır'}</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="submit" disabled={saving || externalWriteBlocked}>{saving ? 'Kaydediliyor…' : 'Ayarı Kaydet'}</button></footer></form></DialogSurface></div>
}
// Pazarama: close mapped products for sale (or reopen them) by barcode. A closed product takes no orders and no price/stock updates.
type InstantNotificationStatus = { state: 'UNSUPPORTED' | 'OFF' | 'ACTIVE' | 'PASSIVE' | 'MISSING' | 'UNKNOWN' | 'WAITING' | 'QUIET' | 'CONNECTION_INACTIVE'; endpoint: string | null; registeredAt: string | null; lastReceivedAt: string | null; receivedLast24Hours: number; message: string | null; setup?: WebhookSetup | null; lastRejectedAt?: string | null; lastRejectedReason?: string | null; previousPreparedAt?: string | null; adoptableFrom?: { connectionName: string; environment: string; preparedAt: string } | null }
const instantNotificationStateLabels: Record<InstantNotificationStatus['state'], string> = { UNSUPPORTED: 'Desteklenmiyor', OFF: 'Kapalı', ACTIVE: 'Açık', PASSIVE: 'Trendyol durdurdu', MISSING: 'Trendyol’da kayıt yok', UNKNOWN: 'Durum okunamadı', WAITING: 'İlk bildirim bekleniyor', QUIET: 'Son saatlerde bildirim yok', CONNECTION_INACTIVE: 'Bağlantı pasif' }
// Order notifications (webhook). Trendyol: turning them on registers the panel's address at
// Trendyol, so it is done only here, after the user confirms what will be registered.
// Hepsiburada registers by hand: the panel prepares the address and credentials and shows, once,
// the text the seller sends Hepsiburada; nothing is sent to Hepsiburada.
function InstantNotificationsPanel({ connectionId, connectionActive, platformCode }: { connectionId: string; connectionActive: boolean; platformCode: string }) {
  const hepsiburada = platformCode === 'HEPSIBURADA'
  const shopify = platformCode === 'SHOPIFY'
  const marketplaceName = shopify ? 'Shopify' : hepsiburada ? 'Hepsiburada' : 'Trendyol'
  const [setup, setSetup] = useState<WebhookSetup | null>(null)
  const [clientSecret, setClientSecret] = useState('')
  const client = useQueryClient()
  const status = useQuery({ queryKey: ['instant-notifications', connectionId], queryFn: () => hubApi<InstantNotificationStatus>(`/connections/${connectionId}/instant-notifications`), staleTime: 30_000 })
  const [confirm, setConfirm] = useState<'on' | 'off' | 'restore' | 'adopt' | null>(null)
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const change = useMutation({
    mutationFn: (on: boolean) => hubApi<InstantNotificationStatus>(`/connections/${connectionId}/instant-notifications`, on
      ? { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, ...(shopify && clientSecret.trim() ? { body: JSON.stringify({ clientSecret: clientSecret.trim() }) } : {}) }
      : { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey() } }),
    onSuccess: (next, on) => {
      client.setQueryData(['instant-notifications', connectionId], { ...next, setup: null })
      setConfirm(null)
      setSetup(on ? next.setup ?? null : null)
      setClientSecret('')
      setFeedback({ kind: 'success', message: on
        ? hepsiburada ? 'Webhook bilgileri hazırlandı. Aşağıdaki metni Hepsiburada’ya iletin; şifre yalnız şimdi gösteriliyor.' : `Webhook açıldı. ${marketplaceName} yeni siparişleri ve durum değişikliklerini artık doğrudan panele gönderecek.`
        : 'Webhook kapatıldı. Siparişler düzenli kontrolle gelmeye devam ediyor.' })
    },
    onError: reason => { setConfirm(null); setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Webhook ayarı değiştirilemedi.' }) }
  })
  // Hepsiburada keeps the address and password it was given: a definition closed and prepared again before
  // Hepsiburada had the new one is made the active one again, so Hepsiburada need not set it up again.
  const restore = useMutation({
    mutationFn: () => hubApi<InstantNotificationStatus>(`/connections/${connectionId}/instant-notifications`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ restorePrevious: true }) }),
    onSuccess: next => {
      client.setQueryData(['instant-notifications', connectionId], { ...next, setup: null })
      setConfirm(null)
      setSetup(null)
      setFeedback({ kind: 'success', message: 'Önceki tanım geri yüklendi. Hepsiburada’ya ilettiğiniz adres ve şifre yeniden geçerli.' })
    },
    onError: reason => { setConfirm(null); setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Önceki tanım geri yüklenemedi.' }) }
  })
  // The definition a test (SIT) store was given, when Hepsiburada was asked to set that one up for the live store: it
  // moves here with its address, user and password; this connection's own unused definition is kept to restore.
  const adopt = useMutation({
    mutationFn: () => hubApi<InstantNotificationStatus>(`/connections/${connectionId}/instant-notifications`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ adoptFromInactive: true }) }),
    onSuccess: next => {
      client.setQueryData(['instant-notifications', connectionId], { ...next, setup: null })
      setConfirm(null)
      setSetup(null)
      setFeedback({ kind: 'success', message: 'Test mağazasının tanımı bu bağlantıya taşındı. Hepsiburada’ya ilettiğiniz adres ve şifre artık bu bağlantıya bildirim getirir.' })
    },
    onError: reason => { setConfirm(null); setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Tanım taşınamadı.' }) }
  })
  const busy = change.isPending || restore.isPending || adopt.isPending
  const data = status.data
  const registered = data != null && data.state !== 'OFF' && data.state !== 'UNSUPPORTED'
  const time = (value: string | null) => value ? formatIstanbul(value, { dateStyle: 'medium', timeStyle: 'short' }) : '—'
  return <section className="panel instant-notifications-panel">
    <div className="cs-sec-head"><h3>Webhook</h3><p>{hepsiburada
      ? 'Hepsiburada webhook’u satıcının talebiyle tanımlar. Panel adresi ve giriş bilgilerini hazırlar; siz Hepsiburada’ya iletirsiniz. Düzenli kontrol yedek olarak sürer.'
      : `Açıkken ${marketplaceName} yeni siparişleri ve sipariş durumu değişikliklerini beklemeden panele gönderir. Düzenli kontrol yedek olarak sürer.`}</p></div>
    {status.isLoading && <Busy />}
    {status.isError && <ErrorBox error={status.error} onRetry={() => void status.refetch()} />}
    {data && <>
      <div className="instant-notifications-status" data-state={data.state}>
        <div><small>Durum</small><strong><i aria-hidden="true" />{instantNotificationStateLabels[data.state]}</strong></div>
        {data.state !== 'UNSUPPORTED' && <div className="instant-notifications-actions">
        {hepsiburada && data.adoptableFrom && <button type="button" className="secondary" disabled={!connectionActive || busy} onClick={() => setConfirm('adopt')}>Test Mağazasının Tanımını Kullan</button>}
        {hepsiburada && data.previousPreparedAt && <button type="button" className="secondary" disabled={!connectionActive || busy} onClick={() => setConfirm('restore')}>Önceki Tanımı Geri Yükle</button>}
        {registered
          ? <button type="button" className="secondary" disabled={busy} onClick={() => setConfirm('off')}>Webhook’u Kapat</button>
          : <button type="button" disabled={!connectionActive || busy || !data.endpoint} title={!connectionActive ? 'Bağlantı aktif olmalı.' : !data.endpoint ? 'Panelin dışarıdan erişilen HTTPS adresi tanımlı değil.' : undefined} onClick={() => setConfirm('on')}>{hepsiburada ? 'Webhook Bilgilerini Hazırla' : 'Webhook’u Aç'}</button>}
      </div>}
      </div>
      {registered && <dl className="instant-notifications-facts">
        <div><dt>Açıldığı Tarih</dt><dd>{time(data.registeredAt)}</dd></div>
        <div><dt>Son Bildirim</dt><dd>{time(data.lastReceivedAt)}</dd></div>
        <div><dt>Son 24 Saat</dt><dd>{data.receivedLast24Hours.toLocaleString('tr-TR')} bildirim</dd></div>
        {data.lastRejectedAt && <div><dt>Son Reddedilen</dt><dd>{time(data.lastRejectedAt)}</dd></div>}
      </dl>}
      {registered && data.lastRejectedReason && <p className="instant-notifications-message is-danger" role="alert">{data.lastRejectedReason}</p>}
      {data.message
        ? <p className="instant-notifications-message" role="status">{data.message}</p>
        : !registered && !connectionActive && data.state !== 'UNSUPPORTED' && <p className="instant-notifications-message">Webhook’u açmak için önce bağlantıyı aktif edin.</p>}
      {setup && <div className="instant-notifications-setup">
        <p>Bu metni Hepsiburada’ya (Satıcı Paneli → Yardım Merkezi → Talepler → “API ENTEGRASYON”) iletin. Şifre yeniden gösterilmez; kaybolursa webhook’u kapatıp yeniden hazırlayın.</p>
        <pre>{webhookSetupText(setup)}</pre>
        <button type="button" className="secondary" onClick={() => { void navigator.clipboard?.writeText(webhookSetupText(setup)).then(() => setFeedback({ kind: 'success', message: 'Metin kopyalandı.' })) }}>Metni Kopyala</button>
      </div>}
    </>}
    {confirm === 'on' && data && hepsiburada && <RrModal title="Webhook Bilgilerini Hazırla" desc="Panel kendi tarafında bir adres ve giriş bilgisi üretecek; Hepsiburada’ya istek gönderilmez." size="sm" locked={change.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={change.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn pri" disabled={change.isPending} onClick={() => change.mutate(true)}>{change.isPending ? 'Hazırlanıyor…' : 'Hazırla'}</button></>}>
      <ul className="instant-notifications-terms">
        <li><strong>Adres:</strong> {data.endpoint} <small>(gizli bağlantı anahtarı ve Basic Auth kullanıcı adı/şifresi panel tarafından üretilir)</small></li>
        <li><strong>Olaylar:</strong> yeni sipariş ve paket, iptal, paket bozma, kargoya verildi, teslim edildi, teslim edilemedi, adres değişikliği ve iade talepleri</li>
        <li>Hepsiburada önce test ortamında (SIT) tanımlar; test başarılı olunca canlı için aynı adımları tekrarlarsınız.</li>
        <li>İlk bildirim gelene kadar siparişler düzenli kontrolle gelmeye devam eder.</li>
      </ul>
    </RrModal>}
    {confirm === 'on' && data && shopify && <RrModal title="Webhook’u Aç" desc="Shopify’a sipariş bildirimi abonelikleri kaydedilecek." size="sm" locked={change.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={change.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn pri" disabled={change.isPending} onClick={() => change.mutate(true)}>{change.isPending ? 'Açılıyor…' : 'Webhook’u Aç'}</button></>}>
      <ul className="instant-notifications-terms">
        <li><strong>Adres:</strong> {data.endpoint} <small>(gizli bağlantı anahtarı panel tarafından üretilir)</small></li>
        <li><strong>Olaylar:</strong> sipariş oluşturuldu, güncellendi, iptal edildi, gönderildi, kısmen gönderildi ve para iadesi</li>
        <li>Bildirim yalnız hangi siparişin değiştiğini söyler; panel o siparişi mevcut Shopify bağlantısıyla yeniden okur.</li>
        <li>Shopify başarısız gönderimlerden sonra bir aboneliği kaldırırsa panel bir saat içinde yeniden oluşturur.</li>
      </ul>
      <label className="instant-notifications-secret">Uygulama Gizli Anahtarı (İsteğe Bağlı)
        <input type="password" autoComplete="off" value={clientSecret} onChange={event => setClientSecret(event.target.value)} placeholder="shpss_…" />
        <small>Girerseniz panel Shopify’ın her bildirime koyduğu imzayı da doğrular. Boş bırakabilirsiniz; bildirimler panelin ürettiği gizli adresle kabul edilir. Anahtar Shopify’da uygulamanın API kimlik bilgileri bölümündedir ve şifreli saklanır.</small>
      </label>
    </RrModal>}
    {confirm === 'on' && data && !hepsiburada && !shopify && <RrModal title="Webhook’u Aç" desc="Trendyol’a bir bildirim adresi kaydedilecek." size="sm" locked={change.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={change.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={change.isPending} onClick={() => change.mutate(true)}>{change.isPending ? 'Açılıyor…' : 'Webhook’u Aç'}</button></>}>
      <ul className="instant-notifications-terms">
        <li><strong>Adres:</strong> {data.endpoint} <small>(gizli bağlantı anahtarı ve kullanıcı adı/şifre panel tarafından üretilir)</small></li>
        <li><strong>Olaylar:</strong> tüm sipariş durumları (oluşturuldu, hazırlanıyor, faturalandı, kargoda, teslim edildi, teslim edilemedi, iptal, iade ve diğerleri)</li>
        <li><strong>Ülkeler:</strong> tüm vitrinler (Türkiye ve yurt dışı)</li>
        <li>Trendyol bildirimleri durdurursa (örneğin sunucu kapalıyken) panel bir saat içinde yeniden açar; aradaki değişiklikler düzenli kontrolle alınır.</li>
      </ul>
    </RrModal>}
    {confirm === 'off' && <RrModal title="Webhook’u Kapat" desc={hepsiburada ? "Paneldeki adres ve giriş bilgileri geçersiz olacak; Hepsiburada’dan tanımı kaldırmasını da isteyin." : `${marketplaceName}’daki bildirim kaydı silinecek.`} size="sm" locked={change.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={change.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={change.isPending} onClick={() => change.mutate(false)}>{change.isPending ? 'Kapatılıyor…' : 'Bildirimleri Kapat'}</button></>}>
      <p>Siparişler ve durum değişiklikleri düzenli kontrolle (3 dakikada bir) gelmeye devam eder.</p>
    </RrModal>}
    {confirm === 'restore' && data?.previousPreparedAt && <RrModal title="Önceki Tanımı Geri Yükle" desc={`${time(data.previousPreparedAt)} tarihinde hazırlanan adres ve şifre yeniden geçerli olur.`} size="sm" locked={restore.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={restore.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn pri" disabled={restore.isPending} onClick={() => restore.mutate()}>{restore.isPending ? 'Geri Yükleniyor…' : 'Geri Yükle'}</button></>}>
      <ul className="instant-notifications-terms">
        <li>Hepsiburada’ya ilettiğiniz bilgiler o tarihte hazırlananlarsa, Hepsiburada’nın yeniden tanımlama yapması gerekmez.</li>
        {registered && <li>Şu anki tanım {time(data.registeredAt)} tarihinde hazırlandı ve hiç bildirim almadı; kapanır.</li>}
        <li>İlk bildirim gelene kadar siparişler düzenli kontrolle gelmeye devam eder.</li>
      </ul>
    </RrModal>}
    {confirm === 'adopt' && data?.adoptableFrom && <RrModal title="Test Mağazasının Tanımını Kullan" desc={`${data.adoptableFrom.connectionName} (${data.adoptableFrom.environment}) bağlantısında ${time(data.adoptableFrom.preparedAt)} tarihinde hazırlanan adres, kullanıcı adı ve şifre bu bağlantıya taşınır.`} size="sm" locked={adopt.isPending} onClose={() => setConfirm(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={adopt.isPending} onClick={() => setConfirm(null)}>Vazgeç</button><button type="button" className="rr-btn pri" disabled={adopt.isPending} onClick={() => adopt.mutate()}>{adopt.isPending ? 'Taşınıyor…' : 'Taşı'}</button></>}>
      <ul className="instant-notifications-terms">
        <li>Hepsiburada’ya bu bilgileri ilettiyseniz, Hepsiburada’dan gelen bildirimler artık bu bağlantıya işlenir; yeniden tanımlama gerekmez.</li>
        {registered && <li>Şu anki tanım {time(data.registeredAt)} tarihinde hazırlandı ve hiç bildirim almadı; kapanır, “Önceki Tanımı Geri Yükle” ile geri alınabilir.</li>}
        <li>Test bağlantısındaki tanım oradan kalkar; test bağlantısı pasif kalır.</li>
        <li>İlk bildirim gelene kadar siparişler düzenli kontrolle gelmeye devam eder.</li>
      </ul>
    </RrModal>}
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}
function PazaramaSaleStatusPanel({ connectionId, writesEnabled, connectionActive }: { connectionId: string; writesEnabled: boolean; connectionActive: boolean }) {
  const [barcodes, setBarcodes] = useState('')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const codes = barcodes.split(/[\s,;]+/).map(code => code.trim()).filter(Boolean)
  const send = useMutation({
    mutationFn: (close: boolean) => hubApi<{ jobId?: string }>(`/connections/${connectionId}/sale-status-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ barcodes: codes, close }) }),
    onSuccess: (_, close) => setFeedback({ kind: 'success', message: `${codes.length} barkod için satışa ${close ? 'kapatma' : 'açma'} isteği kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` }),
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Satışa açma/kapatma başlatılamadı.' })
  })
  const blocked = !writesEnabled || !connectionActive || codes.length === 0 || codes.length > 100 || send.isPending
  const [confirm, confirmDialog] = useConfirmDialog()
  const confirmAndSend = async (close: boolean) => {
    const accepted = await confirm(close
      ? { title: 'Ürünler Satışa Kapatılsın mı?', message: `${codes.length} ürün Pazarama’da satışa kapatılacak. Kapalı ürün sipariş almaz.`, confirmLabel: 'Satışa Kapat', destructive: true }
      : { title: 'Ürünler Satışa Açılsın mı?', message: `${codes.length} ürün Pazarama’da satışa açılacak.`, confirmLabel: 'Satışa Aç' })
    if (accepted) send.mutate(close)
  }
  return <section className="panel n11-price-stock-send">{confirmDialog}
    <h2>Pazarama’da Satışa Aç / Kapat</h2>
    <p>{!connectionActive ? 'İşlem için bağlantı aktif veya doğrulanmış olmalı.' : !writesEnabled ? 'İşlem için yukarıdaki dış yazma anahtarını açın.' : 'Panelde bir varyanta bağlı Pazarama barkodlarını girin (en fazla 100; boşluk, virgül veya satır sonuyla ayırın).'}</p>
    <label>Barkodlar<textarea value={barcodes} onChange={event => setBarcodes(event.target.value)} rows={3} placeholder="Örn. 1101010103531" autoComplete="off" /></label>
    <div className="n11-price-stock-send-actions">
      <button type="button" className="secondary" disabled={blocked} onClick={() => void confirmAndSend(true)}>Satışa Kapat</button>
      <button type="button" disabled={blocked} onClick={() => void confirmAndSend(false)}>Satışa Aç</button>
    </div>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}
function PriceStockSendPanel({ connectionId, writesEnabled, connectionActive, platformName, codeLabel, codeAccusative, codeExample }: { connectionId: string; writesEnabled: boolean; connectionActive: boolean; platformName: string; codeLabel: string; codeAccusative: string; codeExample: string }) {
  const [stockCode, setStockCode] = useState('')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const send = useMutation({
    mutationFn: (code: string | null) => hubApi<{ jobId?: string }>(`/connections/${connectionId}/price-inventory-sync-jobs${code ? `?stockCode=${encodeURIComponent(code)}` : ''}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: (_, code) => setFeedback({ kind: 'success', message: code ? `${code} için fiyat-stok gönderimi kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` : `Değişen tüm ${platformName} fiyat ve stokları kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` }),
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Fiyat-stok gönderimi başlatılamadı.' })
  })
  const blocked = !writesEnabled || !connectionActive
  return <section className="panel n11-price-stock-send">
    <h2>{platformName}’{platformName === 'Pazarama' ? 'ya' : platformName === 'PTT AVM' ? 'ye' : 'e'} Fiyat ve Stok Gönder</h2>
    <p>{!connectionActive ? 'Gönderim için bağlantı aktif veya doğrulanmış olmalı.' : !writesEnabled ? (platformName === 'Pazarama' || platformName === 'PTT AVM' ? 'Gönderim için yukarıdaki dış yazma anahtarını açın.' : 'Gönderim için yukarıdaki dış yazma anahtarını ve fiyat-stok akışını açın.') : `İlk gönderimi tek bir ${codeLabel} ile yapıp sonucu ${platformName} panelinde kontrol edin; sorun yoksa değişen tüm ürünleri gönderin.`}</p>
    <form onSubmit={event => { event.preventDefault(); if (stockCode.trim()) send.mutate(stockCode.trim()) }}>
      <label>{platformName} {codeLabel.split(' ').map(word => word.charAt(0).toLocaleUpperCase('tr-TR') + word.slice(1)).join(' ')}<input value={stockCode} onChange={event => setStockCode(event.target.value)} placeholder={`Örn. ${codeExample}`} autoComplete="off" /></label>
      <div className="n11-price-stock-send-actions">
        <button type="submit" disabled={blocked || !stockCode.trim() || send.isPending}>{send.isPending ? 'Kuyruğa alınıyor…' : `Bu ${codeAccusative.charAt(0).toLocaleUpperCase('tr-TR')}${codeAccusative.slice(1)} Gönder`}</button>
        <button type="button" className="secondary" disabled={blocked || send.isPending} onClick={() => send.mutate(null)}>Değişen Tüm Ürünleri Gönder</button>
      </div>
    </form>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}
// Hepsiburada SIT certification (STAGE connection only): uploads the price and stock of the products Hepsiburada
// loaded into the SIT account (imported hbSku links with an active offer). The job's detail in İşlem Takibi shows
// the inventoryUploadId as its external id.
function HepsiburadaSitPriceStockPanel({ connectionId, connectionReady }: { connectionId: string; connectionReady: boolean }) {
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const send = useMutation({
    mutationFn: () => hubApi<{ jobId?: string }>(`/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: result => { setJobId(result.jobId ?? null); setFeedback({ kind: 'success', message: 'Stok ve fiyat yüklemesi kuyruğa alındı. inventoryUploadId, İşlem Takibi’nde bu işlemin Dış Kimlik alanında görünür.' }) },
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Stok ve fiyat yüklemesi başlatılamadı.' })
  })
  return <section className="panel n11-price-stock-send">
    <h2>Stok ve Fiyat Gönder (SIT)</h2>
    <p>{connectionReady ? 'Hepsiburada’nın SIT hesabına yüklediği, hbSku ile eşleşmiş ve aktif teklifi olan ürünlerin fiyat ve stoğu tek bir yükleme olarak gönderilir. Her gönderim yeni bir yükleme (inventoryUploadId) oluşturur.' : 'Gönderim için bağlantı aktif veya doğrulanmış olmalı ve son bağlantı testi hatasız tamamlanmalı.'}</p>
    <div className="n11-price-stock-send-actions">
      <button type="button" disabled={!connectionReady || send.isPending} onClick={() => send.mutate()}>{send.isPending ? 'Kuyruğa alınıyor…' : 'Stok ve Fiyat Gönder (SIT)'}</button>
    </div>
    {jobId && <p>İşlem kimliği: <code>{jobId}</code> · <Link to="/jobs">İşlem Takibi’nde aç</Link></p>}
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}
function N11PublishSettingsPanel({ connectionId, displayName, version, shipmentTemplate, preparingDay }: { connectionId: string; displayName: string; version: number; shipmentTemplate: string | null | undefined; preparingDay: number | null | undefined }) {
  const client = useQueryClient()
  const [template, setTemplate] = useState(shipmentTemplate ?? '')
  const [days, setDays] = useState(String(preparingDay ?? 3))
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const templates = useQuery({ queryKey: ['n11-shipment-templates', connectionId], queryFn: () => hubApi<string[]>(`/connections/${connectionId}/n11-shipment-templates`), staleTime: 5 * 60_000, retry: false })
  const templateChoices = templates.data ?? []
  const parsedDays = Number(days)
  const daysValid = Number.isInteger(parsedDays) && parsedDays >= 1 && parsedDays <= 30
  const save = useMutation({
    mutationFn: () => hubApi<Connection>(`/connections/${connectionId}`, { method: 'PATCH', headers: { 'If-Match': `"v${version}"` }, body: JSON.stringify({ displayName, n11ShipmentTemplate: template.trim(), n11PreparingDay: parsedDays }) }),
    onSuccess: async result => { client.setQueryData(['connection', connectionId], result); await client.invalidateQueries({ queryKey: ['connections'] }); setFeedback({ kind: 'success', message: 'N11 ürün açma ayarları kaydedildi.' }) },
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'N11 ürün açma ayarları kaydedilemedi.' })
  })
  return <section className="panel n11-price-stock-send">
    <h2>N11 Ürün Açma Ayarları</h2>
    <p>N11’e yeni ürün açarken her ürüne bu kargo şablonu ve hazırlık süresi uygulanır. Şablonlar N11 satıcı panelinizden okunur.</p>
    <form onSubmit={event => { event.preventDefault(); if (daysValid) save.mutate() }}>
      <label>Kargo Şablonu{templateChoices.length ? <select value={template} onChange={event => setTemplate(event.target.value)}><option value="">Şablon seçin</option>{template && !templateChoices.includes(template) && <option value={template}>{template} (N11’de bulunamadı)</option>}{templateChoices.map(name => <option key={name} value={name}>{name}</option>)}</select> : <input value={template} onChange={event => setTemplate(event.target.value)} maxLength={100} placeholder={templates.isLoading ? 'N11’deki şablonlar yükleniyor…' : 'N11 satıcı panelindeki şablon adı'} autoComplete="off" />}{templates.isError && <small className="connection-settings-field-help">N11’deki şablonlar okunamadı; adı elle yazabilirsiniz.</small>}{templates.isSuccess && !templateChoices.length && <small className="connection-settings-field-help">N11 panelinde kargo şablonu bulunamadı; önce N11’de bir şablon oluşturun.</small>}</label>
      <label>Hazırlık Süresi (Gün)<input type="number" min={1} max={30} value={days} onChange={event => setDays(event.target.value)} /></label>
      <div className="n11-price-stock-send-actions">
        <button type="submit" disabled={!daysValid || save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
      </div>
    </form>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}
type SettingsIconName = 'info' | 'sync' | 'bell' | 'write' | 'invoice' | 'key' | 'tool' | 'percent'
const settingsIconPaths: Record<SettingsIconName, string[]> = {
  info: ['M8 7.2v3.6', 'M8 5h.01'],
  sync: ['M13 6.5A5 5 0 0 0 4 4.5L3 6', 'M3 2.5V6h3.5', 'M3 9.5a5 5 0 0 0 9 2l1-1.5', 'M13 13.5V10H9.5'],
  bell: ['M4.5 11V7a3.5 3.5 0 0 1 7 0v4l1 1.5h-9z', 'M6.8 14a1.3 1.3 0 0 0 2.4 0'],
  write: ['M3 13h3l7-7-3-3-7 7z'],
  invoice: ['M4 2.5h6l2 2V13.5H4z', 'M6 7h4', 'M6 9.5h4'],
  key: ['m7.6 8.4 5.4-5.4', 'M11 5l1.5 1.5'],
  tool: ['M10.5 2.5a3 3 0 0 0-3.4 4l-4.6 4.6 2.4 2.4 4.6-4.6a3 3 0 0 0 4-3.4l-1.8 1.8-1.6-.4-.4-1.6z'],
  percent: ['M12.5 3.5l-9 9', 'M4.5 3.5h.01', 'M4.5 5.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2', 'M11.5 12.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2']
}
// Section icons in the same 1.6 line weight as the status shapes.
function SettingsIcon({ name }: { name: SettingsIconName }) {
  return <svg className="cs-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{name === 'info' && <circle cx="8" cy="8" r="6.2" />}{name === 'key' && <circle cx="5.5" cy="10.5" r="3" />}{settingsIconPaths[name].map(path => <path key={path} d={path} />)}</svg>
}
function IntegrationDetailWorkspace({ id, inline = false, onClose }: { id: string; inline?: boolean; onClose?: () => void }) {
  const client = useQueryClient(); const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null); const [syncPolicyOpen, setSyncPolicyOpen] = useState<SyncPolicy | null>(null); const connection = useQuery({ queryKey: ['connection', id], queryFn: () => hubApi<Connection>(`/connections/${id}`), refetchOnMount: 'always' }); const syncPolicies = useQuery({ queryKey: ['sync-policies', id], queryFn: () => hubPoll<SyncPolicy[]>(`/connections/${id}/sync-policies`), enabled: supportsSyncPolicyManagement(connection.data?.platformCode ?? ''), refetchInterval: 30_000, refetchIntervalInBackground: false }); const capabilities = useQuery({ queryKey: ['capabilities', id], queryFn: () => hubApi<Capability[]>(`/connections/${id}/capabilities`), enabled: shouldLoadConnectionCapabilities(connection.data?.platformCode) });
  useEffect(() => {
    if (!feedback) return
    const timeout = window.setTimeout(() => setFeedback(null), 5000)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  const run = useMutation({ mutationFn: (request: { path: string; body?: object; version?: number }) => hubApi<{ succeeded?: boolean; errorCode?: string; errorSummary?: string }>(request.path, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), ...(request.version ? { 'If-Match': `"v${request.version}"` } : {}) }, body: JSON.stringify(request.body ?? {}) }), onSuccess: async (result, request) => { const isConnectionTest = request.path.endsWith('/test-jobs'); if (isConnectionTest) { const succeeded = result.succeeded === true; setFeedback({ kind: succeeded ? 'success' : 'error', message: succeeded ? 'Bağlantı testi başarılı; yetenek kontrolleri yenilendi. Dış yazma yapılmadı.' : `Bağlantı testi başarısız${result.errorCode ? `: ${result.errorCode}` : ''}${result.errorSummary ? ` — ${result.errorSummary}` : '.'}` }) } else if (request.path.endsWith('/stage-test-order-jobs')) setFeedback({ kind: 'info', message: 'Test ortamı siparişi sıraya alındı; sonucu İşlem Takibi’nde görebilirsiniz.' }); else setFeedback({ kind: 'success', message: 'İşlem başarıyla tamamlandı.' }); const invalidations = [client.invalidateQueries({ queryKey: ['connection', id] })]; if (isConnectionTest) invalidations.push(client.invalidateQueries({ queryKey: ['capabilities', id] }), client.invalidateQueries({ queryKey: ['sync-policies', id] })); await Promise.all(invalidations) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.' }) })
   const saveSyncPolicy = useMutation({ mutationFn: (request: { policy: SyncPolicy; intervalSeconds: number; overlapSeconds: number; enabled: boolean; closeModal?: boolean }) => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + request.policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + request.policy.version + '"' }, body: JSON.stringify({ intervalSeconds: request.intervalSeconds, overlapSeconds: request.overlapSeconds, jitterSeconds: request.policy.jitterSeconds, enabled: request.enabled }) }), onSuccess: async (_, request) => { if (request.closeModal) setSyncPolicyOpen(null); setFeedback({ kind: 'success', message: request.closeModal ? 'Otomatik eşitleme ayarı güncellendi.' : 'Akış ' + (request.enabled ? 'açıldı.' : 'kapatıldı.') }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Eşitleme ayarı güncellenemedi.' }) })
   // Starts one flow now; an already waiting or running run of it is reused.
   const runSyncPolicy = useMutation({
     mutationFn: (policy: SyncPolicy) => hubApi<{ jobId: string }>('/connections/' + id + '/sync-policies/' + policy.resourceType + '/runs', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } }),
     onSuccess: (_, policy) => { setFeedback({ kind: 'success', message: `${syncPolicyLabel(policy.resourceType)} başlatıldı. Sonucu İşlem Takibi’nde görebilirsiniz.` }); void client.invalidateQueries({ queryKey: ['jobs'] }) },
     onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Akış başlatılamadı.' })
   })
   const saveFlowGroup = useMutation({ mutationFn: (request: { policies: SyncPolicy[]; enabled: boolean; label: string }) => Promise.all(request.policies.map(policy => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + policy.version + '"' }, body: JSON.stringify({ intervalSeconds: policy.intervalSeconds, overlapSeconds: policy.overlapSeconds, jitterSeconds: policy.jitterSeconds, enabled: request.enabled }) }))), onSuccess: async (_, request) => { setFeedback({ kind: 'success', message: `${request.label} ${request.enabled ? 'açıldı' : 'kapatıldı'}.` }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: async reason => { setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Akışlar güncellenemedi.' }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) } })
   const saveExternalWriteGroup = useMutation({ mutationFn: (request: { policies: SyncPolicy[]; enabled: boolean }) => Promise.all(request.policies.map(policy => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + policy.version + '"' }, body: JSON.stringify({ intervalSeconds: policy.intervalSeconds, overlapSeconds: policy.overlapSeconds, jitterSeconds: policy.jitterSeconds, enabled: request.enabled }) }))), onSuccess: async (_, request) => { setFeedback({ kind: 'success', message: `${connection.data?.displayName ?? 'Entegrasyon'} için dış yazma grubu ${request.enabled ? 'açıldı' : 'kapatıldı'}.`, details: [{ label: 'Entegrasyon', value: `${connection.data?.platformCode ?? ''} · ${connection.data?.displayName ?? id}` }, { label: 'Grup', value: request.policies.map(policy => policy.resourceType).join(', ') }], target: { href: `/integrations/${id}`, label: 'Entegrasyona Git' } }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Dış yazma grubu güncellenemedi.' }) })
  // Saving credentials has its own pending state (the test and other actions keep theirs). The save bumps the
  // connection's version, so both the card list and this connection are re-read; otherwise the next change on the card
  // would send the old version and get 412 (W10). The secret fields are cleared once the server took them.
  const saveCredential = useMutation({
    mutationFn: (request: { form: HTMLFormElement; platformCode: string; version: number }) => hubApi(`/connections/${id}/credential`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${request.version}"` }, body: JSON.stringify(credentialRequestBody(request.platformCode, new FormData(request.form))) }),
    onSuccess: async (_, request) => {
      request.form.reset()
      setFeedback({ kind: 'success', message: 'Kimlik bilgisi şifreli olarak yenilendi; değerler tekrar gösterilmeyecek.' })
      await Promise.all([client.invalidateQueries({ queryKey: ['connection', id] }), client.invalidateQueries({ queryKey: ['connections'] })])
    },
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Kimlik bilgisi kaydedilemedi.' })
  })
  function credential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!connection.data || saveCredential.isPending) return
    saveCredential.mutate({ form: event.currentTarget, platformCode: connection.data.platformCode, version: connection.data.version })
  }
  const rootClass = inline ? 'integration-inline-workspace' : 'content f3'
  const [externalWritesEnabled, setExternalWritesEnabled] = useState(false)
   const [externalStoreId, setExternalStoreId] = useState('')
   const [displayName, setDisplayName] = useState('')
  const [settingsSection, setSettingsSection] = useState('genel')
  const [flowFilter, setFlowFilter] = useState<'all' | 'issue' | 'wait' | 'off'>('all')
  const savedExternalWrites = connection.data?.externalWritesEnabled
  useEffect(() => { if (savedExternalWrites !== undefined) setExternalWritesEnabled(savedExternalWrites) }, [savedExternalWrites])
  const savedStoreId = connection.data?.externalStoreId; const savedPlatformCode = connection.data?.platformCode
  useEffect(() => { if (savedPlatformCode !== undefined && savedStoreId !== undefined) setExternalStoreId(savedPlatformCode === 'SHOPIFY' ? shopifyStoreDisplayName(savedStoreId) : savedStoreId) }, [savedPlatformCode, savedStoreId])
   const savedDisplayName = connection.data?.displayName
  useEffect(() => { if (savedDisplayName !== undefined) setDisplayName(savedDisplayName) }, [savedDisplayName])
  const saveConnectionDetails = useMutation({ mutationFn: () => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); const name = displayName.trim(); const store = externalStoreId.trim(); if (!name) throw new Error('Kayıt adı zorunludur.'); if (!store) throw new Error('Mağaza adı zorunludur.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: name, externalStoreId: store }) }) }, onSuccess: async result => { setDisplayName(result.displayName); setExternalStoreId(result.platformCode === 'SHOPIFY' ? shopifyStoreDisplayName(result.externalStoreId) : result.externalStoreId); setFeedback({ kind: 'success', message: 'Kayıt adı ve mağaza bilgileri güncellendi. Yeniden bağlantı testi gerekiyor.' }); await client.invalidateQueries({ queryKey: ['connection', id] }); await client.invalidateQueries({ queryKey: ['connections'] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı bilgileri güncellenemedi.' }) })
  const saveExternalWrites = useMutation({ mutationFn: (enabled: boolean) => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: connection.data.displayName, externalWritesEnabled: enabled }) }) }, onSuccess: async (_, enabled) => { setExternalWritesEnabled(enabled); setFeedback({ kind: 'success', message: enabled ? `${connection.data?.displayName ?? 'Entegrasyon'} için dış yazma açıldı.` : `${connection.data?.displayName ?? 'Entegrasyon'} salt-okunur moda alındı; dış yazma ve bağlı otomatik akışlar kapatıldı.`, details: [{ label: 'Entegrasyon', value: `${connection.data?.platformCode ?? ''} · ${connection.data?.displayName ?? id}` }], target: { href: `/integrations/${id}`, label: 'Entegrasyona Git' } }); await client.invalidateQueries({ queryKey: ['connection', id] }); await client.invalidateQueries({ queryKey: ['capabilities', id] }); await client.invalidateQueries({ queryKey: ['connections'] }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Dış yazma ayarı güncellenemedi.' }) })
  const saveInvoiceCreation = useMutation({ mutationFn: (enabled: boolean) => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: connection.data.displayName, invoiceCreationEnabled: enabled }) }) }, onSuccess: async result => { client.setQueryData(['connection', id], result); setFeedback({ kind: 'success', message: result.invoiceCreationEnabled ? 'Bu bağlantı için fatura oluşturma açıldı.' : 'Bu bağlantı için fatura oluşturma kapatıldı; mevcut belgeler ve fatura kayıtları korunuyor.' }); await Promise.all([client.invalidateQueries({ queryKey: ['connections'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })]) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Fatura oluşturma ayarı güncellenemedi.' }) })
  if (connection.isLoading) return <div className={rootClass}><Busy /></div>; if (connection.isError || !connection.data) return <div className={rootClass}><ErrorBox error={connection.error} onRetry={() => void connection.refetch()} /></div>; const item = connection.data
  if (!activePlatformCodes.has(item.platformCode)) return <div className={rootClass}><Link to="/integrations" className="back"><UiIcon name="arrowLeft" /> Platformlar</Link><div className="unknown"><strong>Bağlantı aktif kapsam dışında</strong><p>Bu platform için işlem paneli henüz etkin değil. Kapsam dışı tarihsel bağlantılara işlem sunulmaz.</p></div></div>
  const stageManual = item.platformCode !== 'SHOPIFY' && item.environment === 'STAGE'; const displayEnvironment = item.platformCode === 'SHOPIFY' ? 'PRODUCTION' : item.environment; const readConnectionReady = item.status === 'ACTIVE' || item.status === 'VERIFIED'; const stageReady = stageManual && !item.lastErrorCode && readConnectionReady
   const efaturam = item.platformCode === 'TRENDYOL_EFATURAM'
   const externalWritePlatform = item.platformCode === 'SHOPIFY' ? 'Shopify' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada' : item.platformCode === 'N11' ? 'N11' : item.platformCode === 'PAZARAMA' ? 'Pazarama' : item.platformCode === 'PTTAVM' ? 'PTT AVM' : efaturam ? 'E-Faturam' : 'Trendyol'
   // Turkish case endings follow how each name is read: Hepsiburada’ya, N11’e, PTT AVM’ye, Trendyol’dan, N11’den.
   const externalWriteTo = `${externalWritePlatform}’${({ Hepsiburada: 'ya', Pazarama: 'ya', N11: 'e', 'PTT AVM': 'ye' } as Record<string, string>)[externalWritePlatform] ?? 'a'}`
   const externalWriteFrom = `${externalWritePlatform}’${({ N11: 'den', 'PTT AVM': 'den' } as Record<string, string>)[externalWritePlatform] ?? 'dan'}`
   const externalWriteGroups: Array<{ key: string; label: string; resourceTypes: string[]; description: string; capabilityCodes?: string[] }> = item.platformCode === 'SHOPIFY'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki fiyat ve stok değişiklikleri Shopify mağazasına gönderilir.', capabilityCodes: ['PRICE_WRITE', 'INVENTORY_WRITE'] },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Mevcut fulfillment kargo takip bilgisi ve iade onay/ret kararları Shopify’a gönderilir. Shopify Admin API bu panelden taşıyıcı etiketi satın almaz veya üretmez.', capabilityCodes: ['SHIPMENT_WRITE', 'RETURN_READ', 'RETURN_WRITE'] }
       ]
     : item.platformCode === 'PTTAVM'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki PTT AVM fiyatı ve stok değişikliklerini PTT AVM’ye otomatik gönderir. Kapalıyken sistem bu güncellemeleri kendiliğinden göndermez; panelden elle başlattığınız fiyat-stok gönderimi bu ayardan bağımsızdır, yalnız dış yazma anahtarı açık olmalıdır.' }
       ]
     : item.platformCode === 'PAZARAMA'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki Pazarama fiyatı ve stok değişikliklerini Pazarama’ya otomatik gönderir. Kapalıyken sistem bu güncellemeleri kendiliğinden göndermez; panelden elle başlattığınız fiyat-stok gönderimi bu ayardan bağımsızdır, yalnız dış yazma anahtarı açık olmalıdır. Ürün açma ve iade kararları sonraki aşamalarda açılacak.' },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız sipariş işleme alma, tedarik edilemedi, paket bölme/güncelleme, işçilik bedeli, iptal talebi kararlarını ve iade onay/retlerini Pazarama’ya gönderir. Kapalıyken bu işlemler Pazarama’ya aktarılmaz.' }
       ]
     : item.platformCode === 'N11'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki N11 fiyatı ve stok değişikliklerini N11’e otomatik gönderir. Kapalıyken bu güncellemeler N11’e aktarılmaz.' },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız sipariş işleme alma, paket bölme ve iade onaylarını N11’e gönderir. İade reddi N11 satıcı panelinden yapılır. Kapalıyken bu işlemler N11’e aktarılmaz.' }
       ]
     : item.platformCode === 'HEPSIBURADA'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki fiyat ve stok değişikliklerini Hepsiburada’ya otomatik gönderir. Kapalıyken bu güncellemeler Hepsiburada’ya aktarılmaz.', capabilityCodes: ['PRICE_WRITE', 'INVENTORY_WRITE'] },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız kargo, etiket, paket ve iade işlemlerini Hepsiburada’ya gönderir. Kapalıyken bu işlemler dış platforma aktarılmaz.', capabilityCodes: ['SHIPMENT_WRITE', 'RETURN_WRITE'] }
       ]
     : [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: `Açıkken paneldeki fiyat ve stok değişikliklerini ${externalWriteTo} otomatik gönderir. Kapalıyken bu güncellemeler ${externalWriteTo} aktarılmaz.` },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: `Açıkken panelden başlattığınız kargo, etiket ve iade kararlarını ${externalWriteTo} gönderir. Kapalıyken bu işlemler dış platforma aktarılmaz.` }
       ]
  const syncPolicyCategories: Array<{ key: string; label: string; resourceTypes: string[]; kind?: 'external-write-parent'; description?: string }> = [
    { key: 'orders', label: 'Sipariş Akışları', resourceTypes: ['ORDERS', 'ORDER_RECOVERY', 'ORDER_LIFECYCLE'] },
    { key: 'returns', label: 'İade Akışları', resourceTypes: ['RETURNS', 'RETURN_LIFECYCLE'] },
    { key: 'reconciliation', label: 'Uzlaştırma Akışları', resourceTypes: ['ORDER_RECONCILE_SHORT', 'ORDER_RECONCILE_MEDIUM', 'ORDER_RECONCILE_DAILY', 'RETURN_RECONCILE_SHORT', 'RETURN_RECONCILE_MEDIUM', 'RETURN_RECONCILE_DAILY', 'STOCK_RECONCILE_SHORT', 'STOCK_RECONCILE_MEDIUM', 'STOCK_RECONCILE_DAILY'] },
     { key: 'external-writes', label: 'Dış yazma akışları', resourceTypes: externalWriteGroups.flatMap(group => group.resourceTypes), kind: 'external-write-parent' },
    { key: 'invoices', label: 'Faturalama Akışları', resourceTypes: ['ORDER_INVOICE_RECONCILIATION'] },
    { key: 'finance', label: 'Finans Akışları', resourceTypes: ['FINANCE'], description: 'Trendyol cari hesap ekstresinden komisyon, kargo, hizmet bedeli ve stopaj kayıtları günde bir okunur; Trendyol\'a hiçbir şey yazılmaz.' },
    { key: 'reference', label: 'Referans Veri Akışları', resourceTypes: ['REFERENCE_DATA'] }
  ]
  const visibleSyncPolicies = syncPolicies.data?.filter(policy => !['PRODUCTS', 'PRODUCT_WRITE', 'PRICE_STOCK_WRITE'].includes(policy.resourceType) && supportsSyncPolicy(item.platformCode, policy.resourceType)) ?? []
  const categorizedResourceTypes = new Set(syncPolicyCategories.flatMap(category => category.resourceTypes))
  const uncategorizedSyncPolicies = visibleSyncPolicies.filter(policy => !categorizedResourceTypes.has(policy.resourceType))
   const syncPolicyGroups: Array<{ key: string; label: string; resourceTypes: string[]; kind?: 'external-write-parent'; description?: string; policies: SyncPolicy[] }> = [
     ...syncPolicyCategories.map(category => ({ ...category, policies: visibleSyncPolicies.filter(policy => category.resourceTypes.includes(policy.resourceType)) })).filter(group => group.policies.length && !(item.platformCode === 'SHOPIFY' && group.kind === 'external-write-parent')),
     ...(uncategorizedSyncPolicies.length ? [{ key: 'other', label: 'Diğer Akışlar', resourceTypes: [], policies: uncategorizedSyncPolicies }] : [])
   ]
  const renderExternalWriteGroup = (externalGroup: typeof externalWriteGroups[number]) => {
    const policies = visibleSyncPolicies.filter(policy => externalGroup.resourceTypes.includes(policy.resourceType))
    const requiredCapabilities = externalGroup.capabilityCodes?.map(code => ({ code, capability: capabilities.data?.find(value => value.code === code) })) ?? []
    const evidence = externalWriteEvidenceState(requiredCapabilities.map(value => ({ code: value.code, ...value.capability })), externalWritePlatform, capabilities.isLoading)
    const state = externalWriteGroupState(policies, item.externalWritesEnabled, evidence.blocked, capabilities.isLoading, evidence.label)

    return <article className={'sync-policy-row ' + (state.enabled ? 'is-enabled' : 'is-disabled') + (state.partiallyEnabled ? ' is-partial' : '')} key={externalGroup.key} data-external-write-group={externalGroup.key}><div className="sync-policy-card-summary"><strong>{externalGroup.label}</strong><small>{externalGroup.description}</small></div><label className="sync-policy-card-switch" title={[externalGroup.label, state.label, evidence.details].filter(Boolean).join(' · ')}><span>{state.label}</span><input type="checkbox" checked={state.configuredEnabled} disabled={saveExternalWriteGroup.isPending || state.disabled} onChange={event => saveExternalWriteGroup.mutate({ policies, enabled: event.target.checked })} aria-label={externalGroup.label + ' seçeneğini ' + (state.configuredEnabled ? 'kapat' : 'aç')} /><i aria-hidden="true" /></label></article>
  }
  const syncPolicyLabel = (resourceType: string) => resourceType === 'ORDER_RECONCILE_DAILY' && item.platformCode === 'SHOPIFY' ? 'Üç Günlük Sipariş Uzlaştırması' : ({ ORDERS: 'Siparişler ve Paket Durumları', ORDER_RECOVERY: 'Sipariş Geçmişi Kurtarma', ORDER_LIFECYCLE: 'Açık Sipariş Yaşam Döngüsü', ORDER_RECONCILE_SHORT: 'Kısa Sipariş Uzlaştırması', ORDER_RECONCILE_MEDIUM: 'Orta Sipariş Uzlaştırması', ORDER_RECONCILE_DAILY: 'Günlük Sipariş Uzlaştırması', ORDER_INVOICE_RECONCILIATION: 'Paket Fatura Kontrolü', RETURNS: 'İade Talepleri', RETURN_LIFECYCLE: 'Açık İade Yaşam Döngüsü', RETURN_RECONCILE_SHORT: 'Kısa İade Uzlaştırması', RETURN_RECONCILE_MEDIUM: 'Orta İade Uzlaştırması', RETURN_RECONCILE_DAILY: 'Günlük İade Uzlaştırması', STOCK_RECONCILE_SHORT: 'Kısa Stok Uzlaştırması', STOCK_RECONCILE_MEDIUM: 'Orta Stok Uzlaştırması', STOCK_RECONCILE_DAILY: 'Günlük Stok Uzlaştırması', PRICE_WRITE: 'Fiyat Gönderimleri', STOCK_WRITE: 'Stok Gönderimleri', SHIPMENT_WRITE: 'Kargo ve Etiket İşlemleri', RETURN_WRITE: 'İade Kararları', REFERENCE_DATA: 'Katalog Referansları', ORDER_CARGO_INFO: 'Kargo Bilgisi Eşitleme', QUESTIONS: 'Müşteri Soruları', FINANCE: 'Cari Hesap ve Masraflar' } as Record<string, string>)[resourceType] ?? resourceType
  const syncPolicyIntervals: Array<[number, string]> = [[0, 'Anında'], [30, '30 saniye'], [60, '1 dakika'], [120, '2 dakika'], [180, '3 dakika'], [300, '5 dakika'], [480, '8 dakika'], [540, '9 dakika'], [600, '10 dakika'], [900, '15 dakika'], [1800, '30 dakika'], [2700, '45 dakika'], [3600, '1 saat'], [10800, '3 saat'], [21600, '6 saat'], [43200, '12 saat'], [86400, '24 saat'], [259200, '3 gün']]
  const syncPolicyOverlaps: Array<[number, string]> = [[120, '2 dakika'], [300, '5 dakika'], [600, '10 dakika'], [900, '15 dakika']]
  const syncPolicyHealthHelp = (policy: SyncPolicy) => {
    const success = policy.lastSuccessAt ? `Son başarılı kontrol: ${formatDateTime(policy.lastSuccessAt)}.` : 'Henüz başarılı kontrol kaydı yok.'
    const failures = [
      (policy.lastFailedCount ?? 0) > 0 ? `Son kontrolde ${policy.lastFailedCount} kayıt güncellenemedi.` : '',
      (policy.consecutiveFailureCount ?? 0) > 0 ? `${policy.consecutiveFailureCount} ardışık akış hatası var.` : ''
    ].filter(Boolean).join(' ')
    if (!policy.enabled) return `Bu akış kapalı. ${success}`
    if (policy.healthStatus === 'PERMISSION') return 'Bu bir yazma iznidir: açıkken panelden başlatılan işlemler platforma gönderilir. Zamanlanmış bir okuma olmadığı için gecikme durumu yoktur.'
    if (policy.healthStatus === 'OFFLINE') return policy.lastSuccessAt
      ? `${success} Beklenen çalışma aralığı aşıldı; akış güncellenmedi. Pazaryeri bağlantısı açık olabilir, bu uyarı yalnızca bu akışın son çalışmasının eski olduğunu gösterir.`
      : 'Bu akış henüz başarıyla çalışmadı. İlk başarılı çalışması bekleniyor; bu durum tek başına pazaryeri bağlantısının kapalı olduğu anlamına gelmez.'
    return failures ? `${failures} ${success}` : `${success} Durum, son deneme ve kayıt hatalarına göre güncellenir.`
  }
  const syncPolicyDescription = (resourceType: string) => ({
    ORDERS: item.platformCode === 'SHOPIFY' ? 'Shopify siparişlerini okur ve ödeme, gönderim, iptal ve iade durumlarını panele taşır. İlk tarama son 3 takvim ayını kapsar; Shopify API erişimi read_all_orders izni yoksa son 60 günle sınırlıdır.' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada siparişlerini ve paket bilgilerini salt okunur olarak panele alır.' : 'Trendyol’daki yeni ve güncellenen siparişleri panele alır; ürün, müşteri, paket ve kargo bilgilerini yerel çalışma ekranlarında güncel tutar.',
     ORDER_RECOVERY: item.platformCode === 'SHOPIFY' ? 'Tam tarama Shopify’ın erişime açtığı tüm sipariş geçmişini yeniden okur. Varsayılan erişim son 60 günle sınırlıdır; daha eski kayıtlar için read_all_orders izni gerekir.' : item.platformCode === 'HEPSIBURADA' ? 'Normal taramada kaçırılan Hepsiburada siparişlerini ve paketlerini tamamlar. Sipariş ve paket listelemeleri offset ile en fazla 10 kayıtlık sayfalar halinde, tarih filtreleri de en fazla 24 saatlik aralıklarla çekilir.' : 'Normal sipariş taramasında kaçırılmış veya geçmiş tarihli siparişleri yeniden arayarak eksik kayıtları tamamlar.',
     ORDER_LIFECYCLE: item.platformCode === 'SHOPIFY' ? 'Açık Shopify siparişlerinin ödeme, paket, kargo ve teslimat durumlarını izler; değişiklikleri panelde günceller.' : item.platformCode === 'HEPSIBURADA' ? 'Açık Hepsiburada siparişlerinin paket ve taşıma durumlarını salt okunur sipariş detay servisiyle yeniler; her turda en fazla 25 sipariş kontrol edilir.' : 'Açık siparişlerin paket, kargo ve teslimat durumlarını izler; durum değişikliklerini panelde günceller.',
    ORDER_RECONCILE_SHORT: 'Yakın zamanda değişen siparişleri kısa aralıklarla kontrol ederek gecikmeli veya kaçırılmış durum değişikliklerini yakalar.',
    ORDER_RECONCILE_MEDIUM: 'Gün içinde açık kalan siparişleri orta sıklıkta tarayarak yerel sipariş durumunu Trendyol ile karşılaştırır.',
    ORDER_RECONCILE_DAILY: item.platformCode === 'SHOPIFY' ? 'Üç günde bir Shopify siparişlerini uzun dönemli uzlaştırır; uzun süre fark kalan kayıtları yeniden kontrol eder.' : 'Günlük uzlaştırma ile siparişlerin yerel ve Trendyol kayıtlarını yeniden karşılaştırır; uzun süre fark kalanları düzeltme taramasına alır.',
    ORDER_INVOICE_RECONCILIATION: item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada sipariş detayındaki hasInvoice bilgisini okuyarak fatura durumunu panelde güncel tutar.' : 'Paketlerin fatura durumunu kontrol eder; kesilen, bekleyen veya işlenemeyen faturaların paneldeki durumunu güncel tutar.',
    RETURNS: item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada’daki yeni ve güncellenen iade taleplerini salt okunur olarak panele alır.' : 'Trendyol’daki yeni ve güncellenen iade taleplerini panele alır; iade nedeni, ürün satırları ve aksiyon durumunu günceller.',
    RETURN_LIFECYCLE: 'Açık iadelerin kargo, teslim alma, inceleme ve sonuçlanma adımlarını takip eder.',
    RETURN_RECONCILE_SHORT: 'Yakın zamanda değişen iadeleri kısa aralıklarla kontrol ederek gecikmiş durumları yakalar.',
    RETURN_RECONCILE_MEDIUM: 'Açık iadeleri orta sıklıkta Trendyol ile karşılaştırır ve yerel iade kaydını güncel tutar.',
    RETURN_RECONCILE_DAILY: 'Günlük iade uzlaştırmasıyla uzun süre değişmeyen veya eksik kalan iade kayıtlarını yeniden doğrular.',
    STOCK_RECONCILE_SHORT: 'Paneldeki stok ile Trendyol stok bilgisini kısa aralıklarla karşılaştırır ve farkları tespit eder.',
    STOCK_RECONCILE_MEDIUM: 'Stok farklarını orta sıklıkta kontrol eder ve gün içindeki değişikliklerin kaçırılmasını önler.',
    STOCK_RECONCILE_DAILY: 'Gün sonunda stok kayıtlarını yeniden karşılaştırarak kalıcı farkları ve eksik güncellemeleri belirler.',
     PRICE_WRITE: `Panelde yapılan fiyat değişikliklerini ${externalWriteTo} gönderir. Kapalıysa fiyat güncellemeleri dış platforma yazılmaz.`,
     STOCK_WRITE: `Paneldeki stok değişikliklerini ${externalWriteTo} gönderir. Kapalıysa stok güncellemeleri dış platforma yazılmaz.`,
     SHIPMENT_WRITE: `Kargo firması, etiket ve paket dış yazma işlemlerini ${externalWriteTo} aktarır.`,
     RETURN_WRITE: item.platformCode === 'SHOPIFY' ? 'İade kararlarını Shopify sipariş akışına aktarır; yanıt beklenerek paneldeki iade durumu güncellenir.' : 'İade kabul veya ret kararlarını Trendyol API’sine doğrudan gönderir; yanıt beklenerek paneldeki iade durumu güncellenir.',
     REFERENCE_DATA: `${externalWritePlatform} kategori, özellik ve diğer katalog referanslarını okuyarak eşleştirme ekranlarını güncel tutar.`
  } as Record<string, string>)[resourceType] ?? 'Bu akış, bağlantının ilgili veri veya dış yazma sürecini otomatik olarak yönetir.'
  // Flows as rows of one table: state from the health presentation, issues are late, partly failing or stalled flows.
  const flowGroups = syncPolicyGroups.filter(group => group.kind !== 'external-write-parent').map(group => ({ ...group, rows: group.policies.map(policy => {
    const externalWriteBlocked = policy.requiresExternalWrites === true && !item.externalWritesEnabled
    const effectiveEnabled = policy.enabled && !externalWriteBlocked
    const health = syncPolicyHealthPresentation(policy, effectiveEnabled)
    const issue = effectiveEnabled && (health.tone === 'delayed' || health.tone === 'degraded' || health.tone === 'stalled' || (health.tone === 'offline' && !!policy.lastSuccessAt))
    const waiting = effectiveEnabled && health.tone === 'offline' && !policy.lastSuccessAt
    const glyph: FieldState = health.tone === 'healthy' ? 'done' : issue ? 'partial' : 'none'
    return { policy, label: syncPolicyLabel(policy.resourceType), externalWriteBlocked, effectiveEnabled, health, issue, waiting, glyph }
  }) }))
  const allFlows = flowGroups.flatMap(group => group.rows)
  type FlowRow = typeof allFlows[number]
  const flowCounts = { all: allFlows.length, ok: allFlows.filter(row => row.glyph === 'done').length, issue: allFlows.filter(row => row.issue).length, wait: allFlows.filter(row => row.waiting).length, off: allFlows.filter(row => !row.effectiveEnabled).length }
  const flowShare = (count: number) => `${allFlows.length ? (count / allFlows.length) * 100 : 0}%`
  const passesFlowFilter = (row: FlowRow) => flowFilter === 'all' || (flowFilter === 'issue' && row.issue) || (flowFilter === 'wait' && row.waiting) || (flowFilter === 'off' && !row.effectiveEnabled)
  const attentionFlows = allFlows.filter(row => row.issue || row.waiting)
  const flowsAvailable = supportsSyncPolicyManagement(item.platformCode) && readConnectionReady
  const webhookSupported = item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'SHOPIFY'
  const invoiceSupported = item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || item.platformCode === 'PTTAVM'
  const writeSupported = item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || item.platformCode === 'PTTAVM' || efaturam
  const stageTestAvailable = stageTestOrderAvailable(item)
  const toolsAvailable = item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || item.platformCode === 'PTTAVM' || stageTestAvailable || stageManual
  const overview = <>
    <ConnectionAuthAlert item={item} detail={connectionTestHelp(item)} onTest={item.hasCredential ? () => run.mutate({ path: `/connections/${id}/test-jobs` }) : undefined} testing={run.isPending} />
    <div className="cs-stats"><div><small>Kimlik Bilgisi</small><b>{credentialLabel(item)}</b><span>{credentialHelp(item)}</span></div><div><small>Son Doğrulama</small><b><DateText value={item.lastTestedAt} /></b><span>{connectionTestHelp(item)}</span></div><div><small>{stageManual ? 'Test Ortamı İşlemleri' : 'Pazaryerine Yazma'}</small><b>{stageManual ? stageReady ? 'Hazır' : 'Bloke' : item.externalWritesEnabled ? 'Açık' : 'Kapalı'}</b><span>{stageManual ? stageReady ? 'Manuel denemeler doğrulamalarla çalışır.' : 'Bağlantı testi düzeltilmeden işlem başlatılmaz.' : 'Ana anahtar ve bağlantı anahtarıyla korunur.'}</span></div></div>
    {flowsAvailable && allFlows.length > 0 && <div className="cs-card"><p className="cs-label">Akış Sağlığı</p><div className="cs-health-big"><b>{flowCounts.ok} / {flowCounts.all}</b><span>Akış Güncel</span></div><div className="cs-meter" aria-hidden="true"><i className="is-ok" style={{ width: flowShare(flowCounts.ok) }} /><i className="is-warn" style={{ width: flowShare(flowCounts.issue) }} /><i className="is-rest" style={{ width: flowShare(flowCounts.wait + flowCounts.off) }} /></div><div className="cs-legend"><span><i className="is-ok" />Güncel {flowCounts.ok}</span><span><i className="is-warn" />Dikkat {flowCounts.issue}</span><span><i className="is-rest" />Bekleyen / Kapalı {flowCounts.wait + flowCounts.off}</span></div></div>}
    {flowsAvailable && allFlows.length > 0 && <div className="cs-card"><p className="cs-label">Dikkat İsteyenler</p>{attentionFlows.length ? attentionFlows.map(row => <div className="cs-issue" key={row.policy.id}><MappingStateGlyph state={row.glyph} /><div><b>{row.label}</b><small>{row.health.label}{row.policy.lastSuccessAt ? <> · son başarılı <DateText value={row.policy.lastSuccessAt} /></> : null}</small></div><span className="cs-issue-actions"><button type="button" className="secondary" onClick={() => setSyncPolicyOpen(row.policy)}>Ayarlar</button><Link className="cs-link" to="/jobs">İşlem Takibi</Link></span></div>) : <p className="cs-empty">Dikkat isteyen akış yok.</p>}</div>}
  </>
  const flowTable = syncPolicies.isLoading ? <Busy text="Eşitleme ayarları yükleniyor…" /> : syncPolicies.isError ? <ErrorBox error={syncPolicies.error} onRetry={() => void syncPolicies.refetch()} /> : <>
    <div className="cs-chips">{([['all', 'Tümü', flowCounts.all], ['issue', 'Dikkat', flowCounts.issue], ['wait', 'Bekleyen', flowCounts.wait], ['off', 'Kapalı', flowCounts.off]] as const).map(([key, label, count]) => <button type="button" key={key} className={`cs-chip${flowFilter === key ? ' is-on' : ''}`} aria-pressed={flowFilter === key} onClick={() => setFlowFilter(key)}>{label}<b>{count}</b></button>)}</div>
    <div className="cs-ftable" role="table" aria-label="Veri eşitleme akışları">
      <div className="cs-frow is-head" role="row"><span role="columnheader">Akış</span><span role="columnheader">Durum</span><span role="columnheader">Son Başarılı</span><span aria-hidden="true" /></div>
      {flowGroups.map(group => { const rows = group.rows.filter(passesFlowFilter); if (!rows.length) return null; const allOn = group.policies.every(policy => policy.enabled); return <Fragment key={group.key}>
        <div className="cs-fgroup"><b>{group.label}</b><span className="cs-count">{rows.length}</span><span className="cs-grow" /><button type="button" className="secondary cs-quiet" disabled={saveFlowGroup.isPending} onClick={() => saveFlowGroup.mutate({ policies: group.policies, enabled: !allOn, label: group.label })}>{allOn ? 'Tümünü Kapat' : 'Tümünü Aç'}</button></div>
        {rows.map(row => <div className="cs-frow" role="row" key={row.policy.id}><button type="button" className="cs-fname" title="Çalışma sıklığı ve ayrıntılar" onClick={() => setSyncPolicyOpen(row.policy)}>{row.label}</button><span className={`cs-fstate${row.issue ? ' is-warn' : ''}`}><MappingStateGlyph state={row.glyph} />{row.health.label}</span><span className="cs-ftime"><DateText value={row.policy.lastSuccessAt ?? null} /></span><span className="cs-switch-field"><em className="cs-switch-state">{row.effectiveEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" className="cs-switch" checked={row.effectiveEnabled} disabled={saveSyncPolicy.isPending || row.externalWriteBlocked} onChange={event => saveSyncPolicy.mutate({ policy: row.policy, intervalSeconds: row.policy.intervalSeconds, overlapSeconds: row.policy.overlapSeconds, enabled: event.target.checked })} aria-label={`${row.label} akışını ${row.effectiveEnabled ? 'kapat' : 'aç'}`} /></span></div>)}
      </Fragment> })}
      {!allFlows.some(passesFlowFilter) && <p className="cs-empty">Bu filtrede akış yok.</p>}
    </div>
  </>
  const settingsSections: Array<{ key: string; label: string; icon: SettingsIconName; badge?: number; node: ReactNode }> = [
    { key: 'genel', label: 'Genel Bakış', icon: 'info', badge: flowCounts.issue, node: overview },
    // The webhook sits under the data sync flows: it is one more way orders reach the panel.
    ...(flowsAvailable || webhookSupported ? [{ key: 'akis', label: 'Veri Eşitleme', icon: 'sync' as const, badge: flowCounts.issue, node: <>
      {flowsAvailable && <><div className="cs-sec-head"><h3>Veri Eşitleme Akışları</h3><p>Kapalı akış veri çekmez; açınca kaldığı yerden devam eder. Çalışma sıklığı için akışın adına tıklayın.</p></div>{flowTable}</>}
      {webhookSupported && <InstantNotificationsPanel connectionId={id} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformCode={item.platformCode} />}
    </> }] : []),
    ...(writeSupported ? [{ key: 'yaz', label: 'Pazaryerine Yazma', icon: 'write' as const, node: <>{(item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || item.platformCode === 'PTTAVM' || efaturam) && <section className={`panel integration-detail-write-panel${item.platformCode === 'SHOPIFY' ? ' shopify-write-controls' : ''}`}><div className="integration-detail-write-heading"><div><h2>{externalWriteTo} Dış Yazma</h2><p>{item.platformCode === 'SHOPIFY' ? 'Ürün, fiyat/stok ve manuel kargo/iade izinlerini ayrı ayrı yönetin. Ana anahtar kapalıysa Shopify’a hiçbir veri gönderilmez. Taşıyıcı etiketi üretimi desteklenmez.' : efaturam ? 'Fatura yalnızca siz önizlemeyi onayladığınızda, sipariş başına bir kez kesilir; hiçbir zaman otomatik oluşturulmaz. Anahtar kapalıyken E-Faturam’a fatura gönderilmez.' : item.platformCode === 'PAZARAMA' ? 'Otomatik dış yazmalar fiyat ve stok değişikliklerini yönetir. Anahtar kapalıyken Pazarama’ya hiçbir veri gönderilmez; iade kararı ve ürün açma sonraki aşamalarda eklenecek.' : item.platformCode === 'N11' ? 'Otomatik dış yazmalar fiyat ve stok değişikliklerini, manuel dış yazmalar ise sipariş işleme alma, paket bölme ve iade kararlarını yönetir.' : item.platformCode === 'HEPSIBURADA' ? 'Ürün/fiyat/stok otomatik gönderimleri varsayılan olarak kapalıdır. Manuel dış yazmalar yalnızca destek kanıtı olan paket veya iade işlemlerinde ayrıca açılabilir.' : 'Otomatik dış yazmalar fiyat ve stok değişikliklerini, manuel dış yazmalar ise kargo, etiket ve iade kararlarını yönetir.'}</p></div><span className={`integration-detail-write-status ${externalWritesEnabled ? 'is-enabled' : ''}`}>{externalWritesEnabled ? 'Açık' : 'Kapalı'}</span></div><label className="integration-write-toggle"><em className="cs-switch-state">{externalWritesEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" checked={externalWritesEnabled} disabled={saveExternalWrites.isPending || !readConnectionReady} onChange={event => saveExternalWrites.mutate(event.target.checked)} /><span><strong>{item.platformCode === 'SHOPIFY' ? 'Shopify Dış Yazma Ana Anahtarı' : `${externalWriteTo} Dış Yazma`}</strong><small>{item.platformCode === 'SHOPIFY' ? externalWritesEnabled ? 'Ana anahtar açık. Otomatik ve manuel izinleri aşağıda ayrı ayrı yönetin.' : 'Kapalıyken ürün ve diğer dış yazma grupları devre dışı kalır.' : efaturam ? (externalWritesEnabled ? 'Açık. Onayladığınız fatura istekleri E-Faturam’a gönderilir.' : 'Kapalıyken panel E-Faturam’dan veri okur; fatura oluşturmaz.') : externalWritesEnabled ? 'Açık akışlar aşağıdaki otomatik ve manuel seçeneklerden ayrı ayrı yönetilir.' : `Panel ${externalWriteFrom} veri çeker; platforma dışarı veri göndermez.`}</small></span></label>{item.platformCode === 'SHOPIFY' && (syncPolicies.isLoading ? <Busy text="Shopify dış yazma seçenekleri yükleniyor…" /> : syncPolicies.isError ? <ErrorBox error={syncPolicies.error} onRetry={() => void syncPolicies.refetch()} /> : <div className="shopify-write-toggle-list" aria-label="Shopify otomatik ve manuel dış yazma seçenekleri">{externalWriteGroups.map(renderExternalWriteGroup)}</div>)}</section>}
        {item.platformCode !== 'SHOPIFY' && flowsAvailable && syncPolicyGroups.some(group => group.kind === 'external-write-parent') && <div className="cs-write-groups">{externalWriteGroups.map(renderExternalWriteGroup)}</div>}</> }] : []),
    ...(invoiceSupported ? [{ key: 'fatura', label: 'Faturalama', icon: 'invoice' as const, node: <>{(item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || item.platformCode === 'PTTAVM') && <section className="panel integration-detail-write-panel invoice-creation-setting-panel" aria-labelledby="invoice-creation-setting-title"><div className="integration-detail-write-heading"><div><p className="eyebrow">FATURALAMA</p><h2 id="invoice-creation-setting-title">Fatura Oluşturma</h2><p>Bu ayar yalnızca {item.displayName} bağlantısındaki siparişleri etkiler.</p></div></div><label className="integration-write-toggle"><em className="cs-switch-state">{item.invoiceCreationEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" checked={item.invoiceCreationEnabled} disabled={saveInvoiceCreation.isPending} onChange={event => saveInvoiceCreation.mutate(event.target.checked)} /><span><strong>Bu Bağlantıda Fatura Oluşturma</strong><small>{item.invoiceCreationEnabled ? 'Fatura oluşturma ve yeni fatura takip kaydı açma kullanılabilir.' : 'Kapalıyken fatura oluşturma düğmeleri pasif olur. Mevcut fatura kayıtları ve yüklenmiş belgeler etkilenmez.'}</small></span></label></section>}</> }] : []),
    { key: 'magaza', label: 'Mağaza ve Kimlik', icon: 'key', node: <>{item.platformCode === 'TRENDYOL_EFATURAM' && <article className="panel"><h2>E-Faturam Hesap Yetkisi</h2><p>Fatura API'si bağlı bireysel E-Faturam hesabının oturumunu kullanır. Firma ve kullanıcı kimliği yalnız sağlayıcının erişim tokenından alınır; panelde gösterilmez veya ayar olarak saklanmaz.</p><div className="card-list"><div className="record-card"><span><strong>Belge Türü</strong><small>Kurumsal ve E-Fatura mükellefi: TEMELFATURA; diğer siparişler: EARSIVFATURA</small></span><Badge value="AUTO" /></div><div className="record-card"><span><strong>İnternet Satışı Ek Alanları</strong><small>Kullanıcı ayarı değildir; gerekli olduğunda Trendyol siparişi ve resmî kargo kataloğundan otomatik hazırlanır.</small></span><Badge value="AUTO" /></div></div></article>}
        <section className="panel connection-settings-panel"><div className="connection-settings-section"><h2>Mağaza Bilgileri</h2><p>{item.platformCode === 'HEPSIBURADA' ? 'Mağaza adını ve Mağaza ID’sini burada güncelleyin. Bağlantı testi önce entegratör kullanıcı adını, 401 yanıtında Mağaza ID’sini Basic Auth kullanıcı adı olarak dener; başarılı olan değer şifreli kayda alınır. Servis anahtarı şifreli saklanır.' : item.platformCode === 'PTTAVM' ? 'Kayıt adını ve Mağaza ID’sini buradan güncelleyin. Mağaza ID PTT AVM satıcı paneli › Hesap Yönetimi › Entegrasyon Bilgileri ekranındadır; değiştirilirse API kullanıcı adı ve şifresi yeniden girilmelidir.' : 'Bağlantı kayıt adı ve mağaza bilgilerini buradan güncelleyin.'}</p><form onSubmit={event => { event.preventDefault(); saveConnectionDetails.mutate() }}><div className="connection-settings-fields"><label>{item.platformCode === 'HEPSIBURADA' ? 'Mağaza Adı' : 'Kayıt Adı'}<input value={displayName} onChange={event => setDisplayName(event.target.value)} autoComplete="off" required /></label><label>{item.platformCode === 'TRENDYOL_EFATURAM' ? 'VKN / TCKN' : item.platformCode === 'SHOPIFY' ? 'Shopify Mağaza Adresi' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada Mağaza ID' : item.platformCode === 'N11' ? 'N11 Mağaza Adı' : item.platformCode === 'PAZARAMA' ? 'Pazarama Mağaza Adı' : item.platformCode === 'PTTAVM' ? 'PTT AVM Mağaza ID' : 'Mağaza Kimliği (Seller ID)'}<input value={externalStoreId} inputMode={item.platformCode === 'PTTAVM' ? 'numeric' : undefined} pattern={item.platformCode === 'PTTAVM' ? '[0-9]+' : undefined} onChange={event => setExternalStoreId(event.target.value)} autoComplete="off" required /></label></div><div className="connection-settings-input-row">{item.platformCode !== 'HEPSIBURADA' && <span className="connection-settings-field-help">Kartlarda ve bağlantı başlığında gösterilir.</span>}<button disabled={saveConnectionDetails.isPending}>{saveConnectionDetails.isPending ? 'Kaydediliyor…' : 'Bilgileri Kaydet'}</button></div>{item.platformCode === 'SHOPIFY' && <small className="connection-settings-field-help">Tam Shopify mağaza adresi: magazaniz.myshopify.com</small>}</form></div><div className="connection-settings-divider" /><form className={`connection-settings-section ${item.platformCode === 'SHOPIFY' ? 'is-shopify' : ''}`} onSubmit={credential}><h2>{item.platformCode === 'SHOPIFY' ? 'Shopify Uygulama Yetkisi' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada Servis Kimlik Bilgileri' : 'API Bilgileri'}</h2>{item.platformCode === 'TRENDYOL_EFATURAM' ? <><label>Hesap E-Postası<input name="email" type="email" autoComplete="username" required /></label><label>Hesap Parolası<input name="password" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label><p>Bu alanlar yalnız şifreli olarak saklanır ve tekrar gösterilmez.</p></> : item.platformCode === 'SHOPIFY' ? <><label>Shopify Uygulama Tokenı<input name="shopifyAccessToken" type="password" autoComplete="off" placeholder={item.hasCredential ? '••••••••••••' : 'shpat_…'} required /><p>Okuma için read_products, read_inventory, read_orders, read_locations; ürün/fiyat yazması için write_products ve write_inventory; Online Store yayınını bulup etkinleştirmek için read_publications ve write_publications; iadeleri okuyup onaylamak için read_returns (veya read_marketplace_returns) ile write_returns (veya write_marketplace_returns); fulfillment takibi için write_assigned_fulfillment_orders, write_merchant_managed_fulfillment_orders veya write_third_party_fulfillment_orders izni gerekir. Shopify mağaza kullanıcısında ayrıca fulfill_and_ship_orders yetkisi bulunmalıdır. İzinleri uygulamaya ekleyip mağazayı yeniden yetkilendirin, ardından yeni tokenı burada şifreli kaydedip bağlantıyı tekrar test edin. Taşıyıcı etiketi Shopify’dan satın alınmaz veya üretilmez.</p></label></> : item.platformCode === 'HEPSIBURADA' ? <><label>Hepsiburada Entegratör Kullanıcı Adı<input name="hepsiburadaIntegratorUsername" autoComplete="username" defaultValue={hepsiburadaDefaultIntegratorUsername} required /></label><label>Hepsiburada Servis Anahtarı<input name="hepsiburadaServiceKey" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label></> : <><label>{item.platformCode === 'N11' ? 'N11 API Anahtarı (App Key)' : item.platformCode === 'PAZARAMA' ? 'Pazarama Client ID' : item.platformCode === 'PTTAVM' ? 'PTT AVM API Kullanıcı Adı' : 'API Key'}<input name="apiKey" type="text" autoComplete="off" spellCheck={false} placeholder={item.hasCredential ? 'Kayıtlı anahtarı değiştirmek için yazın' : undefined} required /></label><label>{item.platformCode === 'N11' ? 'N11 API Şifresi (App Secret)' : item.platformCode === 'PAZARAMA' ? 'Pazarama Client Secret' : item.platformCode === 'PTTAVM' ? 'PTT AVM API Şifresi' : 'API Secret'}<input name="apiSecret" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label></>}<button disabled={saveCredential.isPending}>{saveCredential.isPending ? 'Kaydediliyor…' : 'Şifreli Kaydet'}</button><button type="button" className="secondary" disabled={!item.hasCredential || run.isPending || saveCredential.isPending} onClick={() => run.mutate({ path: `/connections/${id}/test-jobs` })}>Bağlantıyı Test Et</button></form></section></> },
    ...(toolsAvailable ? [{ key: 'arac', label: 'Araçlar', icon: 'tool' as const, node: <>{item.platformCode === 'N11' && <N11PublishSettingsPanel key={item.version} connectionId={id} displayName={item.displayName} version={item.version} shipmentTemplate={item.n11ShipmentTemplate} preparingDay={item.n11PreparingDay} />}
        {item.platformCode === 'N11' && <PriceStockSendPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformName="N11" codeLabel="stok kodu" codeAccusative="stok kodunu" codeExample="BS74297" />}
        {item.platformCode === 'PAZARAMA' && <PazaramaSaleStatusPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} />}
        {item.platformCode === 'PAZARAMA' && <PriceStockSendPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformName="Pazarama" codeLabel="barkod" codeAccusative="barkodu" codeExample="BS74297" />}
        {item.platformCode === 'PTTAVM' && <PriceStockSendPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformName="PTT AVM" codeLabel="barkod" codeAccusative="barkodu" codeExample="8690000000017" />}
        {item.platformCode === 'HEPSIBURADA' && item.environment === 'STAGE' && <HepsiburadaSitPriceStockPanel connectionId={id} connectionReady={stageReady} />}
        {stageTestAvailable && <div className="panel"><h2>Test Ortamında Yeni Sipariş</h2><p>Yalnız test ortamında tek seferlik deneme siparişi oluşturur. Hepsiburada için bağlı hbSku’su olan, yayındaki bir ürün; yoksa SIT hesabından hbSku ile içe aktarılmış aktif bir ürün seçilir. Tıklayınca pazaryerinin test ortamında sipariş açılır; sipariş numarası İşlem Takibi’nde görünür.</p><button type="button" disabled={!item.hasCredential || run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/stage-test-order-jobs` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Test Siparişi Oluştur'}</button></div>}
        {stageManual && <div className="panel"><h2>Test Ortamı İşlem Durumu</h2><p>Elle yapılan denemeler bağlantıyı, kayıtlı API bilgilerini, girilen değerleri, tekrar korumasını ve pazaryerinin yanıtını denetler.</p><p>Teknik ayrıntılı hata kayıtları İşlem Takibi’nde tutulur.</p></div>}</> }] : [])
  ]
  const activeSection = settingsSections.find(section => section.key === settingsSection) ?? settingsSections[0]
  const metaLine = `${connectionEnvironmentLabel(item.platformCode, displayEnvironment)} · ${efaturam ? 'Yerel kapsam' : `Mağaza ${item.externalStoreId}`} · API ${item.apiVersion}`
    return <div className={`${rootClass} cs-workspace${inline ? ' is-inline' : ''}`}>
    <header className="cs-head">{!inline && <Link to="/integrations" className="cs-back"><UiIcon name="arrowLeft" /> Platformlar</Link>}<img className={`cs-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /><div className="cs-head-copy"><div className="cs-title-line"><h2>{item.displayName}</h2><span className="cs-tags-row"><span className={`cs-tag${readConnectionReady ? ' is-on' : ''}`}><MappingStateGlyph state={readConnectionReady ? 'done' : 'none'} />{statusLabel(item.status)}</span><span className="cs-tag">{stageManual ? 'Manuel akış' : 'Canlı akış'}</span></span></div><p>{metaLine}</p></div><span className="cs-grow" />{onClose && <button type="button" className="cs-close" onClick={onClose} aria-label="Bağlantı ayarlarını kapat"><UiIcon name="close" /></button>}</header>
    <div className="cs-body"><nav className="cs-nav" aria-label="Ayar bölümleri">{settingsSections.map(section => <button type="button" key={section.key} className={section.key === activeSection.key ? 'is-on' : undefined} aria-current={section.key === activeSection.key ? 'true' : undefined} onClick={event => { setSettingsSection(section.key); event.currentTarget.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' }) }}><SettingsIcon name={section.icon} /><span>{section.label}</span>{section.badge ? <span className="cs-count is-warn">{section.badge}</span> : <span />}</button>)}</nav><section className="cs-pane" key={activeSection.key}>{activeSection.node}</section></div>
    {syncPolicyOpen && <SyncPolicyEditorModal policy={syncPolicyOpen} label={syncPolicyLabel(syncPolicyOpen.resourceType)} intervals={syncPolicyIntervals} overlaps={syncPolicyOverlaps} healthHelp={syncPolicyHealthHelp(syncPolicyOpen)} purpose={syncPolicyDescription(syncPolicyOpen.resourceType)} externalWriteBlocked={syncPolicyOpen.requiresExternalWrites === true && !item.externalWritesEnabled} saving={saveSyncPolicy.isPending} running={runSyncPolicy.isPending} onClose={() => setSyncPolicyOpen(null)} onSave={values => saveSyncPolicy.mutate({ policy: syncPolicyOpen, ...values, closeModal: true })} onRunNow={() => runSyncPolicy.mutate(syncPolicyOpen)} />}
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </div>

}
export function IntegrationDetailPage() {
  const { id = '' } = useParams()
  return <IntegrationDetailWorkspace id={id} />
}
