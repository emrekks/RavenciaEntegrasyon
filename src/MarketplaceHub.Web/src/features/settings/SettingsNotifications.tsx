import { useEffect, useState } from 'react'
import { NoticeToast, type NoticeToastKind } from '../../shared/components/NoticeToast'
import { appendNotification } from '../../shared/notifications'

const toastDurationMs = 5500

// Result message of a settings page, shown as the panel's standard notification.
export function SettingsNotice({ message, failed }: { message: string; failed: boolean }) {
  const [visible, setVisible] = useState(true)
  useEffect(() => {
    appendNotification(message, failed ? 'error' : 'success')
    setVisible(true)
    const timeout = window.setTimeout(() => setVisible(false), toastDurationMs)
    return () => window.clearTimeout(timeout)
  }, [message, failed])
  return visible ? <NoticeToast key={message} kind={failed ? 'error' : 'success'} message={message} onClose={() => setVisible(false)} durationMs={toastDurationMs} /> : null
}

const samples: Array<{ kind: NoticeToastKind; label: string; message: string }> = [
  { kind: 'success', label: 'Başarılı', message: '#4574136713 için fatura oluşturuldu ve Hepsiburada’ya iletildi.' },
  { kind: 'pending', label: 'Bekliyor', message: 'Fatura Hepsiburada’ya iletiliyor, sonuç birkaç saniye içinde görünecek.' },
  { kind: 'error', label: 'Başarısız', message: 'Fatura oluşturulamadı: sağlayıcı isteği reddetti.' }
]

// Lets the user see how every notification state looks in the current theme. Nothing is recorded in the history.
export function NotificationTestPanel() {
  const [shown, setShown] = useState<{ kind: NoticeToastKind; message: string; id: number } | null>(null)
  useEffect(() => {
    if (!shown) return
    const timeout = window.setTimeout(() => setShown(null), toastDurationMs)
    return () => window.clearTimeout(timeout)
  }, [shown])
  return <section className="appearance-notification-test">
    <div className="appearance-color-editor-heading"><div><h2>Bildirimler</h2><p>Sağ altta çıkan bildirimlerin seçili temadaki görünümünü deneyin.</p></div></div>
    <div className="appearance-notification-test-actions" role="group" aria-label="Bildirim önizleme">
      {samples.map(sample => <button key={sample.kind} type="button" className="secondary" onClick={() => setShown({ kind: sample.kind, message: sample.message, id: Date.now() })}>{sample.label}</button>)}
    </div>
    {shown && <NoticeToast key={shown.id} kind={shown.kind} message={shown.message} onClose={() => setShown(null)} durationMs={shown.kind === 'pending' ? undefined : toastDurationMs} />}
  </section>
}
