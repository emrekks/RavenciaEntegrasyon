import type { ApiAnswer } from '../../shared/api/api-answer'
import { useEffect, useLayoutEffect, useRef, useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, hubPoll, loadAllPages } from '../../shared/api'
import { LoadingState, Modal } from '../../shared/components'
import { normalizeShippingLabelSettings, useShippingLabelSettings } from '../shipping'
import { SettingsNotice, type SettingsNoticeState } from './SettingsNotifications'
import { unknownCount, useRememberedTabCounts } from '../../shared/tab-counts'
import { marketplaceResetCount, runMarketplaceResets, type BulkResetProgress } from './marketplace-bulk-reset'
import { connectionFullResetSupported } from '../marketplace/connection-data-reset'
import { dataResetConfirmWord, panelResetConfirmed, togglePanelResetCategories } from './panel-factory-reset'
import type { Connection } from '../marketplace/shared'
import './settings-sade.css'
import '../../shared/view-tabs.css'
import { useTabSlideInPlace } from '../../shared/tab-slide'
import { useLacksElevatedRole } from '../elevated-role'
import { reauthBody } from './user-management'
import { queryKeys } from '../../shared/api/query-keys'
import { type SettingsSectionKey, type ShippingSection, type AppearanceSection, settingsSectionGroups, settingsSections, formatSettingsDate, SettingsIcon, SettingsGlyph, SettingsPaneHead, mfaErrorMessage } from './settings-ui'
import { resetGroupsOf, type ResetMarketplace, SettingsDataReset } from './sections/DataResetSection'
import { AppearanceSettingsSection } from './sections/AppearanceSection'
import { ShippingLabelSettingsPanel } from './sections/ShippingLabelSection'
import { OffsiteBackupSection } from './sections/OffsiteBackupSection'
import { AlertEmailSettingsSection } from './sections/AlertEmailSection'
import { UserManagementSection } from './sections/UserManagementSection'
import { AiSettingsSection } from './sections/AiSettingsSection'

type SecuritySession = ApiAnswer<'getAuthSessions'>[number]
type MfaSetup = { otpauthUri: string; secret?: string; qrSvg: string; expiresAt: string }
type SessionConfirmation = { action: 'revoke' | 'revoke-others' | 'delete' | 'delete-closed'; sessionId?: string }

const settingsSectionOrder = settingsSections.map(item => item.key)
// Links from before the section list still open the matching first section.
const legacySettingsSections: Record<string, SettingsSectionKey> = { security: 'auth', shipping: 'sender', appearance: 'read' }
const shippingSections: SettingsSectionKey[] = ['sender', 'dims', 'designer']
const appearanceSections: SettingsSectionKey[] = ['read', 'palette', 'themes', 'notify']

function settingsSectionFrom(value: string | null): SettingsSectionKey {
  if (value && legacySettingsSections[value]) return legacySettingsSections[value]
  return settingsSections.find(item => item.key === value)?.key ?? 'auth'
}

function OtpInput({ digits, onChange }: { digits: string[]; onChange: (digits: string[]) => void }) {
  const refs = useRef<Array<HTMLInputElement | null>>([])
  function fill(start: number, text: string) {
    const clean = text.replace(/\D/g, '').slice(0, digits.length - start)
    const next = [...digits]
    if (!clean) {
      next[start] = ''
      onChange(next)
      return
    }
    clean.split('').forEach((digit, offset) => { next[start + offset] = digit })
    onChange(next)
    refs.current[Math.min(digits.length - 1, start + clean.length)]?.focus()
  }
  return <div className="ss-otp" role="group" aria-label="6 haneli doğrulama kodu">{digits.map((digit, index) => <input key={index} ref={element => { refs.current[index] = element }} inputMode="numeric" autoComplete={index === 0 ? 'one-time-code' : 'off'} data-autofocus={index === 0 || undefined} maxLength={6} value={digit} aria-label={`${index + 1}. hane`} onChange={event => { const raw = event.target.value; fill(index, raw.length > 1 && digit ? raw.replace(digit, '') : raw) }} onKeyDown={event => { if (event.key === 'Backspace' && !digit && index > 0) refs.current[index - 1]?.focus() }} onPaste={event => { event.preventDefault(); fill(index, event.clipboardData.getData('text')) }} />)}</div>
}

const mfaSteps: Array<{ key: 'password' | 'verify' | 'recovery'; label: string; hint: string }> = [
  { key: 'password', label: 'Parola', hint: 'Hesabı doğrula' },
  { key: 'verify', label: 'QR Kod', hint: 'Uygulamaya ekle' },
  { key: 'recovery', label: 'Kurtarma Kodları', hint: 'Güvenle sakla' }
]
const emptyOtp = ['', '', '', '', '', '']

// The key an authenticator app asks for when the QR code cannot be scanned: the base32 secret, in groups of four.
export function manualSetupKey(otpauthUri: string) {
  const secret = /[?&]secret=([A-Z2-7]+)/i.exec(otpauthUri)?.[1]?.toUpperCase() ?? ''
  return secret.match(/.{1,4}/g)?.join(' ') ?? ''
}

export function SystemSettingsPage() {
  const client = useQueryClient()
  const [searchParams, setSearchParams] = useSearchParams()
  const section = settingsSectionFrom(searchParams.get('tab'))
  const shippingSettings = useShippingLabelSettings()
  // The account's label settings are written by the owner and administrators only (PUT settings/shipping-label).
  const labelSaveDenied = useLacksElevatedRole()
  const labelSettings = shippingSettings.settings
  // Normalizing fills block defaults (font size, rotation) only on a second pass, so the saved copy is normalized twice
  // to compare like with like.
  const savedLabelSettings = shippingSettings.isSuccess ? normalizeShippingLabelSettings(normalizeShippingLabelSettings(shippingSettings.data.settings)) : null
  const shippingDirty = savedLabelSettings ? JSON.stringify(normalizeShippingLabelSettings(labelSettings)) !== JSON.stringify(savedLabelSettings) : shippingSettings.isError
  const [labelFormat, setLabelFormat] = useState<'a4' | 'sticker'>(() => labelSettings.defaultFormat)
  // The designer opens on the account's default format, which may arrive after this page mounted.
  const labelFormatSynced = useRef(shippingSettings.isSuccess)
  const serverDefaultFormat = shippingSettings.data?.settings != null ? normalizeShippingLabelSettings(shippingSettings.data.settings).defaultFormat : undefined
  useLayoutEffect(() => {
    if (labelFormatSynced.current || !shippingSettings.isSuccess) return
    labelFormatSynced.current = true
    if (serverDefaultFormat) setLabelFormat(serverDefaultFormat)
  }, [serverDefaultFormat, shippingSettings.isSuccess])
  const [labelSaving, setLabelSaving] = useState(false)
  const [appearanceStatus, setAppearanceStatus] = useState({ dirty: false, themeCount: 0 })
  const [resetScopes, setResetScopes] = useState<string[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  // Resets take the password again (and the authenticator code when it is on); one check covers the run's resets.
  const [resetPassword, setResetPassword] = useState('')
  const [resetFactor, setResetFactor] = useState('')
  const [resetBusy, setResetBusy] = useState(false)
  const [marketplacePicked, setMarketplacePicked] = useState<string[]>([])
  const [marketplaceProgress, setMarketplaceProgress] = useState<Record<string, BulkResetProgress>>({})
  const resetCancelled = useRef(false)
  useEffect(() => { resetCancelled.current = false; return () => { resetCancelled.current = true } }, [])
  // What each list holds now, the counts the reset reports when it is done. Owner and administrators only: for the
  // other roles the server answers 403 and the page asks nothing else.
  const resetView = useQuery({ queryKey: queryKeys.panelFactoryReset(), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getSettingsFactoryReset'>>('/settings/factory-reset', { signal }), enabled: section === 'database', staleTime: 0, retry: false })
  const resetForbidden = resetView.error instanceof ApiRequestError && resetView.error.status === 403
  const resetGroups = resetGroupsOf(resetView.data)
  // The active marketplace connections, each with what its Tam Sıfırlama would remove (the same view its window shows).
  const resetConnections = useQuery({ queryKey: queryKeys.connections('settings-reset'), queryFn: async ({ signal }) => (await loadAllPages<Connection>('/connections', { signal })).items.filter(item => item.status !== 'DELETED' && item.status !== 'HIDDEN' && connectionFullResetSupported(item.platformCode)), enabled: section === 'database' && resetView.isSuccess, staleTime: 0 })
  const resetViews = useQueries({ queries: (resetConnections.data ?? []).map(item => ({ queryKey: queryKeys.connectionFullReset(item.id), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getConnectionsByIdFullReset'>>(`/connections/${item.id}/full-reset`, { signal }), enabled: section === 'database' && resetView.isSuccess, staleTime: 0 })) })
  const resetMarketplaces: ResetMarketplace[] = (resetConnections.data ?? []).map((item, index) => ({ id: item.id, platformCode: item.platformCode, displayName: item.displayName, passive: item.status !== 'ACTIVE' && item.status !== 'VERIFIED', count: marketplaceResetCount(resetViews[index]?.data) }))
  // Through hubApi: a session that ended shows the sign-in screen instead of an error box.
  const status = useQuery({ queryKey: queryKeys.security(), queryFn: ({ signal }) => hubApi<ApiAnswer<'getAuthSecurityStatus'>>('/auth/security-status', { signal }) })
  const sessions = useQuery({ queryKey: queryKeys.sessions(), queryFn: ({ signal }) => hubApi<ApiAnswer<'getAuthSessions'>>('/auth/sessions', { signal }) })
  const [sessionCount] = useRememberedTabCounts('Ayarlar oturumlar', [{ value: 'sessions', count: sessions.isError ? unknownCount : sessions.data?.length }])
  const [mfaStep, setMfaStep] = useState<'closed' | 'password' | 'verify' | 'recovery'>('closed')
  const [setup, setSetup] = useState<MfaSetup | null>(null)
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([])
  const [otp, setOtp] = useState<string[]>(emptyOtp)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const succeed = (message: string) => setNotice({ kind: 'success', message })
  const fail = (message: string) => setNotice({ kind: 'error', message })
  const [mfaError, setMfaError] = useState('')
  const [sessionConfirmation, setSessionConfirmation] = useState<SessionConfirmation | null>(null)
  const [sessionActionBusy, setSessionActionBusy] = useState(false)
  const totpEnabled = status.data?.totpState === 'ENABLED'
  // The current device leads the active list.
  const activeSessions = (sessions.data ?? []).filter(session => session.state === 'ACTIVE').sort((left, right) => Number(right.current) - Number(left.current))
  const activeOtherSessions = activeSessions.filter(session => !session.current)
  const closedSessions = (sessions.data ?? []).filter(session => !session.current && session.state !== 'ACTIVE')

  function selectSection(next: SettingsSectionKey) {
    setNotice(null)
    setSearchParams(next === 'auth' ? {} : { tab: next })
  }
  function toggleResetScopes(scopes: string[], checked: boolean) {
    // A category brings the ones it requires (orders bring their invoices, products their stock and prices).
    setResetScopes(current => togglePanelResetCategories(resetView.data?.groups ?? [], current, scopes, checked))
  }
  function toggleMarketplaces(ids: string[], checked: boolean) {
    setMarketplacePicked(current => checked ? Array.from(new Set([...current, ...ids])) : current.filter(value => !ids.includes(value)))
  }
  async function resetOperationalData() {
    // The page's own confirmation word is the gate for the marketplaces and the catalog alike.
    const resetConfirmWord = dataResetConfirmWord(resetView.data?.groups.length ?? 0, resetScopes.length)
    if ((!resetScopes.length && !marketplacePicked.length) || !panelResetConfirmed(resetConfirmation, resetConfirmWord)) return
    setResetBusy(true); setNotice(null)
    try {
      // The marketplaces first, one after the other; then the catalog cleanup.
      const chosen = (resetConnections.data ?? []).filter(item => marketplacePicked.includes(item.id))
      if (chosen.length) {
        const ok = await runMarketplaceResets(chosen, {
          request: item => hubApi<ApiAnswer<'postConnectionsByIdFullReset'>>(`/connections/${item.id}/full-reset`, { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation: item.displayName, ...reauthBody(resetPassword, resetFactor) }) }),
          poll: item => hubPoll<ApiAnswer<'getConnectionsByIdFullReset'>>(`/connections/${item.id}/full-reset`),
          sleep: ms => new Promise(resolve => window.setTimeout(resolve, ms)),
          onProgress: (id, state) => setMarketplaceProgress(current => ({ ...current, [id]: state })),
          isCancelled: () => resetCancelled.current
        })
        if (!ok) { if (!resetCancelled.current) fail('Pazaryeri sıfırlaması tamamlanamadı; sonraki işlemler başlatılmadı. Ayrıntı ilgili satırda.'); return }
        setMarketplacePicked([])
        if (!resetScopes.length) succeed(`${chosen.length} pazaryeri bağlantısı tam sıfırlandı. Otomatik eşitleme açıksa kayıtlar yeniden çekilir.`)
      }
      if (resetScopes.length) {
        // One job for every chosen category (the same one Paneli Tamamen Sıfırla runs); its steps show below.
        const next = await hubApi<ApiAnswer<'postSettingsFactoryReset'>>('/settings/factory-reset', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ categories: resetScopes, confirmation: resetConfirmation.trim(), ...reauthBody(resetPassword, resetFactor) }) })
        client.setQueryData(queryKeys.panelFactoryReset(), next)
        succeed('Seçilen veriler silinmeye başladı; adımlar sayfanın altındaki Sıfırlama İlerlemesi bölümünde.')
      }
      setResetScopes([]); setResetConfirmation(''); setResetPassword(''); setResetFactor('')
      await client.invalidateQueries()
    } catch (reason) { fail(reason instanceof Error ? reason.message : 'Seçili veriler sıfırlanamadı.') } finally { setResetBusy(false) }
  }

  function openMfa() {
    setNotice(null); setMfaError(''); setOtp(emptyOtp); setMfaStep('password')
  }
  async function prepareMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setMfaError('')
    const data = new FormData(event.currentTarget)
    try {
      // The setup takes the password itself (and, when an authenticator is already on, its current code).
      const enrollment = await hubApi<MfaSetup>('/auth/mfa/setup', { method: 'POST', body: JSON.stringify({ password: data.get('password') }) })
      setSetup(enrollment); setOtp(emptyOtp); setMfaStep('verify')
    } catch (reason) { setMfaError(mfaErrorMessage(reason, 'Güvenli kurulum başlatılamadı.')) } finally { setBusy(false) }
  }
  async function confirmMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const code = otp.join('')
    if (!/^[0-9]{6}$/.test(code)) {
      setMfaError('6 haneli doğrulama kodunu girin.')
      return
    }
    setBusy(true); setMfaError('')
    try {
      const result = await hubApi<{ recoveryCodes: string[] }>('/auth/mfa/confirm', { method: 'POST', body: JSON.stringify({ code }) })
      setRecoveryCodes(result.recoveryCodes); setMfaStep('recovery'); await client.invalidateQueries({ queryKey: queryKeys.security() })
    } catch (reason) { setMfaError(mfaErrorMessage(reason, 'Doğrulayıcı etkinleştirilemedi.')) } finally { setBusy(false) }
  }
  // The recovery codes are shown once: Escape or a click outside does not close this step, only "Kodları Sakladım".
  function closeMfa() {
    if (busy) return
    if (mfaStep === 'recovery') { setMfaError('Kurtarma kodlarını sakladıktan sonra “Kodları Sakladım” ile kapatın; bu kodlar yeniden gösterilmez.'); return }
    setMfaStep('closed')
  }
  async function copyRecoveryCodes() {
    try {
      await navigator.clipboard.writeText(recoveryCodes.join('\n'))
      succeed('Kurtarma kodları panoya kopyalandı.')
    } catch { fail('Kurtarma kodları kopyalanamadı; kodları elle not alın.') }
  }
  async function confirmSessionAction() {
    const confirmation = sessionConfirmation
    if (!confirmation || sessionActionBusy) return
    setSessionActionBusy(true); setNotice(null)
    try {
      if (confirmation.action === 'revoke' && confirmation.sessionId) {
        await hubApi(`/auth/sessions/${confirmation.sessionId}/revoke`, { method: 'POST' })
        succeed('Oturum sonlandırıldı.')
      } else if (confirmation.action === 'revoke-others') {
        await hubApi('/auth/sessions/revoke-others', { method: 'POST' })
        succeed('Diğer aktif oturumlar sonlandırıldı.')
      } else if (confirmation.action === 'delete' && confirmation.sessionId) {
        await hubApi(`/auth/sessions/${confirmation.sessionId}`, { method: 'DELETE' })
        succeed('Kapalı oturum silindi.')
      } else if (confirmation.action === 'delete-closed') {
        await hubApi('/auth/sessions/closed', { method: 'DELETE' })
        succeed('Kapalı oturumlar silindi.')
      }
      await client.invalidateQueries({ queryKey: queryKeys.sessions() })
    } catch {
      fail(confirmation.action.startsWith('delete') ? 'Kapalı oturum silinemedi.' : 'Oturumlar sonlandırılamadı.')
    } finally {
      setSessionActionBusy(false)
      setSessionConfirmation(null)
    }
  }
  async function saveLabelSettings(overwrite = false) {
    setLabelSaving(true); setNotice(null)
    try {
      await (overwrite ? shippingSettings.overwrite() : shippingSettings.save(labelSettings))
      succeed('Kargo etiketi ayarları hesaba kaydedildi.')
    } catch (reason) {
      fail(reason instanceof Error ? reason.message : 'Kargo etiketi ayarları kaydedilemedi.')
    } finally { setLabelSaving(false) }
  }

  function navMeta(key: SettingsSectionKey) {
    if (key === 'auth') return status.data ? <SettingsGlyph state={totpEnabled ? 'done' : 'none'} /> : <svg className="ss-st" aria-hidden="true" style={{ visibility: 'hidden' }} />
    if (key === 'sessions') return sessionCount.count !== undefined ? <span className="ss-count rv-tab-count">{sessionCount.count}</span> : null
    if (key === 'database') return resetScopes.length ? <span className="ss-count rv-tab-count is-warn">{resetScopes.length}</span> : null
    if (key === 'palette') return appearanceStatus.dirty ? <SettingsGlyph state="pending" /> : null
    return null
  }
  function sessionRow(session: SecuritySession) {
    return <div key={session.id} className={`ss-ses${session.current ? ' is-current' : ''}`}>
      <span className="ss-tile"><SettingsIcon name={session.current ? 'device' : 'phone'} /></span>
      <div className="ss-ses-who"><b>{session.current ? 'Bu Cihaz' : 'Diğer Oturum'}</b><small><SettingsGlyph state={session.state === 'ACTIVE' ? 'done' : 'none'} />{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'}</small></div>
      <div className="ss-ses-when"><span>Son etkinlik {formatSettingsDate(session.lastSeenAt)}</span><small>Bitiş {formatSettingsDate(session.expiresAt)}</small></div>
      <div className="ss-ses-act">{session.current ? <span className="ss-tag is-ink is-lg"><SettingsGlyph state="done" />Mevcut Oturum</span> : session.state === 'ACTIVE' ? <button type="button" className="ss-btn is-danger" onClick={() => setSessionConfirmation({ action: 'revoke', sessionId: session.id })}><SettingsIcon name="logout" />Oturumu Sonlandır</button> : <button type="button" className="ss-btn is-danger is-quiet" onClick={() => setSessionConfirmation({ action: 'delete', sessionId: session.id })}><SettingsIcon name="trash" />Kaydı Sil</button>}</div>
    </div>
  }

  const sessionConfirmationCopy = sessionConfirmation?.action === 'revoke'
    ? { title: 'Oturumu Sonlandır', description: 'Bu cihazın bağlantısı sonlandırılacak. Devam etmek istiyor musunuz?', confirm: 'Oturumu Sonlandır' }
    : sessionConfirmation?.action === 'revoke-others'
      ? { title: 'Diğer Oturumları Kapat', description: `${activeOtherSessions.length} aktif oturum sonlandırılacak. Devam etmek istiyor musunuz?`, confirm: 'Oturumları Kapat' }
      : sessionConfirmation?.action === 'delete'
        ? { title: 'Kapalı Oturum Kaydını Sil', description: 'Bu kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?', confirm: 'Kaydı Sil' }
        : sessionConfirmation?.action === 'delete-closed'
          ? { title: 'Kapalı Oturumları Sil', description: `${closedSessions.length} kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?`, confirm: 'Kayıtları Sil' }
          : null
  const affectedSessions = !sessionConfirmation ? [] : sessionConfirmation.sessionId ? (sessions.data ?? []).filter(session => session.id === sessionConfirmation.sessionId) : sessionConfirmation.action === 'revoke-others' ? activeOtherSessions : closedSessions
  const mfaIndex = Math.max(0, mfaSteps.findIndex(step => step.key === mfaStep))
  const activeGroup = settingsSectionGroups.find(group => group.items.some(item => item.key === section)) ?? settingsSectionGroups[0]
  const isShipping = shippingSections.includes(section)
  const isAppearance = appearanceSections.includes(section)
  // A group or section tab slides the pane in from its side and starts the page from its top (shared/tab-slide.tsx).
  // The pane stays mounted (the shipping and appearance forms keep their drafts), so the slide plays in place.
  const paneRef = useRef<HTMLDivElement>(null)
  useTabSlideInPlace(paneRef, section, settingsSectionOrder)
  // Phones: the group tabs scroll sideways; bring the selected one into view (a deep link to Görünüm left it off-screen).
  const groupTabsRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const row = groupTabsRef.current
    const tab = row?.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!row || !tab || row.scrollWidth <= row.clientWidth + 1) return
    const rowBox = row.getBoundingClientRect(); const tabBox = tab.getBoundingClientRect()
    row.scrollLeft += tabBox.left - rowBox.left - (rowBox.width - tabBox.width) / 2
  }, [activeGroup])

  return <section className="content ss-page">
    {notice && <SettingsNotice {...notice} />}
    <header className="ss-page-head"><span className="ss-crumb">Ayarlar</span><h1>Sistem Ayarları</h1></header>
    <div ref={groupTabsRef} className="ss-gtabs rv-tabs rv-view-tabs" role="tablist" aria-label="Ayar grupları">{settingsSectionGroups.map(group => <button type="button" role="tab" key={group.label} aria-selected={group === activeGroup} className={group === activeGroup ? 'is-on is-active' : undefined} onClick={() => selectSection(group.items[0].key)}><span className="rv-tab-label"><SettingsIcon name={group.icon} /><span className="rv-tab-text" data-text={group.label}>{group.label}</span></span></button>)}</div>
    <div className="ss-card">
      {activeGroup.items.length > 1 && <div className="ss-stabs"><span className="ss-seg rv-tabs rv-view-tabs" role="tablist" aria-label={`${activeGroup.label} bölümleri`}>{activeGroup.items.map(item => <button type="button" role="tab" key={item.key} aria-selected={section === item.key} className={section === item.key ? 'is-on is-active' : undefined} onClick={() => selectSection(item.key)}><span className="rv-tab-label"><SettingsIcon name={item.icon} />{item.short ? <><span className="rv-tab-text ss-lbl-long" data-text={item.label}>{item.label}</span><span className="rv-tab-text ss-lbl-short" data-text={item.short}>{item.short}</span></> : <span className="rv-tab-text" data-text={item.label}>{item.label}</span>}</span>{navMeta(item.key)}</button>)}</span></div>}
      <div ref={paneRef} className="ss-pane">
        {section === 'auth' && <>
          <SettingsPaneHead section="auth" description="İki adımlı doğrulama ile girişte telefonunuzdaki kod da istenir." />
          <div className="ss-body">{status.isLoading ? <LoadingState>Güvenlik durumu yükleniyor…</LoadingState> : status.isError || !status.data ? <div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Güvenlik durumu alınamadı.</span></div> : <>
            <div className="ss-auth">
              <span className="ss-tile is-lg"><SettingsIcon name="shield" /></span>
              <div className="ss-auth-main"><h3><span>Authenticator Uygulaması</span> {totpEnabled ? <span className="ss-tag is-ink"><SettingsGlyph state="done" />Etkin</span> : <span className="ss-tag"><SettingsGlyph state="none" />Kapalı</span>}</h3><p className="ss-auth-copy">{totpEnabled ? <>Girişte telefonunuzdaki uygulamadan kod istenir · Kalan kurtarma kodu <b>{status.data.recoveryCodesRemaining}</b></> : 'Girişte telefonunuzdaki doğrulama uygulamasından kod istenir.'}</p></div>
              <div className="ss-auth-side">{totpEnabled ? <span className="ss-tag is-ink is-lg" aria-label="Authenticator etkin"><SettingsGlyph state="done" />Hesabınız Korunuyor</span> : <button type="button" className="ss-btn is-primary" onClick={openMfa}><SettingsIcon name="shield" />Authenticator’ı Etkinleştir</button>}</div>
            </div>
          </>}</div>
        </>}
        {section === 'sessions' && <>
          <SettingsPaneHead section="sessions"actions={activeOtherSessions.length || closedSessions.length ? <>{activeOtherSessions.length > 0 && <button type="button" className="ss-btn is-danger" onClick={() => setSessionConfirmation({ action: 'revoke-others' })}><span className="ss-lbl-long">Diğer Tüm Oturumları Kapat</span><span className="ss-lbl-short">Diğerlerini Kapat</span> <span className="ss-count">{activeOtherSessions.length}</span></button>}{closedSessions.length > 0 && <button type="button" className="ss-btn" onClick={() => setSessionConfirmation({ action: 'delete-closed' })}><span className="ss-lbl-long">Kapalı Oturumları Sil</span><span className="ss-lbl-short">Kapalıları Sil</span> <span className="ss-count">{closedSessions.length}</span></button>}</> : undefined} />
          <div className="ss-body is-flush">{sessions.isLoading ? <div className="ss-pad"><LoadingState>Oturumlar yükleniyor…</LoadingState></div> : sessions.isError || !sessions.data ? <div className="ss-pad"><div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Oturumlar alınamadı.</span></div></div> : <div className="ss-sess">
            <div className="ss-sess-sub">Aktif <span className="ss-count">{activeSessions.length}</span></div>
            {activeSessions.map(sessionRow)}
            {closedSessions.length ? <><div className="ss-sess-sub">Kapalı <span className="ss-count">{closedSessions.length}</span></div>{closedSessions.map(sessionRow)}</> : null}
          </div>}</div>
        </>}
        {section === 'users' && <UserManagementSection mfa={totpEnabled} />}
        {section === 'alerts' && <AlertEmailSettingsSection />}
        {section === 'offsite' && <OffsiteBackupSection mfa={totpEnabled} />}
        {section === 'ai' && <AiSettingsSection />}
        {section === 'database' && <SettingsDataReset forbidden={resetForbidden} groups={resetGroups} counts={resetView.data ? Object.fromEntries(resetView.data.groups.map(entry => [entry.key, entry.count])) : undefined} countsFailed={resetView.isError} picked={resetScopes} onToggle={toggleResetScopes} marketplaces={resetMarketplaces} marketplacePicked={marketplacePicked} onToggleMarketplaces={toggleMarketplaces} progress={marketplaceProgress} confirmation={resetConfirmation} onConfirmation={setResetConfirmation} password={resetPassword} onPassword={setResetPassword} factor={resetFactor} onFactor={setResetFactor} mfa={totpEnabled} busy={resetBusy} onConfirm={() => void resetOperationalData()} />}
        <ShippingLabelSettingsPanel section={isShipping ? section as ShippingSection : null} settings={labelSettings} saved={savedLabelSettings} dirty={shippingDirty} saving={labelSaving} loadFailed={shippingSettings.isError} loading={shippingSettings.isPending} format={labelFormat} setFormat={setLabelFormat} onChange={shippingSettings.setSettings} onSave={() => void saveLabelSettings()} saveDenied={labelSaveDenied} conflict={shippingSettings.conflict} onAcceptServer={shippingSettings.acceptServer} onOverwrite={() => void saveLabelSettings(true)} />
        <AppearanceSettingsSection section={isAppearance ? section as AppearanceSection : null} onStatus={setAppearanceStatus} />
      </div>
    </div>

    <Modal open={mfaStep !== 'closed'} title="Authenticator Kurulumu" hint={mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın. Parolanız yalnız bu kurulum için doğrulanır; kaydedilmez.' : mfaStep === 'verify' ? 'QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın ve üretilen 6 haneli kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'} className="ss-modal is-lg ss-mfa" onClose={closeMfa} footer={mfaStep === 'password' ? <><span className="ss-modal-note">Adım 1 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => setMfaStep('closed')}>Vazgeç</button><button type="submit" form="ss-mfa-password" className="ss-btn is-primary" disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam Et'}</button></> : mfaStep === 'verify' ? <><span className="ss-modal-note">Adım 2 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => { setMfaError(''); setMfaStep('password') }}><SettingsIcon name="back" />Geri</button><button type="submit" form="ss-mfa-verify" className="ss-btn is-primary" disabled={busy}>{busy ? 'Kontrol Ediliyor…' : 'Etkinleştir'}</button></> : <><button type="button" className="ss-btn is-quiet" onClick={() => void copyRecoveryCodes()}><SettingsIcon name="copy" />Kopyala</button><span className="ss-modal-note" /><button type="button" className="ss-btn is-primary" onClick={() => { setMfaError(''); setMfaStep('closed') }}>Kodları Sakladım</button></>}>
      <div className="ss-mfa-split">
        <nav className="ss-mfa-rail" aria-label="Kurulum adımları">{mfaSteps.map((step, index) => <div key={step.key} className={`ss-mfa-step${index === mfaIndex ? ' is-on' : index < mfaIndex ? ' is-done' : ''}`} aria-current={index === mfaIndex ? 'step' : undefined}><SettingsGlyph state={index < mfaIndex ? 'done' : index === mfaIndex ? 'pending' : 'none'} /><span>{step.label}</span><small>{step.hint}</small></div>)}</nav>
        <div className="ss-mfa-pane">
          {mfaStep === 'password' && <form id="ss-mfa-password" className="ss-stack" onSubmit={prepareMfa}><label className="ss-fld"><small>Mevcut Parola</small><input className="ss-in" name="password" type="password" autoComplete="current-password" required data-autofocus placeholder="••••••••" /></label></form>}
          {mfaStep === 'verify' && setup && <form id="ss-mfa-verify" className="ss-qrrow" onSubmit={confirmMfa}><div className="ss-qr"><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /></div><div className="ss-qr-copy"><details className="ss-man"><summary>Kurulum Anahtarını Elle Göster</summary><code>{setup.secret ?? manualSetupKey(setup.otpauthUri)}</code></details><div className="ss-fld"><small>6 Haneli Doğrulama Kodu</small><OtpInput digits={otp} onChange={setOtp} /></div></div></form>}
          {mfaStep === 'recovery' && <><div className="ss-codes">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><div className="ss-callout is-warn"><SettingsIcon name="alert" /><span><b>Bu kodlar yalnızca bir kez gösterilir.</b> Her kod tek kullanımlıktır; telefonunuza erişemediğinizde giriş için kullanılır.</span></div></>}
          {mfaError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{mfaError}</span>}
        </div>
      </div>
    </Modal>

    {sessionConfirmationCopy && <Modal open title={sessionConfirmationCopy.title} description={sessionConfirmationCopy.description} className="ss-modal is-sm" onClose={() => { if (!sessionActionBusy) setSessionConfirmation(null) }} footer={<><button type="button" className="ss-btn" disabled={sessionActionBusy} onClick={() => setSessionConfirmation(null)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" disabled={sessionActionBusy} onClick={() => void confirmSessionAction()}>{sessionActionBusy ? 'İşlem Yapılıyor…' : sessionConfirmationCopy.confirm}</button></>}>
      <div className={`ss-callout${sessionConfirmation?.action.startsWith('delete') ? '' : ' is-warn'}`}><SettingsIcon name="alert" /><span><b>Bu işlem hesap oturumlarını etkiler.</b> Devam etmeden önce yapmak istediğiniz işlemi kontrol edin.</span></div>
      {affectedSessions.length ? <div className="ss-sum">{affectedSessions.map(session => <div key={session.id}><SettingsGlyph state={session.state === 'ACTIVE' ? 'done' : 'none'} /><span>Diğer Oturum<small>Son etkinlik {formatSettingsDate(session.lastSeenAt)}</small></span><span className="ss-sum-r">Bitiş {formatSettingsDate(session.expiresAt)}</span></div>)}</div> : null}
    </Modal>}

  </section>
}
