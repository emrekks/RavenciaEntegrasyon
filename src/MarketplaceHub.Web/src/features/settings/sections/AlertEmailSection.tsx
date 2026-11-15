import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, settingsIfMatch } from '../../../shared/api'
import { FieldHelp, LoadingState } from '../../../shared/components'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { queryKeys } from '../../../shared/api/query-keys'
import { formatSettingsDate, SettingsIcon, SettingsGlyph, SettingsPaneHead, SettingsBar } from '../settings-ui'

type AlertEmailSettings = { senderAddress: string | null; recipients: string[]; appPasswordSaved: boolean; updatedAt: string | null; version: number }
const maximumAlertRecipients = 5

// Settings → Alarm Bildirimleri: the Gmail account that sends the server's and the panel's alerts (security plan Y1).
// The app password is write-only: the server never returns it, the form shows "Kayıtlı" and "Değiştir". The host
// keeps a root-only copy for alerts while the panel is down (deploy/scripts/alert-settings-sync.sh).
export function AlertEmailSettingsSection() {
  const client = useQueryClient()
  const query = useQuery({ queryKey: queryKeys.alertEmailSettings(), queryFn: ({ signal }) => hubApi<AlertEmailSettings>('/settings/alert-email', { signal }), retry: false })
  const saved = query.data
  const [sender, setSender] = useState('')
  const [recipients, setRecipients] = useState<string[]>([''])
  const [password, setPassword] = useState('')
  const [changingPassword, setChangingPassword] = useState(false)
  const [busy, setBusy] = useState<'save' | 'test' | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const [testResult, setTestResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [loadedVersion, setLoadedVersion] = useState<number | null>(null)
  // The form starts from the saved settings, and again after a save (a new version).
  if (saved && loadedVersion !== saved.version) {
    setLoadedVersion(saved.version)
    setSender(saved.senderAddress ?? '')
    setRecipients(saved.recipients.length ? saved.recipients : [''])
    setPassword('')
    setChangingPassword(!saved.appPasswordSaved)
  }

  const cleanRecipients = recipients.map(value => value.trim().toLowerCase()).filter(Boolean)
  const dirty = !!saved && (sender.trim().toLowerCase() !== (saved.senderAddress ?? '') || cleanRecipients.join(',') !== saved.recipients.join(',') || password.trim().length > 0)
  const configured = !!saved?.senderAddress && saved.recipients.length > 0 && saved.appPasswordSaved

  async function save() {
    if (!saved) return
    setBusy('save'); setNotice(null); setTestResult(null)
    try {
      const next = await hubApi<AlertEmailSettings>('/settings/alert-email', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ senderAddress: sender.trim(), recipients: cleanRecipients, appPassword: changingPassword ? password : null }) })
      client.setQueryData(queryKeys.alertEmailSettings(), next)
      setNotice({ kind: 'success', message: 'Alarm e-postası ayarları kaydedildi. Sunucu sonraki alarmda bu ayarları kullanır.' })
    } catch (reason) {
      setNotice({ kind: 'error', message: reason instanceof Error ? reason.message : 'Alarm e-postası ayarları kaydedilemedi.' })
    } finally { setBusy(null) }
  }
  async function sendTest() {
    setBusy('test'); setTestResult(null)
    try {
      const result = await hubApi<{ sent: boolean; recipients: string[] }>('/settings/alert-email/test', { method: 'POST' })
      setTestResult({ kind: 'done', message: `Test e-postası gönderildi: ${result.recipients.join(', ')}. Gelen kutusunu (ve spam klasörünü) kontrol edin.` })
    } catch (reason) {
      setTestResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Test e-postası gönderilemedi.' })
    } finally { setBusy(null) }
  }

  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const bar = <SettingsBar title="Alarm Bildirimleri" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || busy !== null} onClick={() => void save()}><SettingsIcon name="save" />{busy === 'save' ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
  </SettingsBar>

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="alerts" description="Sağlık izleme, yedek, deploy ve geri alma alarmları bu Gmail hesabından e-postayla gönderilir." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Alarm ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Alarm bildirimlerini yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Alarm ayarları alınamadı.</span></div> : <>
        <div className="ss-pair">
          <label className="ss-fld"><small>Gönderen Gmail Adresi</small><input className="ss-in" type="email" inputMode="email" autoComplete="off" spellCheck={false} value={sender} maxLength={254} placeholder="ravencia.alarm@gmail.com" onChange={event => setSender(event.target.value)} /></label>
          <div className="ss-fld"><small>Uygulama Şifresi<FieldHelp label="Uygulama Şifresi"><b>İki Adımlı Doğrulama</b> açıkken <a href="https://myaccount.google.com/apppasswords" target="_blank" rel="noreferrer">myaccount.google.com/apppasswords</a> sayfasında “Ravencia” adıyla şifre oluşturup 16 harfi yapıştırın. Google hesap şifrenizi girmeyin.</FieldHelp></small>{saved.appPasswordSaved && !changingPassword
            ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingPassword(true)}>Değiştir</button></div>
            : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="Uygulama Şifresi" value={password} maxLength={40} placeholder="abcd efgh ijkl mnop" onChange={event => setPassword(event.target.value)} />{saved.appPasswordSaved && <button type="button" className="ss-btn is-xs" onClick={() => { setPassword(''); setChangingPassword(false) }}>Vazgeç</button>}</div>}
          </div>
        </div>
        <div className="ss-fld" role="group" aria-label="Alıcılar"><small>Alıcılar</small>
          {recipients.map((recipient, index) => <div key={index} className="ss-alert-row">
            <input className="ss-in" type="email" inputMode="email" autoComplete="off" spellCheck={false} aria-label={`${index + 1}. Alıcı`} value={recipient} maxLength={254} placeholder="ornek@ravencia.com" onChange={event => setRecipients(current => current.map((value, position) => position === index ? event.target.value : value))} />
            {recipients.length > 1 && <button type="button" className="ss-btn is-xs is-quiet" aria-label={`${index + 1}. Alıcıyı Kaldır`} onClick={() => setRecipients(current => current.filter((_, position) => position !== index))}><SettingsIcon name="trash" /></button>}
          </div>)}
          {recipients.length < maximumAlertRecipients && <div><button type="button" className="ss-btn is-xs" onClick={() => setRecipients(current => [...current, ''])}><SettingsIcon name="plus" />Alıcı Ekle</button></div>}
        </div>
        <div className="ss-alert-row">
          <button type="button" className="ss-btn" disabled={!configured || dirty || busy !== null} onClick={() => void sendTest()}><SettingsIcon name="bell" />{busy === 'test' ? 'Gönderiliyor…' : 'Test E-postası Gönder'}</button>
          {testResult ? <span role="status" className="ss-help"><SettingsGlyph state={testResult.kind} />{testResult.message}</span>
            : <span className="ss-help">{dirty ? 'Test için önce değişiklikleri kaydedin.' : configured ? '' : 'Test için önce gönderen adresi, alıcıları ve uygulama şifresini kaydedin.'}</span>}
        </div>
      </>}
    </div>
    {saved && !forbidden ? bar : null}
  </>
}
