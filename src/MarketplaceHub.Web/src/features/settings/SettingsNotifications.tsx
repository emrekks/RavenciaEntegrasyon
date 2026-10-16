import { useEffect, useState } from 'react'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { appendNotification, normalizeNotificationKind } from '../../shared/notifications'

const toastDurationMs = 5500

// Result message of a settings page, shown as the panel's standard notification.
export function SettingsNotice({ message, failed }: { message: string; failed?: boolean }) {
  const kind = failed === undefined ? normalizeNotificationKind(message, 'success') : failed ? 'error' : 'success'
  const [visible, setVisible] = useState(true)
  useEffect(() => {
    appendNotification(message, kind)
    setVisible(true)
    const timeout = window.setTimeout(() => setVisible(false), toastDurationMs)
    return () => window.clearTimeout(timeout)
  }, [message, kind])
  return visible ? <NoticeToast key={message} kind={kind} message={message} onClose={() => setVisible(false)} durationMs={toastDurationMs} /> : null
}
