import { useContext, useEffect, useRef, useState, type CSSProperties } from 'react'
import { createPortal } from 'react-dom'
import { appendNotification, normalizeNotificationKind } from '../notifications'
import { ToneIcon } from './ToneIcon'
import { ToastViewportContext } from './toast-stack-context'
import { UiIcon } from './UiIcon'
import './notice-toast.css'

export type NoticeToastKind = 'success' | 'error' | 'info' | 'pending'

const defaultTitles: Record<NoticeToastKind, string> = {
  success: 'İşlem Başarılı',
  error: 'İşlem Başarısız',
  info: 'Bilgi',
  pending: 'İşlem Sürüyor'
}

// A top-right notification of the panel. Inside the shell it is drawn into the shared toast column, below the toasts
// already shown. Tone shows only in the icon tile and the countdown bar, the card itself always follows the theme.
export function NoticeToast({ kind, message, lines, title, onClose, durationMs, className }: {
  kind: NoticeToastKind
  message: string
  /** Several outcomes (one per marketplace) shown as a list instead of one run-on paragraph. */
  lines?: readonly string[]
  title?: string
  onClose?: () => void
  durationMs?: number
  className?: string
}) {
  const failed = kind === 'error'
  const viewport = useContext(ToastViewportContext)
  const toast = <div
    className={['app-toast', `app-toast-${kind}`, className].filter(Boolean).join(' ')}
    role={failed ? 'alert' : 'status'}
    aria-live={failed ? 'assertive' : 'polite'}
    aria-busy={kind === 'pending' || undefined}
    style={durationMs ? { '--app-toast-duration': `${durationMs}ms` } as CSSProperties : undefined}
  >
    <span className="app-toast-icon" aria-hidden="true">
      {kind === 'pending'
        ? <svg className="app-toast-spinner" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2.25" strokeLinecap="round"><circle cx="12" cy="12" r="8.5" opacity=".25" /><path d="M12 3.5a8.5 8.5 0 0 1 8.5 8.5" /></svg>
        : <ToneIcon kind={kind} />}
    </span>
    <div className="app-toast-content">
      <strong>{title ?? defaultTitles[kind]}</strong>
      {lines && lines.length > 1
        ? <ul className="app-toast-lines">{lines.map((line, index) => <li key={index}>{line}</li>)}</ul>
        : <p>{lines?.[0] ?? message}</p>}
    </div>
    {onClose && <button type="button" className="app-toast-close" onClick={onClose} aria-label="Bildirimi kapat"><UiIcon name="close" size={16} /></button>}
    {(durationMs || kind === 'pending') && <span className="app-toast-track" aria-hidden="true"><span className="app-toast-progress" /></span>}
  </div>
  return viewport ? createPortal(toast, viewport) : toast
}

// A plain message: the tone is the one the caller names, or else comes from its wording the way the notification
// history reads it. A caller that knows it is reporting a failure says so: a marketplace's own error text rarely
// contains the words the wording check looks for and would otherwise show as a green success.
export function MessageToast({ message, onClose, kind: explicitKind }: { message: string; onClose: () => void; kind?: 'success' | 'error' | 'info' }) {
  const kind = explicitKind ?? normalizeNotificationKind(message, 'success')
  const durationMs = kind === 'info' ? 7000 : 5500
  const onCloseRef = useRef(onClose)
  useEffect(() => { onCloseRef.current = onClose })
  useEffect(() => {
    const timeout = window.setTimeout(() => onCloseRef.current(), durationMs)
    return () => window.clearTimeout(timeout)
  }, [message, durationMs])
  return <NoticeToast key={`${kind}:${message}`} kind={kind} message={message} onClose={onClose} durationMs={durationMs} />
}

/** An action's error as a toast that also goes to the notification history; it hides itself after eight seconds. */
export function ErrorToast({ error, title, onDismiss }: { error: unknown; title?: string; onDismiss?: () => void }) {
  const message = error ? (error instanceof Error ? error.message : 'İşlem tamamlanamadı.') : ''
  const [hiddenMessage, setHiddenMessage] = useState('')
  useEffect(() => {
    if (!message) return
    appendNotification(title ? `${title}: ${message}` : message, 'error')
    const timeout = window.setTimeout(() => setHiddenMessage(message), 8000)
    return () => window.clearTimeout(timeout)
  }, [message, title])
  if (!message || hiddenMessage === message) return null
  return <NoticeToast kind="error" title={title} message={message} durationMs={8000} onClose={() => { setHiddenMessage(message); onDismiss?.() }} />
}
