import { useEffect, useLayoutEffect, useRef, type CSSProperties } from 'react'
import { UiIcon } from '../../shared/components'
import { appendNotification } from '../../shared/notifications'

export const OPERATION_FEEDBACK_TOAST_DURATION_MS = 3500

export type OperationFeedback = {
  message: string
  kind: 'success' | 'error' | 'info'
  persistent?: boolean
}

export function OperationFeedbackToast({ feedback, onClose }: { feedback: OperationFeedback | null; onClose: () => void }) {
  const onCloseRef = useRef(onClose)

  useEffect(() => { onCloseRef.current = onClose }, [onClose])
  useEffect(() => {
    if (feedback) appendNotification(feedback.message, feedback.kind)
  }, [feedback])
  useLayoutEffect(() => {
    if (!feedback || feedback.persistent) return
    const timer = window.setTimeout(() => onCloseRef.current(), OPERATION_FEEDBACK_TOAST_DURATION_MS)
    return () => window.clearTimeout(timer)
  }, [feedback])

  if (!feedback) return null

  const title = feedback.kind === 'success'
    ? 'İşlem başarılı'
    : feedback.kind === 'error'
      ? 'İşlem başarısız'
      : feedback.persistent ? 'İşlem sürüyor' : 'Bilgi'
  const pendingClass = feedback.persistent ? ' is-pending' : ''

  return <div
    key={`${feedback.kind}:${feedback.message}`}
    className={`rv-toast rv-toast-${feedback.kind === 'error' ? 'danger' : feedback.kind} operation-feedback-toast ${feedback.kind}${pendingClass}`}
    style={{ '--operation-feedback-toast-duration': `${OPERATION_FEEDBACK_TOAST_DURATION_MS}ms` } as CSSProperties}
    role={feedback.kind === 'error' ? 'alert' : 'status'}
    aria-live={feedback.kind === 'error' ? 'assertive' : 'polite'}
    aria-busy={feedback.persistent || undefined}
  >
    <span className="rv-toast-icon" aria-hidden="true" />
    <div className="rv-toast-content">
      <strong>{title}</strong>
      <p>{feedback.message}</p>
    </div>
    <button type="button" onClick={onClose} aria-label="Durum raporunu kapat"><UiIcon name="close" /></button>
    <span className="operation-feedback-toast-progress" aria-hidden="true" />
  </div>
}
