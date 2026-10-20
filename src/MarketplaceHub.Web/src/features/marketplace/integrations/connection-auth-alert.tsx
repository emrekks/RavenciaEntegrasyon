import { formatDateTime } from '../../../shared/format-date'
import './connection-auth-alert.css'

export type ConnectionAuthProblem = 'credentials' | 'access' | 'gateway'
type AuthAlertConnection = { status: string; lastErrorCode: string | null; lastErrorAt?: string | null; lastTestedAt: string | null; lastSuccessAt: string | null; disabledAt?: string | null }

// The codes a refused test or sync read leaves on the connection (adapter errors of the authentication class, plus the
// Pazarama gateway block). Webhook signature errors are about one incoming call and do not count.
const credentialCodes = /(_CREDENTIALS?_(REJECTED|INVALID|FORMAT_INVALID)|_AUTHENTICATION_FAILED|_ACCESS_TOKEN_REJECTED|_CONFIGURATION_UNAVAILABLE|_QUESTION_CREDENTIALS|_AUTH_USERNAME_SAVE_FAILED)$/
const accessCodes = /(_ACCESS_DENIED|_ACCESS_FORBIDDEN|_SCOPE_DENIED|_OPERATION_FORBIDDEN|_REQUIRED_READ_SCOPE)$/

export function connectionAuthProblem(code: string | null | undefined): ConnectionAuthProblem | null {
  const value = code?.trim().toUpperCase() ?? ''
  if (!value || value.startsWith('WEBHOOK_') || value.includes('_WEBHOOK_')) return null
  if (value.endsWith('_GATEWAY_BLOCKED')) return 'gateway'
  if (credentialCodes.test(value)) return 'credentials'
  if (accessCodes.test(value)) return 'access'
  return null
}

const presentations: Record<ConnectionAuthProblem, { title: string; action: string; tone: 'danger' | 'warning' }> = {
  credentials: { title: 'Kimlik bilgileri reddedildi', action: 'Bilgileri kontrol edip kaydedin, ardından Test Et’e basın. Test başarılı olana kadar bu bağlantının okumaları bekler.', tone: 'danger' },
  access: { title: 'Pazaryeri erişim izni vermiyor', action: 'Kimlik bilgileri kabul edildi ama bu hesabın gerekli izni yok. İzin açıldıktan sonra Test Et’e basın.', tone: 'danger' },
  gateway: { title: 'Pazaryeri istekleri geçici olarak engelliyor', action: 'Kimlik bilgileri geçerli. Okumalar kendiliğinden yeniden denenir; sürerse pazaryeri desteğine başvurun.', tone: 'warning' }
}

// A disabled connection is turned on again only after a successful test that is newer than the moment it was disabled.
export function reactivationNeedsTest(item: AuthAlertConnection) {
  if (item.status !== 'DISABLED') return false
  if (!item.lastSuccessAt) return true
  if (!item.disabledAt) return false
  return Date.parse(item.lastSuccessAt) <= Date.parse(item.disabledAt)
}

export function activationErrorMessage(reason: unknown) {
  // The API answers CONNECTION_TEST_REQUIRED (ApiRequestError.code) when the last successful test is older than the disable.
  if (typeof reason === 'object' && reason !== null && (reason as { code?: unknown }).code === 'CONNECTION_TEST_REQUIRED')
    return 'Bağlantıyı yeniden açmak için önce yeni bir bağlantı testi gerekir. Test Et’e basın; test başarılı olunca bağlantıyı tekrar etkinleştirin.'
  return reason instanceof Error ? reason.message : 'Bağlantı etkinleştirilemedi.'
}

/** The card and settings warning for refused credentials or access, with when it was last seen. */
export function ConnectionAuthAlert({ item, detail, onTest, testing = false, compact = false }: { item: AuthAlertConnection; detail?: string | null; onTest?: () => void; testing?: boolean; compact?: boolean }) {
  const problem = connectionAuthProblem(item.lastErrorCode)
  const needsTest = reactivationNeedsTest(item)
  if (!problem && !needsTest) return null
  const seenAt = item.lastErrorAt ?? item.lastTestedAt
  const testButton = onTest && <button type="button" className="secondary connection-auth-alert-test" onClick={onTest} disabled={testing}>{testing ? 'Test Ediliyor…' : 'Test Et'}</button>
  if (!problem) return <div className={`connection-auth-alert is-info${compact ? ' is-compact' : ''}`} role="status">
    <div><strong>Yeniden açmak için yeni test gerekli</strong><p>Bağlantı devre dışı bırakıldıktan sonra test edilmedi. Test Et’e basın; test başarılı olunca bağlantıyı etkinleştirebilirsiniz.</p></div>
    {testButton}
  </div>
  const presentation = presentations[problem]
  // The platform's own hint is shown when it says more than the bare error code.
  const hint = detail && detail !== item.lastErrorCode ? detail : null
  return <div className={`connection-auth-alert is-${presentation.tone}${compact ? ' is-compact' : ''}`} role="alert">
    <div>
      <strong>{presentation.title}</strong>
      <p>{presentation.action}</p>
      {!compact && hint && <p className="connection-auth-alert-hint">{hint}</p>}
      <small>{seenAt ? `Son görülme: ${formatDateTime(seenAt)}` : 'Zamanı bilinmiyor'} · Kod: {item.lastErrorCode}</small>
      {needsTest && <small>Devre dışı bağlantıyı yeniden açmak için de yeni bir başarılı test gerekir.</small>}
    </div>
    {testButton}
  </div>
}
