import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { useLocation } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, hubPoll, settingsIfMatch } from '../../../shared/api'
import { FieldHelp, LoadingState, Modal } from '../../../shared/components'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { copyText } from '../../../shared/copy-text'
import { formatBackupSize, googleCloudSteps, offsiteBackupDefaultFolder, offsiteBackupDirty, offsiteBackupDraft, offsiteBackupDraftError, offsiteBackupSaveBody, offsiteBackupState, offsiteCopyPending, offsiteSetupDone, offsiteSetupPlan, shortAgeKey, firstRunStartedAt, markFirstRun, clearFirstRun, type OffsiteBackupDraft, type OffsiteBackupView } from '../offsite-backup'
import { backupKeyFileName, decryptBackup, decryptedFileName, downloadText, generateBackupKeyPair, identityFromKeyFile, saveStream, type BackupKeyPair } from '../offsite-backup-crypto'
import { reauthBody } from '../user-management'
import { queryKeys } from '../../../shared/api/query-keys'
import { useServerDraft } from '../../../shared/hooks/useServerDraft'
import { formatSettingsDate, SettingsIcon, SettingsGlyph, SettingsPaneHead, SettingsBar, SettingsSwitch, mfaErrorMessage, ReauthFields } from '../settings-ui'

// Sistem Ayarları › Google Drive Yedeği (offsite-backup.ts, OffsiteBackupEndpoints): the worker sends the newest verified
// backup set, encrypted to the owner's age PUBLIC key, to the owner's Google Drive twice a day. The owner's own Google OAuth
// client connects it ("Google ile Bağlan"); the client secret and the Google token are write-only (never shown again).
// Saving, connecting and disconnecting take the password again (and the authenticator code when it is on). The key pair is
// made in the browser and a copy is opened in the browser (offsite-backup-crypto.ts): no extra program is needed.
type OffsiteConfirm = 'save' | 'connect' | 'disconnect' | 'setup'
type OffsiteForm = { values: OffsiteBackupDraft; changingSecret: boolean }
const offsiteConfirmCopy: Record<OffsiteConfirm, { title: string; hint: string; action: string; busy: string; danger?: boolean }> = {
  save: { title: 'Yedek Ayarlarını Kaydet', hint: 'Yedeğin nereye ve hangi anahtarla gönderildiği değişir; parolanızla onaylayın.', action: 'Kaydet', busy: 'Kaydediliyor…' },
  setup: { title: 'Google Drive’a Bağlan', hint: 'Parolanızı onaylayın: ayarlar kaydedilir ve Google’ın izin sayfası açılır. Parola yalnız bir kez sorulur.', action: 'Kaydet ve Google’a Git', busy: 'Hazırlanıyor…' },
  connect: { title: 'Google ile Bağlan', hint: 'Parolanızı onaylayınca Google’ın izin sayfası açılır; yedeğin gideceği Google hesabıyla giriş yapıp izin verin.', action: 'Google’a Git', busy: 'Hazırlanıyor…' },
  disconnect: { title: 'Bağlantıyı Kes', hint: 'Google yetkisi Google’da iptal edilir ve panelden silinir; yedek, yeniden bağlanana kadar gönderilmez. Drive’daki kopyalar silinmez.', action: 'Bağlantıyı Kes', busy: 'Kesiliyor…', danger: true }
}

export function OffsiteBackupSection({ mfa }: { mfa: boolean }) {
  const client = useQueryClient()
  const location = useLocation()
  const [askedAt, setAskedAt] = useState<number | null>(null)
  const query = useQuery({
    queryKey: queryKeys.offsiteBackup(),
    queryFn: ({ signal }) => hubPoll<OffsiteBackupView>('/settings/offsite-backup', { signal }),
    retry: false,
    // While a copy is running or asked for, the status follows it every 5 s, and it refreshes when the tab gets focus again.
    refetchInterval: current => offsiteCopyPending(current.state.data, askedAt) ? 5_000 : false,
    refetchOnWindowFocus: 'always'
  })
  const saved = query.data
  // The form starts from the saved settings, and again for every new version (a save here or elsewhere); the status
  // refetches of the same version (every 5 s while a copy runs) keep it.
  const form = useServerDraft<OffsiteBackupView, OffsiteForm | null>({
    server: saved ? { value: saved, version: saved.version } : null,
    onServerChange: 'replace',
    toDraft: view => ({ values: offsiteBackupDraft(view), changingSecret: !view.clientSecretSaved }),
    initialDraft: () => null,
    isDirty: (current, view) => !!current && offsiteBackupDirty(view, current.values)
  })
  const draft = form.draft?.values ?? null
  const changingSecret = form.draft?.changingSecret ?? false
  const setChangingSecret = (value: boolean) => form.setDraft(current => current ? { ...current, changingSecret: value } : current)
  const [busy, setBusy] = useState<OffsiteConfirm | 'test' | 'run' | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(() => (location.state as { offsiteNotice?: SettingsNoticeState } | null)?.offsiteNotice ?? null)
  const [actionResult, setActionResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [confirm, setConfirm] = useState<OffsiteConfirm | null>(null)
  const [password, setPassword] = useState('')
  const [factor, setFactor] = useState('')
  const [confirmError, setConfirmError] = useState('')
  const [keyPair, setKeyPair] = useState<BackupKeyPair | null>(null)
  const [keyOpen, setKeyOpen] = useState(false)
  const [keyError, setKeyError] = useState('')
  const [keyDownloaded, setKeyDownloaded] = useState(false)
  const [keyStored, setKeyStored] = useState(false)
  const [setupKey, setSetupKey] = useState<BackupKeyPair | null>(null)
  const [setupError, setSetupError] = useState('')
  const [restoreOpen, setRestoreOpen] = useState(false)
  const [openRows, setOpenRows] = useState<string[]>([])
  const toggleRow = (id: string) => setOpenRows(rows => rows.includes(id) ? rows.filter(row => row !== id) : [...rows, id])
  const [pasteKey, setPasteKey] = useState(false)

  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const dirty = form.dirty
  const draftError = draft && saved ? offsiteBackupDraftError(draft, saved.clientSecretSaved && !changingSecret) : null
  const state = saved ? offsiteBackupState(saved) : null
  const update = (patch: Partial<OffsiteBackupDraft>) => form.setDraft(current => current ? { ...current, values: { ...current.values, ...patch } } : current)

  function openConfirm(kind: OffsiteConfirm) {
    setPassword(''); setFactor(''); setConfirmError(''); setActionResult(null); setConfirm(kind)
  }
  // The one button: key (made here, file downloaded) -> one password -> save with the defaults -> Google's consent page.
  async function startSetup() {
    if (!saved || !draft) return
    const plan = offsiteSetupPlan(saved, draft, saved.clientSecretSaved && !changingSecret)
    setSetupError('')
    if (plan.error) { setSetupError(plan.error); return }
    if (plan.needsKey) {
      try {
        const pair = await generateBackupKeyPair()
        downloadText(pair.file, backupKeyFileName)
        setSetupKey(pair)
      } catch { setSetupError('Bu tarayıcı anahtar üretemedi; güncel bir Chrome, Edge, Firefox ya da Safari deneyin.'); return }
    } else setSetupKey(null)
    setKeyStored(false)
    openConfirm('setup')
  }
  async function submitConfirm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!confirm || !saved || !draft) return
    const reauth = reauthBody(password, factor)
    setBusy(confirm); setConfirmError('')
    try {
      if (confirm === 'save') {
        const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ ...offsiteBackupSaveBody(draft, changingSecret), ...reauth }) })
        client.setQueryData(queryKeys.offsiteBackup(), next)
        setNotice({ kind: 'success', message: next.connected ? 'Google Drive yedeği ayarları kaydedildi.' : 'Ayarlar kaydedildi. Şimdi “Google ile Bağlan” ile Drive hesabınızı bağlayın.' })
      } else if (confirm === 'setup') {
        if (setupKey && !keyStored) return
        const plan = offsiteSetupPlan(saved, draft, saved.clientSecretSaved && !changingSecret)
        if (plan.needsSave) {
          const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ ...offsiteBackupSaveBody({ ...draft, enabled: true, ageRecipient: setupKey?.recipient ?? draft.ageRecipient }, changingSecret), ...reauth }) })
          client.setQueryData(queryKeys.offsiteBackup(), next)
        }
        const { url } = await hubApi<ApiAnswer<'postSettingsOffsiteBackupGoogleAuthorize'>>('/settings/offsite-backup/google/authorize', { method: 'POST', body: JSON.stringify(reauth) })
        markFirstRun()
        window.location.assign(url)
        return
      } else if (confirm === 'connect') {
        const { url } = await hubApi<ApiAnswer<'postSettingsOffsiteBackupGoogleAuthorize'>>('/settings/offsite-backup/google/authorize', { method: 'POST', body: JSON.stringify(reauth) })
        window.location.assign(url)
        return
      } else {
        const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup/google/disconnect', { method: 'POST', body: JSON.stringify(reauth) })
        client.setQueryData(queryKeys.offsiteBackup(), next)
        setNotice({ kind: 'success', message: 'Google Drive bağlantısı kesildi ve yetki iptal edildi.' })
      }
      setConfirm(null)
    } catch (reason) {
      setConfirmError(mfaErrorMessage(reason, 'İşlem tamamlanamadı.'))
    } finally { setBusy(null) }
  }
  async function testConnection() {
    setBusy('test'); setActionResult(null)
    try {
      await hubApi<ApiAnswer<'postSettingsOffsiteBackupTest'>>('/settings/offsite-backup/test', { method: 'POST' })
      setActionResult({ kind: 'done', message: 'Bağlantı çalışıyor: Google yetkisi geçerli ve Drive klasörü hazır.' })
    } catch (reason) {
      setActionResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Bağlantı denenemedi.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: queryKeys.offsiteBackup() })
    }
  }
  async function sendNow() {
    setBusy('run'); setActionResult(null)
    try {
      await hubApi('/settings/offsite-backup/run', { method: 'POST' })
      setAskedAt(Date.now())
      setActionResult({ kind: 'done', message: 'Gönderim sıraya alındı; bir dakika içinde başlar. Durum burada güncellenir.' })
    } catch (reason) {
      setActionResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Gönderim başlatılamadı.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: queryKeys.offsiteBackup() })
    }
  }
  async function openKeyMaker() {
    setKeyOpen(true); setKeyPair(null); setKeyError(''); setKeyDownloaded(false); setKeyStored(false)
    try { setKeyPair(await generateBackupKeyPair()) } catch { setKeyError('Bu tarayıcı anahtar üretemedi; güncel bir Chrome, Edge, Firefox ya da Safari deneyin.') }
  }
  function closeKeyMaker() {
    // The private key lives only in this window and in the downloaded file.
    setKeyOpen(false); setKeyPair(null)
  }

  const setupDone = !!saved && offsiteSetupDone(saved)
  // After the one-button setup: the first copy was asked for on return; say so when it has reached Drive.
  const firstSuccessAt = saved?.lastSuccessAt ?? null
  useEffect(() => {
    const started = firstRunStartedAt()
    if (started === null || !firstSuccessAt || Date.parse(firstSuccessAt) < started - 5_000) return
    clearFirstRun()
    setNotice({ kind: 'success', message: 'İlk yedek Drive’a gönderildi.' })
  }, [firstSuccessAt])
  const clientDone = !!saved && !!saved.clientId && saved.clientSecretSaved
  const keyDone = !!saved && !!saved.ageRecipient
  const factsList = saved && <dl className="ss-ob-facts">
    <div><dt>Son Gönderim</dt><dd>{saved.lastSuccessAt ? formatSettingsDate(saved.lastSuccessAt) : '—'}</dd></div>
    <div><dt>Set ve Boyut</dt><dd title={saved.lastSet ?? undefined}>{saved.lastSet ? formatBackupSize(saved.lastSize) : '—'}{saved.lastSet && <span className="ss-ob-set"> · {saved.lastSet}</span>}</dd></div>
    <div><dt>Sonraki Gönderim</dt><dd>{saved.runningSince ? 'Şimdi gönderiliyor' : saved.nextRunAt ? formatSettingsDate(saved.nextRunAt) : '—'}</dd></div>
    <div><dt>Drive’daki Kopya</dt><dd>{saved.remoteCopies ?? '—'}</dd></div>
  </dl>
  const clientFields = saved && draft && <>
    <details className="ss-ob-how"><summary>Nasıl Alınır?</summary>
      <ol className="ss-ob-steps">{googleCloudSteps.map(step => <li key={step}>{step}</li>)}</ol>
      <div className="ss-fld"><small>Yönlendirme Adresi</small><div className="ss-alert-row"><input className="ss-in" readOnly value={saved.redirectUri} aria-label="Yönlendirme Adresi" onFocus={event => event.currentTarget.select()} /><button type="button" className="ss-btn" onClick={() => void copyText(saved.redirectUri).then(() => setActionResult({ kind: 'done', message: 'Yönlendirme adresi kopyalandı.' }))}><SettingsIcon name="copy" />Kopyala</button></div><span className="ss-help">İzinler: {saved.scopes}</span></div>
    </details>
    <div className="ss-pair">
      <label className="ss-fld"><small>İstemci Kimliği</small><input className="ss-in" autoComplete="off" spellCheck={false} value={draft.clientId} maxLength={120} placeholder="…apps.googleusercontent.com" onChange={event => update({ clientId: event.target.value })} /></label>
      <div className="ss-fld"><small>İstemci Gizli Anahtarı</small>{saved.clientSecretSaved && !changingSecret
        ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingSecret(true)}>Değiştir</button></div>
        : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="İstemci Gizli Anahtarı" value={draft.clientSecret} maxLength={100} placeholder="GOCSPX-…" onChange={event => update({ clientSecret: event.target.value })} />{saved.clientSecretSaved && <button type="button" className="ss-btn is-xs" onClick={() => { update({ clientSecret: '' }); setChangingSecret(false) }}>Vazgeç</button>}</div>}
      </div>
    </div>
  </>
  const keyFields = saved && draft && <div className="ss-fld">
    <div className="ss-ob-inline">
      {(pasteKey || !!draft.ageRecipient) && <input className="ss-in" autoComplete="off" spellCheck={false} aria-label="age Açık Anahtarı" value={draft.ageRecipient} maxLength={80} placeholder="age1…" onChange={event => update({ ageRecipient: event.target.value })} />}
      <button type="button" className={`ss-btn${!keyDone && clientDone ? ' is-primary' : ''}`} onClick={() => void openKeyMaker()}><SettingsIcon name="lock" />Şifreleme Anahtarı Oluştur</button>
      {!pasteKey && !draft.ageRecipient && <button type="button" className="ss-link" onClick={() => setPasteKey(true)}>Elimde Anahtar Var</button>}
    </div>
    <span className="ss-help">{saved.ageRecipient && draft.ageRecipient.trim() !== saved.ageRecipient ? 'Anahtar değişirse yeni kopyalar yeni anahtarla açılır; eski anahtar dosyanızı saklayın.' : 'Özel anahtar yalnız size dosya olarak iner; kaybolursa yedekler açılamaz.'}</span>
  </div>
  const folderFields = saved && draft && <>
    <div className="ss-fld"><small>Drive Klasörü<FieldHelp label="Drive Klasörü">{draft.folderMode === 'NAME' ? 'Uygulama Drive’ınızın ana dizininde bu adla kendi klasörünü oluşturur ve kimliğini saklar.' : 'Yalnız uygulamanın daha önce oluşturduğu bir klasörün ID’si çalışır: Drive izni (drive.file) başka bir uygulamanın ya da elle oluşturulan klasörü göremez.'}{saved.folderId ? ` Kullanılan klasör ID’si: ${saved.folderId}` : ''}</FieldHelp></small>
      <div className="ss-ob-folder">
        <div className="ss-seg3 is-two" role="group" aria-label="Klasör Seçimi">
          <button type="button" aria-pressed={draft.folderMode === 'NAME'} className={draft.folderMode === 'NAME' ? 'is-on' : undefined} onClick={() => update({ folderMode: 'NAME', folderValue: saved.folderMode === 'NAME' ? saved.folderValue : offsiteBackupDefaultFolder })}>Uygulama Klasörü</button>
          <button type="button" aria-pressed={draft.folderMode === 'ID'} className={draft.folderMode === 'ID' ? 'is-on' : undefined} onClick={() => update({ folderMode: 'ID', folderValue: saved.folderMode === 'ID' ? saved.folderValue : saved.folderId ?? '' })}>Klasör ID’si</button>
        </div>
        <input className="ss-in" autoComplete="off" spellCheck={false} aria-label={draft.folderMode === 'NAME' ? 'Klasör Adı' : 'Klasör ID’si'} value={draft.folderValue} maxLength={draft.folderMode === 'NAME' ? 80 : 200} placeholder={draft.folderMode === 'NAME' ? offsiteBackupDefaultFolder : '1AbC…'} onChange={event => update({ folderValue: event.target.value })} />
      </div>
    </div>
    <div className="ss-pair">
      <label className="ss-fld"><small>Günlük Saklama (Gün)<FieldHelp label="Günlük Saklama">Bu kadar günün bütün kopyaları Drive’da kalır; daha eskileri silinir (her ayın ilk kopyası hariç).</FieldHelp></small><input className="ss-in" type="number" inputMode="numeric" min={3} max={365} value={draft.keepDays} onChange={event => update({ keepDays: event.target.value })} /></label>
      <label className="ss-fld"><small>Aylık Saklama (Ay)<FieldHelp label="Aylık Saklama">Son bu kadar ayın her birinin ilk kopyası ayrıca saklanır. 0: aylık kopya tutulmaz. Uygulama yalnız kendi yüklediği yedek dosyalarını siler.</FieldHelp></small><input className="ss-in" type="number" inputMode="numeric" min={0} max={120} value={draft.keepMonths} onChange={event => update({ keepMonths: event.target.value })} /></label>
    </div>
  </>
  const summaryRow = (id: 'client' | 'key' | 'folder', label: string, value: ReactNode, body: ReactNode) => <div className="ss-ob-row">
    <div className="ss-ob-row-line"><b>{label}</b><span aria-hidden="true">·</span><span className="ss-ob-row-value">{value}</span><button type="button" className="ss-btn is-xs" aria-label={`${label} ${openRows.includes(id) ? 'Gizle' : 'Değiştir'}`} aria-expanded={openRows.includes(id)} onClick={() => toggleRow(id)}>{openRows.includes(id) ? 'Gizle' : 'Değiştir'}</button></div>
    {openRows.includes(id) && <div className="ss-ob-row-body">{body}</div>}
  </div>
 const ready = !!saved && saved.connected && !saved.authorizationInvalid && !!saved.ageRecipient
  const bar = <SettingsBar title="Google Drive Yedeği" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || !!draftError || busy !== null} title={draftError ?? undefined} onClick={() => openConfirm('save')}><SettingsIcon name="save" />Ayarları Kaydet</button>
  </SettingsBar>
  const copy = confirm ? offsiteConfirmCopy[confirm] : null

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="offsite" description="Sunucudaki doğrulanmış yedek, şifreleme anahtarınızla şifrelenip günde iki kez (12:30 ve 00:30) Google Drive’ınıza gönderilir. Özel anahtar sunucuya hiç girmez; yedeği yalnız siz açabilirsiniz." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Yedek ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Google Drive yedeğini yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved || !draft || !state ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Yedek ayarları alınamadı.</span></div> : <>
        {setupDone ? <>
          <section className="ss-ob-status" aria-label="Yedek Durumu">
            <div className="ss-ob-state"><span className="ss-tile"><SettingsIcon name="cloud" /></span><span className={`ss-tag is-lg${state.tone === 'done' ? ' is-ink' : ''}`}><SettingsGlyph state={state.tone} />{state.label}</span><span className="ss-ob-account">{saved.account ?? 'Google hesabı'}{saved.connectedAt ? ` · bağlandı ${formatSettingsDate(saved.connectedAt)}` : ''}</span></div>
            {saved.authorizationInvalid && <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span><b>Google yetkisi geçersiz; yedek gönderilmiyor.</b> {saved.authorizationError ?? ''} “Google ile Yeniden Bağlan”a basın.</span></div>}
            {factsList}
            {saved.lastError && saved.consecutiveFailures > 0 && <div className="ss-callout is-danger"><SettingsIcon name="alert" /><span><b>Son hata{saved.lastErrorAt ? ` (${formatSettingsDate(saved.lastErrorAt)})` : ''}:</b> {saved.lastError}{saved.consecutiveFailures > 1 ? ` · ${saved.consecutiveFailures} deneme` : ''}</span></div>}
            <div className="ss-alert-row">
              <button type="button" className={`ss-btn${saved.authorizationInvalid ? '' : ' is-primary'}`} disabled={busy !== null || dirty || !ready || !saved.enabled} title={!saved.enabled ? 'Önce otomatik gönderimi açıp kaydedin.' : undefined} onClick={() => void sendNow()}><SettingsIcon name="cloud" />{busy === 'run' ? 'İsteniyor…' : 'Şimdi Gönder'}</button>
              <button type="button" className="ss-btn" disabled={busy !== null} onClick={() => void testConnection()}><SettingsIcon name="info" />{busy === 'test' ? 'Deneniyor…' : 'Bağlantıyı Test Et'}</button>
              <button type="button" className="ss-btn" onClick={() => setRestoreOpen(true)}><SettingsIcon name="lock" />Yedeği Aç</button>
              <button type="button" className={`ss-btn${saved.authorizationInvalid ? ' is-primary' : ''}`} disabled={busy !== null || dirty} onClick={() => openConfirm('connect')}><SettingsIcon name="reset" />{saved.authorizationInvalid ? 'Google ile Yeniden Bağlan' : 'Yeniden Bağlan'}</button>
              <button type="button" className="ss-btn is-danger" disabled={busy !== null} onClick={() => openConfirm('disconnect')}><SettingsIcon name="logout" />Bağlantıyı Kes</button>
            </div>
            {actionResult ? <span role="status" className="ss-help ss-ob-result"><SettingsGlyph state={actionResult.kind} />{actionResult.message}</span>
              : dirty ? <span className="ss-help">Göndermek için önce değişiklikleri kaydedin.</span> : null}
          </section>
          <section className="ss-ob-rows" aria-label="Yedek Ayarları">
            <SettingsSwitch label="Otomatik Gönderim" checked={draft.enabled} onChange={enabled => update({ enabled })} />
            {summaryRow('key', 'Şifreleme Anahtarı', <><span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span><code>{shortAgeKey(saved.ageRecipient)}</code></>, keyFields)}
            {summaryRow('client', 'Google İstemcisi', <span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span>, clientFields)}
            {summaryRow('folder', 'Klasör ve Saklama', `${draft.folderMode === 'NAME' ? (draft.folderValue.trim() || offsiteBackupDefaultFolder) : 'Klasör ID’si'} · ${draft.keepDays} gün / ${draft.keepMonths} ay`, folderFields)}
            {draftError && dirty && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{draftError}</span>}
          </section>
        </> : <>
          <div className="ss-ob-wizard">
            <section className="ss-ob-step" aria-label="Google Drive’a Bağlan">
              <div className="ss-ob-step-head"><span className="ss-tile is-sm"><SettingsIcon name="cloud" /></span><h3>Google Drive’a Bağlan</h3></div>
              <div className="ss-ob-step-body">
                {clientDone ? summaryRow('client', 'Google İstemcisi', <span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span>, clientFields) : clientFields}
                <div className="ss-alert-row"><button type="button" className="ss-btn is-primary" disabled={busy !== null} onClick={() => void startSetup()}><SettingsIcon name="cloud" />Google Drive’a Bağlan</button></div>
                <span className="ss-help">{keyDone || draft.ageRecipient.trim() ? 'Parolanızı onaylayınca ayarlar kaydedilir ve Google’ın izin sayfası açılır.' : 'Şifreleme anahtarı bu tarayıcıda üretilir ve özel anahtar dosyası inerken parolanız bir kez sorulur; ardından Google izin sayfası açılır.'}</span>
                {setupError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{setupError}</span>}
              </div>
            </section>
            {draftError && dirty && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{draftError}</span>}
            <details className="ss-ob-adv"><summary>Gelişmiş Ayarlar</summary>
              <div className="ss-ob-adv-body">
                <div className="ss-fld"><small>Kendi Açık Anahtarınız (İsteğe Bağlı)</small>
                  <input className="ss-in" autoComplete="off" spellCheck={false} aria-label="age Açık Anahtarı" value={draft.ageRecipient} maxLength={80} placeholder="age1…" onChange={event => update({ ageRecipient: event.target.value })} />
                  <span className="ss-help">Boş bırakırsanız anahtar çifti sizin için üretilir. Elinizde bir age açık anahtarı varsa buraya yapıştırın.</span>
                </div>
                {folderFields}
              </div>
            </details>
            <div><button type="button" className="ss-btn" onClick={() => setRestoreOpen(true)}><SettingsIcon name="lock" />Yedeği Aç</button></div>
          </div>
        </>}
      </>}
    </div>
    {saved && !forbidden && dirty && setupDone ? bar : null}
    <Modal open={confirm !== null} title={copy?.title ?? ''} hint={copy?.hint} className="ss-modal is-sm" onClose={() => { if (busy === null) { setConfirm(null); setSetupKey(null) } }} footer={<><button type="button" className="ss-btn" disabled={busy !== null} onClick={() => { setConfirm(null); setSetupKey(null) }}>Vazgeç</button><button type="submit" form="ss-offsite-confirm" className={`ss-btn ${copy?.danger ? 'is-danger is-solid' : 'is-primary'}`} disabled={busy !== null || !password || (confirm === 'setup' && !!setupKey && !keyStored)}>{busy === confirm ? copy?.busy : copy?.action}</button></>}>
      <form id="ss-offsite-confirm" className="ss-stack" onSubmit={submitConfirm}>
        {confirm === 'setup' && setupKey && <>
          <div className="ss-callout is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Özel anahtar dosyası indirildi ({backupKeyFileName}).</b> Güvenli bir yerde saklayın; kaybolursa Drive’daki yedekler açılamaz, panel ve sunucu bir kopyasını tutmaz.</span></div>
          <div className="ss-alert-row"><button type="button" className="ss-btn" onClick={() => downloadText(setupKey.file, backupKeyFileName)}><SettingsIcon name="save" />Dosyayı Yeniden İndir</button></div>
          <label className="ss-ob-check"><input type="checkbox" checked={keyStored} onChange={event => setKeyStored(event.target.checked)} /><span>Dosyayı indirdim ve sakladım.</span></label>
        </>}
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {confirmError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{confirmError}</span>}
      </form>
    </Modal>
    <Modal open={keyOpen} title="Şifreleme Anahtarı Oluştur" hint="Anahtar çifti bu tarayıcıda üretildi. Sunucuya yalnız açık anahtar gider." className="ss-modal is-lg" onClose={closeKeyMaker} footer={<><button type="button" className="ss-btn" onClick={closeKeyMaker}>Vazgeç</button><button type="button" className="ss-btn is-primary" disabled={!keyPair || !keyDownloaded || !keyStored} onClick={() => { if (keyPair) update(setupDone ? { ageRecipient: keyPair.recipient } : { ageRecipient: keyPair.recipient, enabled: true }); closeKeyMaker(); setNotice({ kind: 'success', message: 'Yeni açık anahtar forma yazıldı. Kullanmak için Ayarları Kaydet’e basın.' }) }}>Açık Anahtarı Kullan</button></>}>
      <div className="ss-stack">
        {keyError ? <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{keyError}</span> : !keyPair ? <LoadingState>Anahtar üretiliyor…</LoadingState> : <>
          <div className="ss-callout is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Özel anahtar dosyasını indirip güvenli bir yerde saklayın.</b> Kaybolursa Drive’daki yedekler hiçbir şekilde açılamaz; panel ve sunucu bu dosyanın kopyasını tutmaz. Parola yöneticinize ve ayrıca çevrimdışı bir yere (USB) koyun, kimseyle paylaşmayın.</span></div>
          <label className="ss-fld"><small>Açık Anahtar</small><input className="ss-in" readOnly value={keyPair.recipient} onFocus={event => event.currentTarget.select()} /></label>
          <div className="ss-alert-row"><button type="button" className={`ss-btn${keyDownloaded ? '' : ' is-primary'}`} onClick={() => { downloadText(keyPair.file, backupKeyFileName); setKeyDownloaded(true) }}><SettingsIcon name="save" />{keyDownloaded ? 'Yeniden İndir' : 'Özel Anahtarı İndir'}</button><span className="ss-help">{keyDownloaded ? <><SettingsGlyph state="done" />{backupKeyFileName} indirildi.</> : backupKeyFileName}</span></div>
          <label className="ss-ob-check"><input type="checkbox" checked={keyStored} disabled={!keyDownloaded} onChange={event => setKeyStored(event.target.checked)} /><span>Özel anahtar dosyasını indirdim ve güvenli bir yere kaydettim.</span></label>
        </>}
      </div>
    </Modal>
    {restoreOpen && <OffsiteRestoreModal onClose={() => setRestoreOpen(false)} />}
  </>
}

// Yedeği Aç: the copy and the key file are read and decrypted in this browser only.
function OffsiteRestoreModal({ onClose }: { onClose: () => void }) {
  const [backup, setBackup] = useState<File | null>(null)
  const [keyFile, setKeyFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  async function open() {
    if (!backup || !keyFile) return
    setBusy(true); setResult(null)
    try {
      const identity = identityFromKeyFile(await keyFile.text())
      if (!identity) { setResult({ kind: 'bad', message: 'Anahtar dosyasında AGE-SECRET-KEY-1 ile başlayan özel anahtar yok.' }); return }
      let stream: ReadableStream<Uint8Array>
      try { stream = await decryptBackup(backup, identity) } catch { setResult({ kind: 'bad', message: 'Yedek bu anahtarla açılamadı: anahtar bu yedeğe ait değil ya da dosya bozuk.' }); return }
      await saveStream(stream, decryptedFileName(backup.name))
      setResult({ kind: 'done', message: `${decryptedFileName(backup.name)} kaydedildi. İçinden database.dump, private-volumes.tar.gz ve SHA256SUMS çıkar.` })
    } catch (reason) {
      if (reason instanceof DOMException && reason.name === 'AbortError') return
      setResult({ kind: 'bad', message: 'Yedek açılamadı: dosya eksik ya da bozuk olabilir.' })
    } finally { setBusy(false) }
  }
  return <Modal open title="Yedeği Aç" hint="Seçtiğiniz dosyalar yalnız bu tarayıcıda okunur; sunucuya hiçbir şey gönderilmez." className="ss-modal is-lg" onClose={() => { if (!busy) onClose() }} footer={<><button type="button" className="ss-btn" disabled={busy} onClick={onClose}>Kapat</button><button type="button" className="ss-btn is-primary" disabled={busy || !backup || !keyFile} onClick={() => void open()}>{busy ? 'Açılıyor…' : 'Çöz ve Kaydet'}</button></>}>
    <div className="ss-stack">
      <label className="ss-fld"><small>Yedek Dosyası (.tar.age)</small><input className="ss-in" type="file" accept=".age" onChange={event => { setBackup(event.target.files?.[0] ?? null); setResult(null) }} /></label>
      <label className="ss-fld"><small>Özel Anahtar Dosyası</small><input className="ss-in" type="file" accept=".txt,text/plain" onChange={event => { setKeyFile(event.target.files?.[0] ?? null); setResult(null) }} /></label>
      {result && <span role="status" className={result.kind === 'bad' ? 'ss-ferr' : 'ss-help'}><SettingsGlyph state={result.kind} />{result.message}</span>}
    </div>
  </Modal>
}
