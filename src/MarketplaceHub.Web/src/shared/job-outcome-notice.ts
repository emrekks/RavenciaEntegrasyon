import { useMemo } from 'react'
import { useToastStack } from './components/toast-stack-context'
import { followJobOutcomes, outcomeTarget, type FollowOptions, type FollowedJob, type JobOutcomeFeedback } from './job-outcome'
import { appendNotification, type NotificationContext } from './notifications'

/** How a followed result is shown: a toast in the shell's column (no owner, so it outlives the screen) and its notification. */
export function jobOutcomeToast(feedback: JobOutcomeFeedback, interim: boolean) {
  return {
    kind: feedback.kind,
    title: feedback.title,
    message: feedback.lines.join(' '),
    lines: feedback.lines,
    // A failure stays until it is read; a running or finished transfer closes by itself.
    durationMs: feedback.kind === 'error' ? undefined : interim ? 8000 : 6000
  } as const
}

/**
 * Follows jobs a screen started (a store-wide price-stock push, a sale status change, an invoice resend) to their
 * real result: the screen first says the transfer started, this then says aktarıldı / aktarılamadı with the reason, or
 * that it is still running. The result goes to the notification center too, so it reaches the owner after leaving.
 */
export function useJobOutcomeNotice() {
  const stack = useToastStack()
  return useMemo(() => (jobs: readonly FollowedJob[], context: NotificationContext = {}, options: FollowOptions = {}) => {
    if (!jobs.length) return
    const notification = context
    void followJobOutcomes(jobs, outcomeTarget(jobs), (feedback, interim) => {
      const toast = jobOutcomeToast(feedback, interim)
      appendNotification({ ...notification, title: toast.title, message: toast.message, kind: toast.kind, jobId: jobs.length === 1 ? jobs[0].jobId : notification.jobId })
      stack.show(toast)
    }, options)
  }, [stack])
}
