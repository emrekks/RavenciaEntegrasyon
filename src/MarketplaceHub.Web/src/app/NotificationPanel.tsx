import { useState } from 'react'
import { ToneIcon, UiIcon } from '../shared/components'
import './notification-panel.css'
import { describeNotification } from '../shared/notification-describe'
import { clearNotificationHistory, markAllNotificationsRead, markNotificationRead, type AppNotification } from '../shared/notifications'

type Filter = 'ALL' | 'UNREAD' | 'ERROR'


// The first sentence is the summary; anything after " · " is the technical detail.
function notificationPresentation(notification: AppNotification) {
  const parts = notification.message.split(' · ').map(part => part.trim()).filter(Boolean)
  const summary = parts.shift() ?? notification.message
  const text = notification.message.toLocaleLowerCase('tr-TR')
  const title = /yayın uyarısı/u.test(text) ? 'Yayın Uyarısı' : /iade kabul/u.test(text) ? (notification.kind === 'error' ? 'İade Kabul Kararı İletilemedi' : 'İade Kabul Kararı İletildi')
    : /iade (?:ret\b|reddi)/u.test(text) ? (notification.kind === 'error' ? 'İade Ret Kararı İletilemedi' : 'İade Ret Kararı İletildi')
      : text.includes('kuyruğa alındı') ? 'İşlem Kuyruğa Alındı'
        : text.includes('iptal') ? 'İşlem İptal Edildi'
          : text.includes('silindi') ? 'Katalog Temizliği Tamamlandı'
            : text.includes('seçildi') ? 'Katalog Seçimi Tamamlandı'
              : notification.kind === 'error' ? 'İşlem Başarısız'
                : notification.kind === 'success' ? 'İşlem Tamamlandı'
                  : 'İşlem Bilgisi'
  return { title, summary, extra: parts }
}

function relativeTime(iso: string) {
  const created = new Date(iso)
  const minutes = Math.floor((Date.now() - created.getTime()) / 60000)
  if (minutes < 1) return 'Az önce'
  if (minutes < 60) return `${minutes} dk önce`
  return created.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })
}

function dayDate(iso: string) {
  return new Date(iso).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', weekday: 'long' })
}

function dayLabel(iso: string) {
  const created = new Date(iso)
  const today = new Date()
  const yesterday = new Date(today.getTime() - 86_400_000)
  if (created.toDateString() === today.toDateString()) return 'Bugün'
  if (created.toDateString() === yesterday.toDateString()) return 'Dün'
  return created.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long' })
}

type ItemProps = { notification: AppNotification; expanded: boolean; onToggle: () => void; onOpen: (href: string) => void }

function NotificationItem({ notification, expanded, onToggle, onOpen }: ItemProps) {
  const presentation = notificationPresentation(notification)
  const unread = !notification.read
  return <article className={`rv-notification-history-item is-${notification.kind} ${unread ? 'is-unread' : 'is-read'}${expanded ? ' is-expanded' : ''}`}>
    <span className="rv-notification-icon" aria-hidden="true"><ToneIcon kind={notification.kind} size={20} /></span>
    <div className="rv-notification-history-copy">
      <div className="rv-notification-history-top">
        <strong>{presentation.title}</strong>
        <time dateTime={notification.createdAt} title={new Date(notification.createdAt).toLocaleString('tr-TR')}>{relativeTime(notification.createdAt)}</time>
      </div>
      <p>{presentation.summary}</p>
      {expanded && <dl className="rv-notification-detail">
        {describeNotification(notification).map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd className={fact.tone ? `is-${fact.tone}` : undefined}>{fact.value}</dd></div>)}
        {notification.details?.map((detail, index) => <div key={`${detail.label}:${index}`}><dt>{detail.label}</dt><dd className={detail.tone ? `is-${detail.tone}` : undefined}>{detail.value}</dd></div>)}
        <div><dt>Zaman</dt><dd>{new Date(notification.createdAt).toLocaleString('tr-TR')}</dd></div>
      </dl>}
      <div className="rv-notification-foot">
        {notification.href && <button type="button" className="rv-notification-go" onClick={() => onOpen(notification.href!)}>{notification.actionLabel ?? 'Sayfaya Git'}</button>}
        <button type="button" className="rv-notification-more" aria-expanded={expanded} onClick={onToggle}>{expanded ? 'Ayrıntıyı Gizle' : 'Ayrıntı'}</button>
        {unread && <button type="button" className="rv-notification-mark-read" onClick={() => markNotificationRead(notification.id)} aria-label="Okundu olarak işaretle" title="Okundu olarak işaretle"><UiIcon name="circleCheck" size={18} /></button>}
      </div>
    </div>
  </article>
}

type PanelProps = { notifications: AppNotification[]; onNavigate: (href: string) => void; onCleared: () => void }

export function NotificationPanel({ notifications, onNavigate, onCleared }: PanelProps) {
  const [filter, setFilter] = useState<Filter>('ALL')
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const unread = notifications.filter(notification => !notification.read)
  const errors = notifications.filter(notification => notification.kind === 'error')
  const visible = filter === 'UNREAD' ? unread : filter === 'ERROR' ? errors : notifications
  const groups = Array.from(visible.reduce((map, notification) => {
    const label = dayLabel(notification.createdAt)
    return map.set(label, [...(map.get(label) ?? []), notification])
  }, new Map<string, AppNotification[]>()))
  const dayKind = (label: string) => label === 'Bugün' ? 'is-today' : label === 'Dün' ? 'is-yesterday' : 'is-older'
  const filters: Array<[Filter, string, number]> = [['ALL', 'Tümü', notifications.length], ['UNREAD', 'Okunmamış', unread.length], ['ERROR', 'Hatalar', errors.length]]

  const open = (notification: AppNotification) => (href: string) => {
    markNotificationRead(notification.id)
    onNavigate(href)
  }

  return <>
    <div className="rv-notification-history-heading">
      <span className="rv-notification-history-title"><strong>Bildirimler</strong>{unread.length > 0 && <b aria-label={`${unread.length} okunmamış bildirim`}>{unread.length}</b>}</span>
      <div className="rv-notification-history-actions">
        <button type="button" className="rv-notification-mark-all" onClick={() => markAllNotificationsRead()} disabled={unread.length === 0} title="Tüm bildirimleri okundu olarak işaretle">Tümünü Okundu Yap</button>
        <button type="button" className="rv-notification-clear" onClick={() => { clearNotificationHistory(); onCleared() }} disabled={notifications.length === 0}>Temizle</button>
      </div>
    </div>
    <div className="rv-notification-filters" role="tablist" aria-label="Bildirim filtresi">
      {filters.map(([value, label, count]) => <button key={value} type="button" role="tab" aria-selected={filter === value} className={filter === value ? 'is-active' : undefined} onClick={() => setFilter(value)}>{label}<span>{count}</span></button>)}
    </div>
    {visible.length === 0
      ? <p className="rv-notification-history-empty">{notifications.length === 0 ? 'Henüz kaydedilmiş bildirim yok.' : filter === 'UNREAD' ? 'Okunmamış bildirim yok.' : 'Hata bildirimi yok.'}</p>
      : <div className="rv-notification-history-list">
        {groups.map(([label, items]) => <section key={label} className={`rv-notification-day ${dayKind(label)}`} aria-label={label}>
          <h4 className="rv-notification-group"><span>{label}</span><small>{dayDate(items[0].createdAt)}</small><b>{items.length}</b></h4>
          {items.map(notification => <NotificationItem key={notification.id} notification={notification} expanded={expandedId === notification.id} onToggle={() => setExpandedId(current => current === notification.id ? null : notification.id)} onOpen={open(notification)} />)}
        </section>)}
      </div>}
  </>
}
