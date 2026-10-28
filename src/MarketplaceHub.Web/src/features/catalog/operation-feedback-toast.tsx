import { useEffect, useId, useLayoutEffect, useMemo, useRef } from 'react'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { useToastStack } from '../../shared/components/toast-stack-context'
import { appendNotification, normalizeNotificationKind } from '../../shared/notifications'
import type { ToastInput } from '../../shared/toast-stack'

export const OPERATION_FEEDBACK_TOAST_DURATION_MS = 3500

export type OperationFeedback = {
  message: string
  kind: 'success' | 'error' | 'info'
  persistent?: boolean
  /** A structured result (save + publish): its own title, one line per marketplace, and its own reading time. */
  title?: string
  lines?: readonly string[]
  durationMs?: number
  /** A slot in the toast column: the next feedback with the same key takes this one's place (a running save its result). */
  key?: string
}

/** How an operation's feedback looks as a toast. */
export function operationFeedbackToast(feedback: OperationFeedback): ToastInput {
  // A structured result names its own tone: "kuyruğa alındı" in a line must not turn a started publication blue.
  const kind = feedback.title ? feedback.kind : normalizeNotificationKind(feedback.message, feedback.kind)
  return {
    // A persistent info toast is work in progress; a persistent error (a refused publication) stays until closed.
    kind: feedback.persistent && feedback.kind !== 'error' ? 'pending' : kind,
    title: feedback.title ?? (/yayın uyarısı/iu.test(feedback.message) ? 'Yayın Uyarısı' : undefined),
    message: feedback.message,
    lines: feedback.lines,
    durationMs: feedback.persistent ? undefined : feedback.durationMs ?? OPERATION_FEEDBACK_TOAST_DURATION_MS,
    key: feedback.key
  }
}

/**
 * Feedback of a screen's operations in the shell's toast column: each one lands below the toasts already shown and goes
 * to the notification history. A running (pending) toast the screen left on screen is taken away when it closes.
 */
export function useOperationFeedbackToasts() {
  const stack = useToastStack()
  const owner = useId()
  useEffect(() => () => stack.dismissWhere(item => item.owner === owner && item.kind === 'pending'), [stack, owner])
  return useMemo(() => ({
    show: (feedback: OperationFeedback) => {
      appendNotification(feedback.title ? `${feedback.title}: ${feedback.message}` : feedback.message, feedback.kind)
      stack.show({ ...operationFeedbackToast(feedback), owner })
    },
    /** Takes away the toast in a key's slot. */
    dismiss: (key: string) => stack.dismissKey(key),
    /** Takes away the error toasts this screen showed (the form was edited after them). */
    dismissErrors: () => stack.dismissWhere(item => item.owner === owner && item.kind === 'error')
  }), [stack, owner])
}

/** A single feedback toast a component owns itself (outside the shell's column). */
export function OperationFeedbackToast({ feedback, onClose }: { feedback: OperationFeedback | null; onClose: () => void }) {
  const onCloseRef = useRef(onClose)

  useEffect(() => { onCloseRef.current = onClose }, [onClose])
  useEffect(() => {
    if (feedback) appendNotification(feedback.title ? `${feedback.title}: ${feedback.message}` : feedback.message, feedback.kind)
  }, [feedback])
  useLayoutEffect(() => {
    if (!feedback || feedback.persistent) return
    const timer = window.setTimeout(() => onCloseRef.current(), feedback.durationMs ?? OPERATION_FEEDBACK_TOAST_DURATION_MS)
    return () => window.clearTimeout(timer)
  }, [feedback])

  if (!feedback) return null

  const toast = operationFeedbackToast(feedback)
  return <NoticeToast
    key={`${toast.kind}:${feedback.message}`}
    kind={toast.kind}
    title={toast.title}
    message={toast.message}
    lines={toast.lines}
    onClose={onClose}
    durationMs={toast.durationMs}
  />
}
