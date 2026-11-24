import { type ReactNode } from 'react'
import { ApiRequestError } from '../../shared/api'
import { FieldHelp, StickyActionBar, TitleHint } from '../../shared/components'
import { uiGlyph16, type UiIconName } from '../../shared/components/UiIcon'
import { formatIstanbul } from '../../shared/format-date'

// System settings as a section list (docs/tasarim-onerileri/sistem-ayarlari.html, option 2): the four former tabs are
// split into ten small sections; the list on the left shows each section's state, the pane on the right shows one.
export type SettingsSectionKey = 'auth' | 'sessions' | 'users' | 'alerts' | 'ai' | 'database' | 'offsite' | 'sender' | 'dims' | 'designer' | 'read' | 'palette' | 'themes' | 'notify'
export type ShippingSection = 'sender' | 'dims' | 'designer'
export type AppearanceSection = 'read' | 'palette' | 'themes' | 'notify'
export type SettingsIconName = 'shield' | 'truck' | 'device' | 'phone' | 'db' | 'user' | 'ruler' | 'layout' | 'type' | 'palette' | 'save' | 'bell' | 'lock' | 'info' | 'alert' | 'logout' | 'trash' | 'reset' | 'plus' | 'barcode' | 'sparkle' | 'copy' | 'back' | 'chev' | 'tag' | 'cloud'
export type SettingsGlyphState = 'done' | 'pending' | 'none' | 'bad'

export const settingsSectionGroups: Array<{ label: string; icon: SettingsIconName; items: Array<{ key: SettingsSectionKey; label: string; short?: string; icon: SettingsIconName }> }> = [
  { label: 'Güvenlik ve Oturumlar', icon: 'shield', items: [{ key: 'auth', label: 'Authenticator', icon: 'shield' }, { key: 'sessions', label: 'Oturumlar', icon: 'device' }, { key: 'alerts', label: 'Alarm Bildirimleri', short: 'Alarmlar', icon: 'bell' }] },
  { label: 'Kullanıcılar', icon: 'user', items: [{ key: 'users', label: 'Kullanıcılar', icon: 'user' }] },
  { label: 'Veri', icon: 'db', items: [{ key: 'database', label: 'Veritabanı Temizliği', short: 'Temizlik', icon: 'db' }, { key: 'offsite', label: 'Google Drive Yedeği', short: 'Drive Yedeği', icon: 'cloud' }] },
  { label: 'Yapay Zekâ', icon: 'sparkle', items: [{ key: 'ai', label: 'Yapay Zekâ', icon: 'sparkle' }] },
  { label: 'Kargo Ayarları', icon: 'truck', items: [{ key: 'sender', label: 'Gönderici ve Yazdırma', short: 'Gönderici', icon: 'user' }, { key: 'dims', label: 'Etiket Ölçüleri', short: 'Ölçüler', icon: 'ruler' }, { key: 'designer', label: 'Etiket Tasarımı', short: 'Tasarım', icon: 'layout' }] },
  { label: 'Görünüm', icon: 'palette', items: [{ key: 'read', label: 'Okunabilirlik ve Tema', short: 'Tema', icon: 'type' }, { key: 'palette', label: 'Renk Paleti', short: 'Renkler', icon: 'palette' }, { key: 'themes', label: 'Kayıtlı Temalar', short: 'Temalar', icon: 'save' }, { key: 'notify', label: 'Bildirimler', icon: 'bell' }] }
]
export const settingsSections = settingsSectionGroups.flatMap(group => group.items.map(item => ({ ...item, group: group.label })))

export function formatSettingsDate(value: string | null) {
  return formatIstanbul(value, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

const settingsIconPaths: Record<SettingsIconName, ReactNode> = {
  shield: <><path d="M8 1.8 13 3.6v4.1c0 3-2.1 5.4-5 6.5-2.9-1.1-5-3.5-5-6.5V3.6z" /><path d="m5.8 8 1.6 1.6 2.9-3" /></>,
  phone: <><rect x="4.5" y="1.8" width="7" height="12.4" rx="1.6" /><path d="M7.2 12h1.6" /></>,
  truck: <><path d="M1.8 4h7.7v7H1.8z" /><path d="M9.5 6.5h2.7l2 2.3V11H9.5" /><circle cx="4.5" cy="11.8" r="1.3" /><circle cx="11.5" cy="11.8" r="1.3" /></>,
  device: <><rect x="2" y="3" width="12" height="8" rx="1.5" /><path d="M5.5 13.5h5M8 11v2.5" /></>,
  db: <><ellipse cx="8" cy="3.8" rx="5" ry="2" /><path d="M3 3.8v8.4c0 1.1 2.2 2 5 2s5-.9 5-2V3.8" /><path d="M3 8c0 1.1 2.2 2 5 2s5-.9 5-2" /></>,
  user: <><circle cx="8" cy="5.5" r="2.7" /><path d="M2.8 13.6a5.4 5.4 0 0 1 10.4 0" /></>,
  ruler: <><rect x="1.8" y="5" width="12.4" height="6" rx="1" /><path d="M4.5 5v2.2M7 5v3M9.5 5v2.2M12 5v3" /></>,
  layout: <><rect x="2.5" y="2.5" width="11" height="11" rx="1.5" /><path d="M2.5 6.5h11M7 6.5v7" /></>,
  type: <path d="M3 4.5V3h10v1.5M8 3v10M6 13h4" />,
  palette: <><path d="M8 2a6 6 0 1 0 0 12c.9 0 1.3-.6 1.3-1.2 0-.8-.6-1-.6-1.7 0-.6.5-1.1 1.1-1.1h1.4A2.8 2.8 0 0 0 14 7.2C14 4.3 11.3 2 8 2z" /><circle cx="5" cy="7.2" r=".7" /><circle cx="7.4" cy="4.8" r=".7" /><circle cx="10.4" cy="5.4" r=".7" /></>,
  save: <><path d="M3 2.5h8l2.5 2.5v8.5H3z" /><path d="M5.5 2.5v3h4v-3M5.5 13.5V9.5h5v4" /></>,
  bell: <><path d="M4 11V7.5a4 4 0 0 1 8 0V11l1 1.5H3z" /><path d="M6.6 14a1.5 1.5 0 0 0 2.8 0" /></>,
  lock: <><rect x="3" y="7" width="10" height="7" rx="1.6" /><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2" /></>,
  info: <><circle cx="8" cy="8" r="6.2" /><path d="M8 7.2v3.6M8 5h.01" /></>,
  alert: <><path d="M8 2.5 14 13H2z" /><path d="M8 6.5v3M8 11.5h.01" /></>,
  logout: <><path d="M6 2.5H3.5v11H6" /><path d="M10 5l3 3-3 3M13 8H6.5" /></>,
  trash: <path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.6 8.5h5.8l.6-8.5" />,
  reset: <><path d="M3 3v3.5h3.5" /><path d="M3.4 6.5a5 5 0 1 1-.3 3" /></>,
  plus: <path d="M8 3.5v9M3.5 8h9" />,
  barcode: <path d="M2.5 3v10M4.8 3v10M7.4 3v10M9.6 3v10M11.4 3v10M13.5 3v10" />,
  sparkle: <path d="M8 2.5 9.3 6.7 13.5 8 9.3 9.3 8 13.5 6.7 9.3 2.5 8l4.2-1.3z" />,
  copy: <><rect x="5" y="5" width="8.5" height="8.5" rx="1.5" /><path d="M11 5V3.5a1 1 0 0 0-1-1H3.5a1 1 0 0 0-1 1V10a1 1 0 0 0 1 1H5" /></>,
  back: <path d="M10 3.5 5.5 8l4.5 4.5" />,
  chev: <path d="m4.5 6 3.5 3.5L11.5 6" />,
  tag: <><path d="M2.5 8.2V3.2a.7.7 0 0 1 .7-.7h5l5.3 5.3a1 1 0 0 1 0 1.4l-4.3 4.3a1 1 0 0 1-1.4 0z" /><circle cx="5.5" cy="5.5" r=".9" /></>,
  cloud: <><path d="M4.6 12.5h6.6a2.8 2.8 0 0 0 .5-5.55A3.9 3.9 0 0 0 4.2 6.3a3.1 3.1 0 0 0 .4 6.2z" /></>
}

// The icons that mean what a glyph of the panel's main icon set means draw that glyph.
const sharedSettingsGlyphs: Partial<Record<SettingsIconName, UiIconName>> = {
  shield: 'shield', truck: 'truck', lock: 'lock', bell: 'bell', alert: 'alert', logout: 'logout', trash: 'trash', plus: 'plus', copy: 'copy', save: 'save',
  tag: 'tag', sparkle: 'sparkle', device: 'monitor', layout: 'layout', back: 'chevronLeft', chev: 'chevronDown'
}

export function SettingsIcon({ name, size = 16 }: { name: SettingsIconName; size?: number }) {
  const shared = sharedSettingsGlyphs[name]
  return <svg className="ss-icon" width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{shared ? uiGlyph16(shared) : settingsIconPaths[name]}</svg>
}

export function SettingsGlyph({ state }: { state: SettingsGlyphState }) {
  if (state === 'done') return <svg className="ss-st is-done" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'pending') return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  if (state === 'bad') return <svg className="ss-st is-bad" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
}

export function SettingsPaneHead({ section, description, actions }: { section: SettingsSectionKey; description?: ReactNode; actions?: ReactNode }) {
  const item = settingsSections.find(entry => entry.key === section) ?? settingsSections[0]
  return <header className="ss-head"><div><h2>{item.label}{description ? <TitleHint label={item.label}>{description}</TitleHint> : null}</h2></div>{actions ? <div className="ss-head-actions">{actions}</div> : null}</header>
}

export function SettingsBar({ title, copy, hint, dirty = false, children }: { title: string; copy?: ReactNode; hint?: ReactNode; dirty?: boolean; children: ReactNode }) {
  return <StickyActionBar label={title} className="ss-bar" info={copy ? { start: <span className={dirty ? 'is-dirty' : undefined}>{copy}</span> } : undefined} lead={<div className="ss-bar-copy"><strong>{title}{hint ? <TitleHint label={title}>{hint}</TitleHint> : null}</strong>{copy ? <span className={dirty ? 'is-dirty' : undefined} title={typeof copy === 'string' ? copy : undefined}>{copy}</span> : null}</div>}><div className="ss-bar-actions">{children}</div></StickyActionBar>
}

export function SettingsSwitch({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return <div className="ss-swfld"><b>{label}</b><label className={`ss-swl${checked ? ' is-on' : ''}`}><input type="checkbox" role="switch" className="ss-sw" aria-label={label} checked={checked} onChange={event => onChange(event.target.checked)} /><span>{checked ? 'Açık' : 'Kapalı'}</span></label></div>
}

// What the server said, in words for the person: a wrong password, a wrong or expired code, too many tries.
export function mfaErrorMessage(reason: unknown, fallback: string) {
  if (!(reason instanceof ApiRequestError)) return fallback
  switch (reason.code) {
    case 'REAUTHENTICATION_FAILED': return 'Parola doğrulanamadı. Art arda hatalı denemelerde bu ağdan giriş bir süre engellenir.'
    case 'SECOND_FACTOR_FAILED': return 'Mevcut doğrulayıcı kodu veya kurtarma kodu kabul edilmedi.'
    case 'MFA_CODE_INVALID': return 'Doğrulama kodu geçersiz. Uygulamadaki güncel 6 haneli kodu girin.'
    case 'MFA_ENROLLMENT_NOT_PENDING': return 'Kurulumun süresi doldu; parolanızla yeniden başlatın.'
    case 'RATE_LIMITED': return 'Çok fazla deneme yapıldı. Bir dakika bekleyip yeniden deneyin.'
    default: return reason.message || fallback
  }
}

// The caller's password, and with an authenticator on, its code: every change on this page asks for them again.
export function ReauthFields({ password, onPassword, factor, onFactor, mfa }: { password: string; onPassword: (value: string) => void; factor: string; onFactor: (value: string) => void; mfa: boolean }) {
  return <div className={mfa ? 'ss-pair' : 'ss-stack'}>
    <label className="ss-fld"><small>Parolanız</small><input className="ss-in" type="password" autoComplete="current-password" required value={password} onChange={event => onPassword(event.target.value)} placeholder="••••••••" /></label>
    {mfa && <label className="ss-fld"><small>Doğrulama Kodu<FieldHelp label="Doğrulama Kodu">Authenticator uygulamanızdaki 6 haneli kod; telefonunuz yanınızda değilse bir kurtarma kodu.</FieldHelp></small><input className="ss-in" inputMode="text" autoComplete="one-time-code" required value={factor} maxLength={20} onChange={event => onFactor(event.target.value)} placeholder="123456" /></label>}
  </div>
}
