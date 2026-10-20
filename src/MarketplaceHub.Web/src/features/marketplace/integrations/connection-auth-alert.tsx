import { useId } from 'react'
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

/** What a card says about a refused connection, or null when there is nothing to report. */
export function connectionAuthSummary(item: AuthAlertConnection) {
  const problem = connectionAuthProblem(item.lastErrorCode)
  const needsTest = reactivationNeedsTest(item)
  if (!problem && !needsTest) return null
  const seenAt = item.lastErrorAt ?? item.lastTestedAt
  if (!problem) return { title: 'Yeniden açmak için yeni test gerekli', action: 'Bağlantı devre dışı bırakıldıktan sonra test edilmedi. Test Et’e basın; test başarılı olunca bağlantıyı etkinleştirebilirsiniz.', tone: 'info' as const, meta: null }
  const presentation = presentations[problem]
  return { title: presentation.title, action: presentation.action, tone: presentation.tone, meta: `${seenAt ? `Son görülme: ${formatDateTime(seenAt)}` : 'Zamanı bilinmiyor'} · Kod: ${item.lastErrorCode}` }
}

/**
 * The card's state badge for a refused connection: "Hata" (or "Uyarı"), with the reason on hover or focus, so the
 * card keeps the same height as its neighbours instead of growing a warning box.
 */
export function ConnectionAuthBadge({ item }: { item: AuthAlertConnection }) {
  const tipId = `connection-auth-tip${useId()}`
  const summary = connectionAuthSummary(item)
  if (!summary) return null
  const label = summary.tone === 'danger' ? 'Hata' : summary.tone === 'warning' ? 'Uyarı' : 'Test Gerekli'
  // The same chip as "Pasif" / "Bağlı ve aktif"; focusable so a keyboard can read the reason too.
  // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger
  return <span className={`reference-connection-state connection-auth-badge is-${summary.tone}`} tabIndex={0} aria-describedby={tipId}>
    <AuthStateGlyph tone={summary.tone} />{label}
    <span className="connection-auth-tip" role="tooltip" id={tipId}>
      <b>{summary.title}</b>
      {summary.action}
      {summary.meta && <small>{summary.meta}</small>}
    </span>
  </span>
}

// Drawn like the Pasif / Bağlı ve aktif marks (MappingStateGlyph): a refused connection is a filled disc with an
// exclamation, one waiting for a new test is a ring with a clock hand.
function AuthStateGlyph({ tone }: { tone: string }) {
  if (tone === 'info') return <svg className="cm-glyph" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="M8 4.9v3.3l2 1.2" /></svg>
  return <svg className="cm-glyph" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="M8 4.6v4.1" stroke="var(--rv-color-surface)" strokeWidth="1.7" strokeLinecap="round" /><circle cx="8" cy="11.3" r="1" fill="var(--rv-color-surface)" /></svg>
}

/** The settings warning for refused credentials or access, with when it was last seen. */
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
