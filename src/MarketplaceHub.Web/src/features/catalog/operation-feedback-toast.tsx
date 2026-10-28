import { useEffect, useLayoutEffect, useRef } from 'react'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { appendNotification, normalizeNotificationKind } from '../../shared/notifications'

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

  const kind = normalizeNotificationKind(feedback.message, feedback.kind)
  return <NoticeToast
    key={`${kind}:${feedback.message}`}
    // A persistent info toast is work in progress; a persistent error (a refused publication) stays until closed.
    kind={feedback.persistent && feedback.kind !== 'error' ? 'pending' : kind}
    title={/yayın uyarısı/iu.test(feedback.message) ? 'Yayın Uyarısı' : undefined}
    message={feedback.message}
    onClose={onClose}
    durationMs={feedback.persistent ? undefined : OPERATION_FEEDBACK_TOAST_DURATION_MS}
  />
}
