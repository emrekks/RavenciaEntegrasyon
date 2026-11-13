// The re-check a sensitive change carries: the signed-in user's password, and with an authenticator on, its 6-digit code
// or a recovery code (Ayarlar › Kullanıcılar, Google Drive Yedeği, and every reset that empties data).
import { useQuery } from '@tanstack/react-query'
import { hubApi } from './api'

type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }

// Whether the signed-in user's authenticator is on, so the code field is shown (the same query Güvenlik uses).
export function useSecondFactorEnabled() {
  const status = useQuery({ queryKey: ['security'], queryFn: () => hubApi<SecurityStatus>('/auth/security-status') })
  return status.data?.totpState === 'ENABLED'
}

export function reauthBody(password: string, secondFactor: string) {
  const factor = secondFactor.trim()
  if (!factor) return { password }
  return /^\d{6}$/.test(factor.replace(/\s/g, '')) ? { password, code: factor.replace(/\s/g, '') } : { password, recoveryCode: factor }
}

// Ready to send: a password, and with an authenticator on, a code too.
export function reauthReady(password: string, secondFactor: string, mfa: boolean) {
  return password.length > 0 && (!mfa || secondFactor.trim().length > 0)
}

// The two fields in the caller's own field style: each field is a <label> holding its caption and input, or with bare
// the inputs alone (named by aria-label and placeholder) for a one-line confirmation row.
export function ReauthInputs({ idPrefix, password, onPassword, factor, onFactor, mfa, disabled, fieldClassName, inputClassName, captionClassName, bare }: {
  idPrefix: string
  password: string
  onPassword: (value: string) => void
  factor: string
  onFactor: (value: string) => void
  mfa: boolean
  disabled?: boolean
  fieldClassName?: string
  inputClassName?: string
  captionClassName?: string
  bare?: boolean
}) {
  if (bare) return <>
    <input id={`${idPrefix}-password`} className={inputClassName} type="password" autoComplete="current-password" required value={password} disabled={disabled} onChange={event => onPassword(event.target.value)} placeholder="Parolanız" aria-label="Parolanız" title="Parolanız" />
    {mfa && <input id={`${idPrefix}-code`} className={inputClassName} inputMode="text" autoComplete="one-time-code" required value={factor} maxLength={20} disabled={disabled} onChange={event => onFactor(event.target.value)} placeholder="Doğrulama Kodu" aria-label="Doğrulama Kodu (6 haneli kod ya da kurtarma kodu)" title="Doğrulama Kodu (6 haneli kod ya da kurtarma kodu)" />}
  </>
  return <>
    <label className={fieldClassName} htmlFor={`${idPrefix}-password`}>
      <span className={captionClassName}>Parolanız</span>
      <input id={`${idPrefix}-password`} className={inputClassName} type="password" autoComplete="current-password" required value={password} disabled={disabled} onChange={event => onPassword(event.target.value)} placeholder="••••••••" />
    </label>
    {mfa && <label className={fieldClassName} htmlFor={`${idPrefix}-code`}>
      <span className={captionClassName}>Doğrulama Kodu</span>
      <input id={`${idPrefix}-code`} className={inputClassName} inputMode="text" autoComplete="one-time-code" required value={factor} maxLength={20} disabled={disabled} onChange={event => onFactor(event.target.value)} placeholder="123456 ya da kurtarma kodu" />
    </label>}
  </>
}
