import { useEffect, useState } from 'react'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { appendNotification } from '../../shared/notifications'

const toastDurationMs = 5500

export type SettingsNoticeKind = 'success' | 'error' | 'info'
export type SettingsNoticeState = { message: string; kind: SettingsNoticeKind }

// Result message of a settings page, shown as the panel's standard notification. The caller always says whether it
// is a success, a failure or a note (W9): a server error text must never be guessed into a green success from its
// wording.
export function SettingsNotice({ message, kind }: SettingsNoticeState) {
  const [visible, setVisible] = useState(true)
  useEffect(() => {
    appendNotification(message, kind)
    setVisible(true)
    const timeout = window.setTimeout(() => setVisible(false), toastDurationMs)
    return () => window.clearTimeout(timeout)
  }, [message, kind])
  return visible ? <NoticeToast key={message} kind={kind} message={message} onClose={() => setVisible(false)} durationMs={toastDurationMs} /> : null
}
