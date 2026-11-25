import { useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, ApiRequestError, type Me } from '../shared/api'
import { queryKeys } from '../shared/api/query-keys'
import { focusOnMount } from '../shared/focus'
import { readStoredItem } from '../shared/local-storage'
import { clearPersonalStorage } from '../shared/storage-scope'
import { clearQuerySnapshot, leaveForAnotherAccount, resumeQuerySnapshot } from '../shared/query-snapshot'
import { RrModal } from '../shared/ui/rr-components'
import './session-expired.css'

// Scan 25 Nov 2026 (D-7): the session ended while the panel was open (idle timeout, revoked elsewhere). The workspace
// stays where it is, behind this window: a half-filled form, an open editor and the lists keep what they hold in memory.
// Signing in again here, as the same person in the same workspace, closes the window and the screens read their data
// again; nothing that failed is sent again by itself (the person presses Kaydet once more). Another account signing in
// gets a fresh page, never this one's data. The password is sent once to the server and kept nowhere.
type SessionResumeResult = 'resumed' | 'other-account'

function sessionResumeResult(before: Pick<Me, 'id' | 'tenantId'>, after: Pick<Me, 'id' | 'tenantId'>): SessionResumeResult {
  return before.id === after.id && before.tenantId === after.tenantId ? 'resumed' : 'other-account'
}

function signInErrorMessage(reason: unknown, step: 'password' | 'code') {
  if (reason instanceof ApiRequestError && reason.code === 'MFA_ATTEMPTS_EXCEEDED') return 'Çok fazla hatalı kod girildi; güvenliğiniz için oturum kapatıldı. Parolanızla yeniden deneyin.'
  if (reason instanceof ApiRequestError && reason.status === 429) return 'Çok fazla deneme yapıldı. Bir dakika bekleyip yeniden deneyin.'
  if (reason instanceof ApiRequestError && reason.status === 401) return step === 'password' ? 'Parola hatalı. Art arda başarısız denemelerde bu ağdan giriş bir süre engellenir.' : 'Doğrulama kodu geçersiz veya süresi dolmuş.'
  if (reason instanceof ApiRequestError && reason.status === 400) return 'Güvenlik doğrulaması tamamlanamadı. Yeniden deneyin.'
  return 'Giriş servisine ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.'
}

export function SessionExpiredDialog({ me, onResumed }: { me: Me; onResumed: () => void }) {
  const client = useQueryClient()
  const [step, setStep] = useState<'password' | 'code'>('password')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [leaving, setLeaving] = useState(false)

  async function finish() {
    const current = await api<Me>('/me')
    if (current.state === 'MFA_CHALLENGE') { setStep('code'); return }
    if (current.state !== 'ACTIVE' || sessionResumeResult(me, current) === 'other-account') {
      // Another account or a further sign-in step: a fresh page, so nothing of this workspace is shown to it.
      leaveForAnotherAccount(client)
      return
    }
    client.setQueryData(queryKeys.me(), current)
    // The reload snapshot is written again for this session (it stopped when the session ended).
    resumeQuerySnapshot(client, current.tenantId, current.id)
    onResumed()
    // The screens read what the server holds again; a write that failed is not sent again by itself.
    void client.invalidateQueries({ predicate: query => query.queryKey[0] !== 'me', refetchType: 'active' })
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    setBusy(true); setError('')
    try {
      if (step === 'password') {
        await api<{ state: string }>('/login', { method: 'POST', body: JSON.stringify({ email: me.email, password: data.get('password'), tenantId: me.tenantId, rememberMe: readStoredItem('ravencia.rememberMe') !== '0' }) })
      } else {
        await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: data.get('code'), recoveryCode: null }) })
      }
      await finish()
    } catch (reason) {
      if (reason instanceof ApiRequestError && reason.code === 'MFA_ATTEMPTS_EXCEEDED') setStep('password')
      setError(signInErrorMessage(reason, step))
    } finally { setBusy(false) }
  }

  async function signOut() {
    setLeaving(true)
    try { await api('/logout', { method: 'POST' }) } catch { /* the redirect below leaves anyway */ } finally { clearPersonalStorage(); clearQuerySnapshot(); window.location.replace(`/?signedOut=${Date.now()}`) }
  }

  return <RrModal title="Oturum Doldu" crumb="Güvenli Erişim" size="sm" locked closeButton={false} role="alertdialog" modalClass="rv-session-expired" describedBy="session-expired-text" onClose={() => undefined}
    foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={busy || leaving} onClick={() => void signOut()}>{leaving ? 'Çıkılıyor…' : 'Çıkış Yap'}</button><button type="submit" form="session-expired-form" className="rr-btn rr-primary" disabled={busy || leaving}>{busy ? 'Doğrulanıyor…' : step === 'password' ? 'Giriş Yap' : 'Doğrula'}</button></>}>
    <form id="session-expired-form" className="rv-session-expired-form" onSubmit={event => void submit(event)}>
      <p id="session-expired-text">Oturum doldu, yeniden giriş yapın. Açık sayfanız ve kaydedilmemiş bilgileriniz bu pencerenin arkasında duruyor; giriş yapınca kaldığınız yerden devam edersiniz.</p>
      <label className="rr-fld"><small>E-Posta</small><input className="rr-in" type="email" name="email" value={me.email} readOnly autoComplete="username" /></label>
      {step === 'password'
        ? <label className="rr-fld"><small>Parola</small><input key="password" className="rr-in" type="password" name="password" required autoComplete="current-password" ref={focusOnMount} /></label>
        : <label className="rr-fld"><small>6 Haneli Doğrulama Kodu</small><input key="code" className="rr-in" name="code" inputMode="numeric" pattern="[0-9]{6}" minLength={6} maxLength={6} required autoComplete="one-time-code" ref={focusOnMount} /></label>}
      {error && <em className="rr-err" role="alert">{error}</em>}
    </form>
  </RrModal>
}
