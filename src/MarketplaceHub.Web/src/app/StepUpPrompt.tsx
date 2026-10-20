import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { api, ApiRequestError, onUnauthorized, setStepUpHandler } from '../shared/api'
import { Button, Callout, Modal, TextField } from '../shared/components'

type SecurityStatus = { totpState: string }
// 'unknown': the status could not be read. The code field is shown (optional) and an empty one makes the window read
// the status again before sending: a password sent alone to an account with an authenticator would cost one of the
// session's five code attempts.
type FactorState = 'loading' | 'off' | 'on' | 'unknown'

// Irreversible work (emptying data, deleting a connection, changing its credentials) answers 403 REAUTHENTICATION_REQUIRED
// when the last sign-in or re-authentication is older than five minutes. This window asks for the password, and the
// authenticator code when one is on, and posts them to /auth/reauthenticate; after that succeeds the client sends the
// refused request once more. It is rendered into <body> so it sits over the confirmation window that asked for it.
export function StepUpPrompt() {
  const [open, setOpen] = useState(false)
  const [factor, setFactor] = useState<FactorState>('loading')
  const [useRecoveryCode, setUseRecoveryCode] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const pending = useRef<((value: boolean) => void) | null>(null)
  const formId = useId()

  useEffect(() => setStepUpHandler(() => new Promise<boolean>(resolve => {
    pending.current?.(false)
    pending.current = resolve
    setError(''); setUseRecoveryCode(false); setFactor('loading'); setOpen(true)
    void readFactor().then(setFactor)
  })), [])

  // A prompt left open when the app unmounts (sign-out) gives up instead of leaving the request hanging.
  useEffect(() => () => { pending.current?.(false); pending.current = null }, [])

  function settle(value: boolean) {
    pending.current?.(value)
    pending.current = null
    setOpen(false)
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (factor === 'loading') return
    const data = new FormData(event.currentTarget)
    const secondFactor = String(data.get('factor') ?? '').trim()
    let known: FactorState = factor
    if (known === 'unknown' && secondFactor.length === 0) {
      setBusy(true); setError('')
      known = await readFactor()
      setBusy(false)
      setFactor(known)
      if (known === 'unknown') { setError('Güvenlik durumu okunamadı. Doğrulayıcınız açıksa kodu da girin ya da biraz sonra yeniden deneyin.'); return }
      if (known === 'on') { setError('Doğrulayıcı kodunu da girin.'); return }
    }
    const withFactor = (known === 'on' || known === 'unknown') && secondFactor.length > 0
    setBusy(true); setError('')
    try {
      await api('/reauthenticate', { method: 'POST', body: JSON.stringify({ password: data.get('password'), code: withFactor && !useRecoveryCode ? secondFactor : null, recoveryCode: withFactor && useRecoveryCode ? secondFactor : null }) })
      settle(true)
    } catch (reason) {
      setError(stepUpErrorMessage(reason))
      if (reason instanceof ApiRequestError && reason.code === 'SECOND_FACTOR_FAILED' && factor !== 'on') setFactor('on')
      // The session ended (idle, revoked) or the server closed it after too many wrong codes: the sign-in screen takes over.
      if (reason instanceof ApiRequestError && (reason.code === 'MFA_ATTEMPTS_EXCEEDED' || reason.code === 'AUTHENTICATION_REQUIRED' || reason.code === 'SESSION_STATE_FORBIDDEN')) { settle(false); onUnauthorized() }
    } finally {
      setBusy(false)
    }
  }

  if (!open) return null
  return createPortal(<Modal open title="Kimliğinizi doğrulayın" description="Bu işlem geri alınamaz; devam etmek için parolanızı yeniden girin." className="rv-step-up" onClose={() => { if (!busy) settle(false) }} footer={<>
    <Button variant="secondary" onClick={() => settle(false)} disabled={busy}>Vazgeç</Button>
    <Button type="submit" form={formId} loading={busy} disabled={factor === 'loading'}>Doğrula ve devam et</Button>
  </>}>
    <form id={formId} className="rv-step-up-form" onSubmit={submit}>
      <TextField label="Parola" name="password" type="password" autoComplete="current-password" required data-autofocus />
      {factor === 'on' && <TextField key={useRecoveryCode ? 'recovery' : 'code'} label={useRecoveryCode ? 'Kurtarma kodu' : 'Doğrulayıcı kodu'} name="factor" autoComplete="one-time-code" inputMode={useRecoveryCode ? 'text' : 'numeric'} required />}
      {factor === 'unknown' && <TextField key={useRecoveryCode ? 'recovery-optional' : 'code-optional'} label={useRecoveryCode ? 'Kurtarma kodu (doğrulayıcı açıksa)' : 'Doğrulayıcı kodu (doğrulayıcı açıksa)'} name="factor" autoComplete="one-time-code" inputMode={useRecoveryCode ? 'text' : 'numeric'} />}
      {(factor === 'on' || factor === 'unknown') && <Button variant="ghost" size="sm" onClick={() => setUseRecoveryCode(value => !value)}>{useRecoveryCode ? 'Doğrulayıcı kodu kullan' : 'Kurtarma kodu kullan'}</Button>}
      {error && <Callout tone="danger" role="alert">{error}</Callout>}
    </form>
  </Modal>, document.body)
}

async function readFactor(): Promise<FactorState> {
  try {
    const status = await api<SecurityStatus>('/security-status')
    return status.totpState === 'ENABLED' ? 'on' : 'off'
  } catch {
    return 'unknown'
  }
}

export function stepUpErrorMessage(reason: unknown) {
  if (!(reason instanceof ApiRequestError)) return 'Doğrulama tamamlanamadı. Bağlantınızı kontrol edip yeniden deneyin.'
  switch (reason.code) {
    case 'REAUTHENTICATION_FAILED': return 'Parola doğrulanamadı. Art arda hatalı denemelerde bu ağdan giriş bir süre engellenir.'
    case 'SECOND_FACTOR_FAILED': return 'Doğrulayıcı kodu veya kurtarma kodu kabul edilmedi.'
    case 'MFA_ATTEMPTS_EXCEEDED': return 'Çok fazla hatalı kod girildi; güvenliğiniz için oturum kapatıldı.'
    case 'AUTHENTICATION_REQUIRED': case 'SESSION_STATE_FORBIDDEN': return 'Oturumunuz sona erdi; yeniden giriş yapın.'
    case 'RATE_LIMITED': return 'Çok fazla deneme yapıldı. Bir dakika bekleyip yeniden deneyin.'
    default: return reason.message
  }
}
