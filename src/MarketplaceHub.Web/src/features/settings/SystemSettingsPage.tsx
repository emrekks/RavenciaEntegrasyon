import { Fragment, useEffect, useEffectEvent, useRef, useState, type CSSProperties, type DragEvent, type FormEvent, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { useSearchParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi } from '../../shared/api'
import { LoadingState, Modal } from '../../shared/components'
import type { DataResetGroup } from '../../shared/components/DataResetLedger'
import { NoticeToast, type NoticeToastKind } from '../../shared/components/NoticeToast'
import { code128Bars, defaultShippingLabelBlockPosition, defaultShippingLabelSettings, normalizeShippingLabelSettings, shippingLabelBlockCatalog, shippingLabelFields, useShippingLabelSettings, type ShippingLabelBlock, type ShippingLabelBlockKind, type ShippingLabelField, type ShippingLabelSettings } from '../shipping'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontFamilyOptions, appearanceFontScale, appearanceFontSizeOptions, defaultAppearanceColorTheme, defaultAppearanceSettings, grafitAppearanceColorTheme, matchingColorThemeId, defaultLightPalette, useAppearanceSettings, type AppearanceColorThemeProfile, type AppearancePalette, type AppearanceSettings } from './appearance-settings'
import { SettingsNotice, type SettingsNoticeState } from './SettingsNotifications'
import { readVisualThemePreference, setVisualThemePreference, visualThemeChangeEvent, type VisualTheme } from './visual-theme'
import { shippingLabelBlockParts } from './shipping-label-preview'
import { useRememberedTabCounts } from '../../shared/tab-counts'
import './settings-sade.css'
import '../../shared/view-tabs.css'
import { formatIstanbul } from '../../shared/format-date'

type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; secret?: string; qrSvg: string; expiresAt: string }
type SessionConfirmation = { action: 'revoke' | 'revoke-others' | 'delete' | 'delete-closed'; sessionId?: string }

// System settings as a section list (docs/tasarim-onerileri/sistem-ayarlari.html, option 2): the four former tabs are
// split into ten small sections; the list on the left shows each section's state, the pane on the right shows one.
type SettingsSectionKey = 'auth' | 'sessions' | 'database' | 'sender' | 'dims' | 'designer' | 'read' | 'palette' | 'themes' | 'notify'
type ShippingSection = 'sender' | 'dims' | 'designer'
type AppearanceSection = 'read' | 'palette' | 'themes' | 'notify'
type SettingsIconName = 'shield' | 'truck' | 'device' | 'phone' | 'db' | 'user' | 'ruler' | 'layout' | 'type' | 'palette' | 'save' | 'bell' | 'lock' | 'info' | 'alert' | 'logout' | 'trash' | 'reset' | 'plus' | 'barcode' | 'sparkle' | 'copy' | 'back' | 'chev'
type SettingsGlyphState = 'done' | 'pending' | 'none' | 'bad'

const settingsSectionGroups: Array<{ label: string; icon: SettingsIconName; items: Array<{ key: SettingsSectionKey; label: string; short?: string; icon: SettingsIconName }> }> = [
  { label: 'Güvenlik ve Oturumlar', icon: 'shield', items: [{ key: 'auth', label: 'Authenticator', icon: 'shield' }, { key: 'sessions', label: 'Oturumlar', icon: 'device' }] },
  { label: 'Veri', icon: 'db', items: [{ key: 'database', label: 'Veritabanı Temizliği', icon: 'db' }] },
  { label: 'Kargo Ayarları', icon: 'truck', items: [{ key: 'sender', label: 'Gönderici ve Yazdırma', short: 'Gönderici', icon: 'user' }, { key: 'dims', label: 'Etiket Ölçüleri', short: 'Ölçüler', icon: 'ruler' }, { key: 'designer', label: 'Etiket Tasarımı', short: 'Tasarım', icon: 'layout' }] },
  { label: 'Görünüm', icon: 'palette', items: [{ key: 'read', label: 'Okunabilirlik ve Tema', short: 'Tema', icon: 'type' }, { key: 'palette', label: 'Renk Paleti', short: 'Renkler', icon: 'palette' }, { key: 'themes', label: 'Kayıtlı Temalar', short: 'Temalar', icon: 'save' }, { key: 'notify', label: 'Bildirimler', icon: 'bell' }] }
]
const settingsSections = settingsSectionGroups.flatMap(group => group.items.map(item => ({ ...item, group: group.label })))
// Links from before the section list still open the matching first section.
const legacySettingsSections: Record<string, SettingsSectionKey> = { security: 'auth', shipping: 'sender', appearance: 'read' }
const shippingSections: SettingsSectionKey[] = ['sender', 'dims', 'designer']
const appearanceSections: SettingsSectionKey[] = ['read', 'palette', 'themes', 'notify']

function settingsSectionFrom(value: string | null): SettingsSectionKey {
  if (value && legacySettingsSections[value]) return legacySettingsSections[value]
  return settingsSections.find(item => item.key === value)?.key ?? 'auth'
}

function formatSettingsDate(value: string) {
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
  chev: <path d="m4.5 6 3.5 3.5L11.5 6" />
}

function SettingsIcon({ name, size = 16 }: { name: SettingsIconName; size?: number }) {
  return <svg className="ss-icon" width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{settingsIconPaths[name]}</svg>
}

function SettingsGlyph({ state }: { state: SettingsGlyphState }) {
  if (state === 'done') return <svg className="ss-st is-done" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'pending') return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  if (state === 'bad') return <svg className="ss-st is-bad" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
}

function SettingsPaneHead({ section, description, actions }: { section: SettingsSectionKey; description: ReactNode; actions?: ReactNode }) {
  const item = settingsSections.find(entry => entry.key === section) ?? settingsSections[0]
  return <header className="ss-head"><div><h2>{item.label}</h2><p>{description}</p></div>{actions ? <div className="ss-head-actions">{actions}</div> : null}</header>
}

function SettingsBar({ title, copy, dirty = false, children }: { title: string; copy: ReactNode; dirty?: boolean; children: ReactNode }) {
  return <div className="ss-bar"><div className="ss-bar-copy"><strong>{title}</strong><span className={dirty ? 'is-dirty' : undefined}>{copy}</span></div><div className="ss-bar-actions">{children}</div></div>
}

function SettingsSwitch({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return <div className="ss-swfld"><b>{label}</b><label className={`ss-swl${checked ? ' is-on' : ''}`}><input type="checkbox" role="switch" className="ss-sw" aria-label={label} checked={checked} onChange={event => onChange(event.target.checked)} /><span>{checked ? 'Açık' : 'Kapalı'}</span></label></div>
}

function SettingsSwatches({ palette, large = false }: { palette: AppearancePalette; large?: boolean }) {
  return <span className={`ss-sws${large ? ' is-lg' : ''}`} aria-hidden="true">{[palette.bg, palette.surface, palette.primary, palette.accent].map((color, index) => <i key={index} style={{ backgroundColor: color }} />)}</span>
}

function OtpInput({ digits, onChange }: { digits: string[]; onChange: (digits: string[]) => void }) {
  const refs = useRef<Array<HTMLInputElement | null>>([])
  function fill(start: number, text: string) {
    const clean = text.replace(/\D/g, '').slice(0, digits.length - start)
    const next = [...digits]
    if (!clean) {
      next[start] = ''
      onChange(next)
      return
    }
    clean.split('').forEach((digit, offset) => { next[start + offset] = digit })
    onChange(next)
    refs.current[Math.min(digits.length - 1, start + clean.length)]?.focus()
  }
  return <div className="ss-otp" role="group" aria-label="6 haneli doğrulama kodu">{digits.map((digit, index) => <input key={index} ref={element => { refs.current[index] = element }} inputMode="numeric" autoComplete={index === 0 ? 'one-time-code' : 'off'} data-autofocus={index === 0 || undefined} maxLength={6} value={digit} aria-label={`${index + 1}. hane`} onChange={event => { const raw = event.target.value; fill(index, raw.length > 1 && digit ? raw.replace(digit, '') : raw) }} onKeyDown={event => { if (event.key === 'Backspace' && !digit && index > 0) refs.current[index - 1]?.focus() }} onPaste={event => { event.preventDefault(); fill(index, event.clipboardData.getData('text')) }} />)}</div>
}

const notificationSamples: Array<{ kind: NoticeToastKind; label: string; glyph: SettingsGlyphState; message: string }> = [
  { kind: 'success', label: 'Başarılı', glyph: 'done', message: '#4574136713 için fatura oluşturuldu ve Hepsiburada’ya iletildi.' },
  { kind: 'pending', label: 'Bekliyor', glyph: 'pending', message: 'Fatura Hepsiburada’ya iletiliyor, sonuç birkaç saniye içinde görünecek.' },
  { kind: 'error', label: 'Başarısız', glyph: 'bad', message: 'Fatura oluşturulamadı: sağlayıcı isteği reddetti.' }
]
const notificationSampleDurationMs = 5500

// Lets the user see how every notification state looks in the current theme. Nothing is recorded in the history.
function NotificationSamples() {
  const [shown, setShown] = useState<{ kind: NoticeToastKind; message: string; id: number } | null>(null)
  useEffect(() => {
    if (!shown || shown.kind === 'pending') return
    const timeout = window.setTimeout(() => setShown(null), notificationSampleDurationMs)
    return () => window.clearTimeout(timeout)
  }, [shown])
  return <>
    <div className="ss-ntest" role="group" aria-label="Bildirim önizleme">{notificationSamples.map(sample => <button type="button" key={sample.kind} className="ss-btn" onClick={() => setShown({ kind: sample.kind, message: sample.message, id: Date.now() })}><SettingsGlyph state={sample.glyph} />{sample.label}</button>)}</div>
    {shown && <NoticeToast key={shown.id} kind={shown.kind} message={shown.message} onClose={() => setShown(null)} durationMs={shown.kind === 'pending' ? undefined : notificationSampleDurationMs} />}
  </>
}

const resetScopeGroups: DataResetGroup[] = [
  { label: 'Katalog', description: 'Ürün kataloğunda kullanılan temel listeler.', options: [
    { scope: 'PRODUCTS', label: 'Ürünler Listesi', description: 'Ürünleri, varyantları ve bu ürünlere bağlı görsel, stok ve kanal kayıtlarını temizler.', unit: 'ürün' },
    { scope: 'CATEGORIES', label: 'Kategori Listesi', description: 'Panel kategori ağacını ve kategori-pazaryeri eşleştirmelerini kaldırır; ürünlerin kategori bağlantısını boşaltır.', unit: 'kategori' },
    { scope: 'CATEGORY_ATTRIBUTES', label: 'Kategori Özellikleri', description: 'Kategori özellik başlıklarını, değerlerini, kategori atamalarını, ürün değerlerini ve pazaryeri özellik eşleştirmelerini kaldırır.', unit: 'özellik' },
    { scope: 'BRANDS', label: 'Marka Listesi', description: 'Marka kayıtlarını ve marka-pazaryeri eşleştirmelerini kaldırır; ürünlerin marka bağlantısını boşaltır.', unit: 'marka' }
  ] },
  { label: 'Ürün Seçenekleri', description: 'Seçenek grupları ve değerleri.', options: [
    { scope: 'OPTIONS', label: 'Ürün Seçenekleri', description: 'Ürün seçenek gruplarını, seçenek değerlerini ve varyantlarla olan seçenek bağlantılarını temizler.', unit: 'seçenek' }
  ] }
]
const resetConfirmWord = 'Verileri sil'
const resetNumber = (value: number) => value.toLocaleString('tr-TR')

// Database cleanup: each group has one header with its own select-all and a selected/total count; each data type is
// a whole-row tile (checkbox beside the title, count as a tag); the notes sit in one callout and the sticky bar shows
// what goes as chips, the total, the typed confirmation and the reset button.
function SettingsDataReset({ groups, counts, countsFailed, picked, onToggle, confirmation, onConfirmation, busy, onConfirm }: { groups: DataResetGroup[]; counts?: Record<string, number>; countsFailed: boolean; picked: string[]; onToggle: (scopes: string[], checked: boolean) => void; confirmation: string; onConfirmation: (value: string) => void; busy: boolean; onConfirm: () => void }) {
  const options = groups.flatMap(group => group.options)
  const going = options.filter(option => picked.includes(option.scope))
  const known = going.every(option => counts?.[option.scope] !== undefined)
  const total = going.reduce((sum, option) => sum + (counts?.[option.scope] ?? 0), 0)
  const confirmed = confirmation.trim() === resetConfirmWord
  const countText = (scope: string, unit: string) => {
    const value = counts?.[scope]
    if (value !== undefined) return `${resetNumber(value)} ${unit}`
    return countsFailed ? 'Sayı alınamadı' : counts ? null : 'Sayılıyor…'
  }
  return <>
    <SettingsPaneHead section="database" description="Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir." />
    <div className="ss-body ss-dr" role="group" aria-label="Sıfırlanacak veriler">
      {groups.map(group => {
        const chosen = group.options.filter(option => picked.includes(option.scope)).length
        const all = chosen === group.options.length
        return <section className="ss-dr-group" key={group.label} aria-label={group.label}>
          <header className="ss-dr-gh">
            <div><h3>{group.label}</h3><p>{group.description}</p></div>
            <span className={`ss-tag${chosen ? ' is-ink' : ''}`}>{chosen}/{group.options.length} Seçili</span>
            <button type="button" className="ss-btn is-xs" disabled={busy} onClick={() => onToggle(group.options.map(option => option.scope), !all)}>{all ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>
          </header>
          <div className="ss-dr-list">{group.options.map(option => {
            const on = picked.includes(option.scope)
            const count = countText(option.scope, option.unit)
            return <label key={option.scope} className={`ss-dr-opt${on ? ' is-on' : ''}`}>
              <input type="checkbox" className="ss-cb" checked={on} disabled={busy} onChange={event => onToggle([option.scope], event.target.checked)} />
              <span className="ss-dr-text"><b>{option.label}</b><small>{option.description}</small></span>
              {count ? <span className="ss-tag ss-dr-count">{count}</span> : null}
            </label>
          })}</div>
        </section>
      })}
      <div className="ss-callout ss-dr-note">
        <p className="is-warn"><SettingsIcon name="alert" /><span><b>Geri alınamaz.</b> Yetkili işlemi: bağlı alt kayıtlar güvenli sırayla temizlenir.</span></p>
        <p><SettingsIcon name="info" /><span>Sipariş, iade ve fatura kayıtları her platform için ayrı silinir: <b>Entegrasyonlar</b> sayfasında bağlantının menüsünden <b>Verileri Sıfırla</b>.</span></p>
      </div>
    </div>
    <div className="ss-bar ss-dr-bar">
      <div className="ss-dr-sum" role="status">
        <div className="ss-dr-sum-head"><strong>Silinecekler</strong><span className="ss-dr-total">Toplam <b>{going.length && !known ? '…' : `${resetNumber(total)} kayıt`}</b></span></div>
        <div className="ss-dr-chips">{going.length
          ? going.map(option => <span key={option.scope} className="ss-dr-chip">{option.label}{counts?.[option.scope] !== undefined ? <b>{resetNumber(counts[option.scope])}</b> : null}</span>)
          : <span className="ss-dr-empty">Henüz seçim yok; yukarıdan silinecek veri türünü işaretleyin.</span>}</div>
      </div>
      <div className="ss-bar-actions">
        <input id="settings-reset-confirm" className={`ss-in ss-dr-in${confirmed ? ' is-match' : ''}`} value={confirmation} onChange={event => onConfirmation(event.target.value)} disabled={busy} autoComplete="off" placeholder={`${resetConfirmWord} yazın`} aria-label={`Onaylamak için ${resetConfirmWord} yazın`} title={`Onaylamak için ${resetConfirmWord} yazın`} />
        <button type="button" className="ss-btn is-danger is-solid ss-dr-go" disabled={!going.length || !confirmed || busy} onClick={onConfirm}><SettingsIcon name="trash" />{busy ? 'Sıfırlanıyor…' : going.length ? `${going.length} Veri Türünü Sıfırla` : 'Seçilen Verileri Sıfırla'}</button>
      </div>
    </div>
  </>
}

const mfaSteps: Array<{ key: 'password' | 'verify' | 'recovery'; label: string; hint: string }> = [
  { key: 'password', label: 'Parola', hint: 'Hesabı doğrula' },
  { key: 'verify', label: 'QR Kod', hint: 'Uygulamaya ekle' },
  { key: 'recovery', label: 'Kurtarma Kodları', hint: 'Güvenle sakla' }
]
const emptyOtp = ['', '', '', '', '', '']

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

// The key an authenticator app asks for when the QR code cannot be scanned: the base32 secret, in groups of four.
export function manualSetupKey(otpauthUri: string) {
  const secret = /[?&]secret=([A-Z2-7]+)/i.exec(otpauthUri)?.[1]?.toUpperCase() ?? ''
  return secret.match(/.{1,4}/g)?.join(' ') ?? ''
}

export function SystemSettingsPage() {
  const client = useQueryClient()
  const [searchParams, setSearchParams] = useSearchParams()
  const section = settingsSectionFrom(searchParams.get('tab'))
  const shippingSettings = useShippingLabelSettings()
  const labelSettings = shippingSettings.settings
  // Normalizing fills block defaults (font size, rotation) only on a second pass, so the saved copy is normalized twice
  // to compare like with like.
  const savedLabelSettings = shippingSettings.isSuccess ? normalizeShippingLabelSettings(normalizeShippingLabelSettings(shippingSettings.data.settings)) : null
  const shippingDirty = savedLabelSettings ? JSON.stringify(normalizeShippingLabelSettings(labelSettings)) !== JSON.stringify(savedLabelSettings) : shippingSettings.isError
  const [labelFormat, setLabelFormat] = useState<'a4' | 'sticker'>(() => labelSettings.defaultFormat)
  const [labelSaving, setLabelSaving] = useState(false)
  const [appearanceStatus, setAppearanceStatus] = useState({ dirty: false, themeCount: 0 })
  const [resetScopes, setResetScopes] = useState<string[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  const [resetBusy, setResetBusy] = useState(false)
  // What each list holds now, the counts the reset reports when it is done.
  const resetCounts = useQuery({ queryKey: ['data-reset-counts', 'settings'], queryFn: () => hubApi<{ items: Array<{ scope: string; count: number }> }>('/settings/data-reset'), enabled: section === 'database', staleTime: 0 })
  // Through hubApi: a session that ended shows the sign-in screen instead of an error box.
  const status = useQuery({ queryKey: ['security'], queryFn: () => hubApi<SecurityStatus>('/auth/security-status') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => hubApi<SecuritySession[]>('/auth/sessions') })
  const [sessionCount] = useRememberedTabCounts('Ayarlar oturumlar', [{ value: 'sessions', count: sessions.data?.length }])
  const [mfaStep, setMfaStep] = useState<'closed' | 'password' | 'verify' | 'recovery'>('closed')
  const [setup, setSetup] = useState<MfaSetup | null>(null)
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([])
  const [otp, setOtp] = useState<string[]>(emptyOtp)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const succeed = (message: string) => setNotice({ kind: 'success', message })
  const fail = (message: string) => setNotice({ kind: 'error', message })
  const [mfaError, setMfaError] = useState('')
  const [sessionConfirmation, setSessionConfirmation] = useState<SessionConfirmation | null>(null)
  const [sessionActionBusy, setSessionActionBusy] = useState(false)
  const totpEnabled = status.data?.totpState === 'ENABLED'
  // The current device leads the active list.
  const activeSessions = (sessions.data ?? []).filter(session => session.state === 'ACTIVE').sort((left, right) => Number(right.current) - Number(left.current))
  const activeOtherSessions = activeSessions.filter(session => !session.current)
  const closedSessions = (sessions.data ?? []).filter(session => !session.current && session.state !== 'ACTIVE')

  function selectSection(next: SettingsSectionKey) {
    setNotice(null)
    setSearchParams(next === 'auth' ? {} : { tab: next })
  }
  function toggleResetScopes(scopes: string[], checked: boolean) {
    setResetScopes(current => checked ? Array.from(new Set([...current, ...scopes])) : current.filter(value => !scopes.includes(value)))
  }
  async function resetOperationalData() {
    if (!resetScopes.length || resetConfirmation.trim() !== resetConfirmWord) return
    setResetBusy(true); setNotice(null)
    try {
      const result = await hubApi<{ products: number; orders: number; returns: number; invoices: number; categories: number; categoryAttributes: number; brands: number; options: number }>('/settings/data-reset', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ scopes: resetScopes, confirmation: resetConfirmation.trim() }) })
      succeed(`Temizlik tamamlandı: ${result.products} ürün, ${result.categories} kategori, ${result.categoryAttributes} kategori özelliği, ${result.brands} marka, ${result.options} ürün seçeneği.`)
      setResetScopes([]); setResetConfirmation('')
      await client.invalidateQueries()
    } catch (reason) { fail(reason instanceof Error ? reason.message : 'Seçili veriler sıfırlanamadı.') } finally { setResetBusy(false) }
  }

  function openMfa() {
    setNotice(null); setMfaError(''); setOtp(emptyOtp); setMfaStep('password')
  }
  async function prepareMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setMfaError('')
    const data = new FormData(event.currentTarget)
    try {
      // The setup takes the password itself (and, when an authenticator is already on, its current code).
      const enrollment = await hubApi<MfaSetup>('/auth/mfa/setup', { method: 'POST', body: JSON.stringify({ password: data.get('password') }) })
      setSetup(enrollment); setOtp(emptyOtp); setMfaStep('verify')
    } catch (reason) { setMfaError(mfaErrorMessage(reason, 'Güvenli kurulum başlatılamadı.')) } finally { setBusy(false) }
  }
  async function confirmMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const code = otp.join('')
    if (!/^[0-9]{6}$/.test(code)) {
      setMfaError('6 haneli doğrulama kodunu girin.')
      return
    }
    setBusy(true); setMfaError('')
    try {
      const result = await hubApi<{ recoveryCodes: string[] }>('/auth/mfa/confirm', { method: 'POST', body: JSON.stringify({ code }) })
      setRecoveryCodes(result.recoveryCodes); setMfaStep('recovery'); await client.invalidateQueries({ queryKey: ['security'] })
    } catch (reason) { setMfaError(mfaErrorMessage(reason, 'Doğrulayıcı etkinleştirilemedi.')) } finally { setBusy(false) }
  }
  // The recovery codes are shown once: Escape or a click outside does not close this step, only "Kodları Sakladım".
  function closeMfa() {
    if (busy) return
    if (mfaStep === 'recovery') { setMfaError('Kurtarma kodlarını sakladıktan sonra “Kodları Sakladım” ile kapatın; bu kodlar yeniden gösterilmez.'); return }
    setMfaStep('closed')
  }
  async function copyRecoveryCodes() {
    try {
      await navigator.clipboard.writeText(recoveryCodes.join('\n'))
      succeed('Kurtarma kodları panoya kopyalandı.')
    } catch { fail('Kurtarma kodları kopyalanamadı; kodları elle not alın.') }
  }
  async function confirmSessionAction() {
    const confirmation = sessionConfirmation
    if (!confirmation || sessionActionBusy) return
    setSessionActionBusy(true); setNotice(null)
    try {
      if (confirmation.action === 'revoke' && confirmation.sessionId) {
        await hubApi(`/auth/sessions/${confirmation.sessionId}/revoke`, { method: 'POST' })
        succeed('Oturum sonlandırıldı.')
      } else if (confirmation.action === 'revoke-others') {
        await hubApi('/auth/sessions/revoke-others', { method: 'POST' })
        succeed('Diğer aktif oturumlar sonlandırıldı.')
      } else if (confirmation.action === 'delete' && confirmation.sessionId) {
        await hubApi(`/auth/sessions/${confirmation.sessionId}`, { method: 'DELETE' })
        succeed('Kapalı oturum silindi.')
      } else if (confirmation.action === 'delete-closed') {
        await hubApi('/auth/sessions/closed', { method: 'DELETE' })
        succeed('Kapalı oturumlar silindi.')
      }
      await client.invalidateQueries({ queryKey: ['sessions'] })
    } catch {
      fail(confirmation.action.startsWith('delete') ? 'Kapalı oturum silinemedi.' : 'Oturumlar sonlandırılamadı.')
    } finally {
      setSessionActionBusy(false)
      setSessionConfirmation(null)
    }
  }
  async function saveLabelSettings() {
    setLabelSaving(true); setNotice(null)
    try {
      await shippingSettings.save(labelSettings)
      succeed('Kargo etiketi ayarları hesaba kaydedildi.')
    } catch (reason) {
      fail(reason instanceof Error ? reason.message : 'Kargo etiketi ayarları kaydedilemedi.')
    } finally { setLabelSaving(false) }
  }

  function navMeta(key: SettingsSectionKey) {
    if (key === 'auth') return status.data ? <SettingsGlyph state={totpEnabled ? 'done' : 'none'} /> : <svg className="ss-st" aria-hidden="true" style={{ visibility: 'hidden' }} />
    if (key === 'sessions') return sessionCount.count !== undefined ? <span className="ss-count rv-tab-count">{sessionCount.count}</span> : null
    if (key === 'database') return resetScopes.length ? <span className="ss-count rv-tab-count is-warn">{resetScopes.length}</span> : null
    if (key === 'palette') return appearanceStatus.dirty ? <SettingsGlyph state="pending" /> : null
    return null
  }
  function sessionRow(session: SecuritySession) {
    return <div key={session.id} className={`ss-ses${session.current ? ' is-current' : ''}`}>
      <span className="ss-tile"><SettingsIcon name={session.current ? 'device' : 'phone'} /></span>
      <div className="ss-ses-who"><b>{session.current ? 'Bu Cihaz' : 'Diğer Oturum'}</b><small><SettingsGlyph state={session.state === 'ACTIVE' ? 'done' : 'none'} />{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'}</small></div>
      <div className="ss-ses-when"><span>Son etkinlik {formatSettingsDate(session.lastSeenAt)}</span><small>Bitiş {formatSettingsDate(session.expiresAt)}</small></div>
      <div className="ss-ses-act">{session.current ? <span className="ss-tag is-ink is-lg"><SettingsGlyph state="done" />Mevcut Oturum</span> : session.state === 'ACTIVE' ? <button type="button" className="ss-btn is-danger" onClick={() => setSessionConfirmation({ action: 'revoke', sessionId: session.id })}><SettingsIcon name="logout" />Oturumu Sonlandır</button> : <button type="button" className="ss-btn is-danger is-quiet" onClick={() => setSessionConfirmation({ action: 'delete', sessionId: session.id })}><SettingsIcon name="trash" />Kaydı Sil</button>}</div>
    </div>
  }

  const sessionConfirmationCopy = sessionConfirmation?.action === 'revoke'
    ? { title: 'Oturumu Sonlandır', description: 'Bu cihazın bağlantısı sonlandırılacak. Devam etmek istiyor musunuz?', confirm: 'Oturumu Sonlandır' }
    : sessionConfirmation?.action === 'revoke-others'
      ? { title: 'Diğer Oturumları Kapat', description: `${activeOtherSessions.length} aktif oturum sonlandırılacak. Devam etmek istiyor musunuz?`, confirm: 'Oturumları Kapat' }
      : sessionConfirmation?.action === 'delete'
        ? { title: 'Kapalı Oturum Kaydını Sil', description: 'Bu kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?', confirm: 'Kaydı Sil' }
        : sessionConfirmation?.action === 'delete-closed'
          ? { title: 'Kapalı Oturumları Sil', description: `${closedSessions.length} kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?`, confirm: 'Kayıtları Sil' }
          : null
  const affectedSessions = !sessionConfirmation ? [] : sessionConfirmation.sessionId ? (sessions.data ?? []).filter(session => session.id === sessionConfirmation.sessionId) : sessionConfirmation.action === 'revoke-others' ? activeOtherSessions : closedSessions
  const mfaIndex = Math.max(0, mfaSteps.findIndex(step => step.key === mfaStep))
  const activeGroup = settingsSectionGroups.find(group => group.items.some(item => item.key === section)) ?? settingsSectionGroups[0]
  const isShipping = shippingSections.includes(section)
  const isAppearance = appearanceSections.includes(section)

  return <section className="content ss-page">
    {notice && <SettingsNotice {...notice} />}
    <header className="ss-page-head"><span className="ss-crumb">Ayarlar</span><h1>Sistem Ayarları</h1><p>Hesap güvenliği, yerel veri, kargo etiketi ve görünüm.</p></header>
    <div className="ss-gtabs rv-tabs rv-view-tabs" role="tablist" aria-label="Ayar grupları">{settingsSectionGroups.map(group => <button type="button" role="tab" key={group.label} aria-selected={group === activeGroup} className={group === activeGroup ? 'is-on is-active' : undefined} onClick={() => selectSection(group.items[0].key)}><span className="rv-tab-label"><SettingsIcon name={group.icon} /><span className="rv-tab-text" data-text={group.label}>{group.label}</span></span></button>)}</div>
    <div className="ss-card">
      {activeGroup.items.length > 1 && <div className="ss-stabs"><span className="ss-seg rv-tabs rv-view-tabs" role="tablist" aria-label={`${activeGroup.label} bölümleri`}>{activeGroup.items.map(item => <button type="button" role="tab" key={item.key} aria-selected={section === item.key} className={section === item.key ? 'is-on is-active' : undefined} onClick={() => selectSection(item.key)}><span className="rv-tab-label"><SettingsIcon name={item.icon} />{item.short ? <><span className="rv-tab-text ss-lbl-long" data-text={item.label}>{item.label}</span><span className="rv-tab-text ss-lbl-short" data-text={item.short}>{item.short}</span></> : <span className="rv-tab-text" data-text={item.label}>{item.label}</span>}</span>{navMeta(item.key)}</button>)}</span></div>}
      <div className="ss-pane">
        {section === 'auth' && <>
          <SettingsPaneHead section="auth" description="İki adımlı doğrulama ile girişte telefonunuzdaki kod da istenir." />
          <div className="ss-body">{status.isLoading ? <LoadingState>Güvenlik durumu yükleniyor…</LoadingState> : status.isError || !status.data ? <div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Güvenlik durumu alınamadı.</span></div> : <>
            <div className="ss-auth">
              <span className="ss-tile is-lg"><SettingsIcon name="shield" /></span>
              <h3>Authenticator {totpEnabled ? <span className="ss-tag is-ink"><SettingsGlyph state="done" />Etkin</span> : <span className="ss-tag"><SettingsGlyph state="none" />Kapalı</span>}</h3>
              <p>Giriş sırasında telefonunuzdaki tek kullanımlık kodla hesabınızı koruyun.</p>
              <div className="ss-auth-meta">{totpEnabled ? <span>Kalan kurtarma kodu <b>{status.data.recoveryCodesRemaining}</b></span> : <span>Google Authenticator, Microsoft Authenticator veya uyumlu bir uygulama gerekir.</span>}</div>
              <div className="ss-auth-side">{totpEnabled ? <span className="ss-tag is-ink is-lg" aria-label="Authenticator etkin"><SettingsGlyph state="done" />Hesabınız Korunuyor</span> : <button type="button" className="ss-btn is-primary" onClick={openMfa}><SettingsIcon name="shield" />Authenticator’ı Etkinleştir</button>}</div>
            </div>
            {!totpEnabled && <div className="ss-callout"><SettingsIcon name="info" /><span>Kurulum üç adımdır: <b>parolanızı doğrulayın</b>, <b>QR kodu tarayıp 6 haneli kodu girin</b>, <b>kurtarma kodlarını saklayın</b>.</span></div>}
          </>}</div>
        </>}
        {section === 'sessions' && <>
          <SettingsPaneHead section="sessions" description="Hesabınıza bağlı cihazları ve son etkinliklerini görüntüleyin." actions={activeOtherSessions.length || closedSessions.length ? <>{activeOtherSessions.length > 0 && <button type="button" className="ss-btn is-danger" onClick={() => setSessionConfirmation({ action: 'revoke-others' })}><span className="ss-lbl-long">Diğer Tüm Oturumları Kapat</span><span className="ss-lbl-short">Diğerlerini Kapat</span> <span className="ss-count">{activeOtherSessions.length}</span></button>}{closedSessions.length > 0 && <button type="button" className="ss-btn" onClick={() => setSessionConfirmation({ action: 'delete-closed' })}><span className="ss-lbl-long">Kapalı Oturumları Sil</span><span className="ss-lbl-short">Kapalıları Sil</span> <span className="ss-count">{closedSessions.length}</span></button>}</> : undefined} />
          <div className="ss-body is-flush">{sessions.isLoading ? <div className="ss-pad"><LoadingState>Oturumlar yükleniyor…</LoadingState></div> : sessions.isError || !sessions.data ? <div className="ss-pad"><div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Oturumlar alınamadı.</span></div></div> : <div className="ss-sess">
            <div className="ss-sess-sub">Aktif <span className="ss-count">{activeSessions.length}</span></div>
            {activeSessions.map(sessionRow)}
            {closedSessions.length ? <><div className="ss-sess-sub">Kapalı <span className="ss-count">{closedSessions.length}</span></div>{closedSessions.map(sessionRow)}</> : null}
          </div>}</div>
        </>}
        {section === 'database' && <SettingsDataReset groups={resetScopeGroups} counts={resetCounts.data ? Object.fromEntries(resetCounts.data.items.map(entry => [entry.scope, entry.count])) : undefined} countsFailed={resetCounts.isError} picked={resetScopes} onToggle={toggleResetScopes} confirmation={resetConfirmation} onConfirmation={setResetConfirmation} busy={resetBusy} onConfirm={() => void resetOperationalData()} />}
        <ShippingLabelSettingsPanel section={isShipping ? section as ShippingSection : null} settings={labelSettings} saved={savedLabelSettings} dirty={shippingDirty} saving={labelSaving} loadFailed={shippingSettings.isError} format={labelFormat} setFormat={setLabelFormat} onChange={shippingSettings.setSettings} onSave={() => void saveLabelSettings()} />
        <AppearanceSettingsSection section={isAppearance ? section as AppearanceSection : null} onStatus={setAppearanceStatus} />
      </div>
    </div>

    <Modal open={mfaStep !== 'closed'} title="Authenticator Kurulumu" description={mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın.' : mfaStep === 'verify' ? 'QR kodu uygulamanıza ekleyip üretilen kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'} className="ss-modal is-lg ss-mfa" onClose={closeMfa} footer={mfaStep === 'password' ? <><span className="ss-modal-note">Adım 1 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => setMfaStep('closed')}>Vazgeç</button><button type="submit" form="ss-mfa-password" className="ss-btn is-primary" disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam Et'}</button></> : mfaStep === 'verify' ? <><span className="ss-modal-note">Adım 2 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => { setMfaError(''); setMfaStep('password') }}><SettingsIcon name="back" />Geri</button><button type="submit" form="ss-mfa-verify" className="ss-btn is-primary" disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Etkinleştir'}</button></> : <><button type="button" className="ss-btn is-quiet" onClick={() => void copyRecoveryCodes()}><SettingsIcon name="copy" />Kopyala</button><span className="ss-modal-note" /><button type="button" className="ss-btn is-primary" onClick={() => { setMfaError(''); setMfaStep('closed') }}>Kodları Sakladım</button></>}>
      <div className="ss-mfa-split">
        <nav className="ss-mfa-rail" aria-label="Kurulum adımları">{mfaSteps.map((step, index) => <div key={step.key} className={`ss-mfa-step${index === mfaIndex ? ' is-on' : index < mfaIndex ? ' is-done' : ''}`} aria-current={index === mfaIndex ? 'step' : undefined}><SettingsGlyph state={index < mfaIndex ? 'done' : index === mfaIndex ? 'pending' : 'none'} /><span>{step.label}</span><small>{step.hint}</small></div>)}</nav>
        <div className="ss-mfa-pane">
          {mfaStep === 'password' && <form id="ss-mfa-password" className="ss-stack" onSubmit={prepareMfa}><label className="ss-fld"><small>Mevcut Parola</small><input className="ss-in" name="password" type="password" autoComplete="current-password" required data-autofocus placeholder="••••••••" /></label><p className="ss-lead">Parolanız yalnız bu kurulum için doğrulanır; kaydedilmez.</p></form>}
          {mfaStep === 'verify' && setup && <form id="ss-mfa-verify" className="ss-qrrow" onSubmit={confirmMfa}><div className="ss-qr"><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /></div><div className="ss-qr-copy"><p>QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın.</p><details className="ss-man"><summary>Kurulum Anahtarını Elle Göster</summary><code>{setup.secret ?? manualSetupKey(setup.otpauthUri)}</code></details><div className="ss-fld"><small>6 Haneli Doğrulama Kodu</small><OtpInput digits={otp} onChange={setOtp} /></div></div></form>}
          {mfaStep === 'recovery' && <><div className="ss-codes">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><div className="ss-callout is-warn"><SettingsIcon name="alert" /><span><b>Bu kodlar yalnızca bir kez gösterilir.</b> Her kod tek kullanımlıktır; telefonunuza erişemediğinizde giriş için kullanılır.</span></div></>}
          {mfaError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{mfaError}</span>}
        </div>
      </div>
    </Modal>

    {sessionConfirmationCopy && <Modal open title={sessionConfirmationCopy.title} description={sessionConfirmationCopy.description} className="ss-modal is-sm" onClose={() => { if (!sessionActionBusy) setSessionConfirmation(null) }} footer={<><button type="button" className="ss-btn" disabled={sessionActionBusy} onClick={() => setSessionConfirmation(null)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" disabled={sessionActionBusy} onClick={() => void confirmSessionAction()}>{sessionActionBusy ? 'İşlem yapılıyor…' : sessionConfirmationCopy.confirm}</button></>}>
      <div className={`ss-callout${sessionConfirmation?.action.startsWith('delete') ? '' : ' is-warn'}`}><SettingsIcon name="alert" /><span><b>Bu işlem hesap oturumlarını etkiler.</b> Devam etmeden önce yapmak istediğiniz işlemi kontrol edin.</span></div>
      {affectedSessions.length ? <div className="ss-sum">{affectedSessions.map(session => <div key={session.id}><SettingsGlyph state={session.state === 'ACTIVE' ? 'done' : 'none'} /><span>Diğer Oturum<small>Son etkinlik {formatSettingsDate(session.lastSeenAt)}</small></span><span className="ss-sum-r">Bitiş {formatSettingsDate(session.expiresAt)}</span></div>)}</div> : null}
    </Modal>}

  </section>
}

function AppearanceSettingsSection({ section, onStatus }: { section: AppearanceSection | null; onStatus: (status: { dirty: boolean; themeCount: number }) => void }) {
  const appearance = useAppearanceSettings()
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const succeed = (message: string) => setNotice({ kind: 'success', message })
  const fail = (message: string) => setNotice({ kind: 'error', message })
  const note = (message: string) => setNotice({ kind: 'info', message })
  const [busy, setBusy] = useState(false)
  const [newThemeName, setNewThemeName] = useState('')
  const [themeMenuOpen, setThemeMenuOpen] = useState(false)
  const [selectedThemeId, setSelectedThemeId] = useState(() => matchingColorThemeId(appearance.settings))
  const [themeToDelete, setThemeToDelete] = useState<AppearanceColorThemeProfile | null>(null)
  const [visualTheme, setVisualTheme] = useState<VisualTheme>(readVisualThemePreference)
  // The colour editor works on the palette of the theme being previewed.
  const colorTheme = visualTheme
  const savedAppearance = useRef(appearance.settings)
  const dirty = JSON.stringify(appearance.draft) !== JSON.stringify(appearance.settings)
  const themeCount = appearance.draft.colorThemes.length

  const reportStatus = useEffectEvent(() => onStatus({ dirty, themeCount }))
  useEffect(() => reportStatus(), [dirty, themeCount])

  useEffect(() => {
    const syncVisualTheme = (event: Event) => {
      const nextTheme = (event as CustomEvent<VisualTheme>).detail
      if (nextTheme === 'light' || nextTheme === 'dark') {
        setVisualTheme(nextTheme)
        setSelectedThemeId(nextTheme === 'light' ? 'default-light' : defaultAppearanceColorTheme.id)
      }
    }
    window.addEventListener(visualThemeChangeEvent, syncVisualTheme)
    return () => window.removeEventListener(visualThemeChangeEvent, syncVisualTheme)
  }, [])

  useEffect(() => {
    savedAppearance.current = appearance.settings
  }, [appearance.settings])

  useEffect(() => {
    const root = document.documentElement
    root.dataset.theme = visualTheme
    root.dataset.themeMode = visualTheme
    root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearance.draft.fontFamily])
    root.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearance.draft.fontSize]))
    const palette = appearance.draft.colors[visualTheme]
    appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), palette[key]))
  }, [appearance.draft, visualTheme])

  useEffect(() => () => {
    const root = document.documentElement
    const saved = savedAppearance.current
    const savedTheme: VisualTheme = localStorage.getItem('ravencia.visualTheme') === 'light' ? 'light' : 'dark'
    root.dataset.theme = savedTheme
    root.dataset.themeMode = savedTheme
    root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[saved.fontFamily])
    root.style.setProperty('--rv-font-scale', String(appearanceFontScale[saved.fontSize]))
    const palette = saved.colors[savedTheme]
    appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), palette[key]))
  }, [])

  async function save() {
    setBusy(true)
    setNotice(null)
    try {
      await appearance.save(appearance.draft)
      succeed('Görünüm ayarları hesabınıza kaydedildi.')
    } catch (reason) {
      fail(reason instanceof Error ? reason.message : 'Görünüm ayarları kaydedilemedi.')
    } finally {
      setBusy(false)
    }
  }

  function applyColorTheme(theme: typeof appearance.draft.colorThemes[number]) {
    setVisualThemePreference('dark')
    setSelectedThemeId(theme.id)
    appearance.setDraft({ ...appearance.draft, themeMode: 'dark', colors: { ...appearance.draft.colors, dark: { ...theme.palette } } })
    note(`${theme.name} önizlemeye uygulandı. Kalıcı yapmak için değişiklikleri kaydedin.`)
  }

  function applyDefaultAppearance() {
    const defaults: AppearanceSettings = {
      ...defaultAppearanceSettings,
      colors: { dark: { ...defaultAppearanceSettings.colors.dark }, light: { ...defaultAppearanceSettings.colors.light } },
      colorThemes: appearance.draft.colorThemes.map(theme => ({ ...theme, palette: { ...theme.palette } }))
    }
    setVisualThemePreference('dark')
    setSelectedThemeId(defaultAppearanceColorTheme.id)
    appearance.setDraft(defaults)
    note('Varsayılan görünüm önizlemeye uygulandı. Kalıcı yapmak için değişiklikleri kaydedin.')
  }

  function resetLightPalette() {
    appearance.setDraft({ ...appearance.draft, colors: { ...appearance.draft.colors, light: { ...defaultLightPalette } } })
    note('Açık tema renkleri varsayılana döndürüldü. Kalıcı yapmak için değişiklikleri kaydedin.')
  }

  function applyLightTheme() {
    setSelectedThemeId('default-light')
    setVisualThemePreference('light')
    note('Ravencia — Aydınlık önizlemeye uygulandı.')
  }

  async function saveColorTheme() {
    const name = newThemeName.trim()
    if (!name) {
      fail('Kaydetmek için bir tema adı yazın.')
      return
    }
    const id = `custom-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
    const profile = { id, name: name.slice(0, 60), palette: { ...palette }, builtIn: false }
    const nextDraft = { ...appearance.draft, colorThemes: [...appearance.draft.colorThemes, profile] }
    setBusy(true)
    setNotice(null)
    try {
      await appearance.save(nextDraft)
      setSelectedThemeId(id)
      setNewThemeName('')
      succeed(`“${profile.name}” renk teması kaydedildi.`)
    } catch (reason) {
      fail(reason instanceof Error ? reason.message : 'Renk teması kaydedilemedi.')
    } finally {
      setBusy(false)
    }
  }

  async function deleteColorTheme() {
    const theme = themeToDelete
    if (!theme || theme.builtIn) return
    const nextThemes = appearance.draft.colorThemes.filter(item => item.id !== theme.id)
    const nextDraft = { ...appearance.draft, colorThemes: nextThemes }
    if (selectedThemeId === theme.id) {
      setSelectedThemeId(defaultAppearanceColorTheme.id)
      nextDraft.colors = { ...nextDraft.colors, dark: { ...defaultAppearanceColorTheme.palette } }
    }
    setBusy(true)
    setNotice(null)
    try {
      await appearance.save(nextDraft)
      succeed(`“${theme.name}” renk teması silindi.`)
    } catch (reason) {
      fail(reason instanceof Error ? reason.message : 'Renk teması silinemedi.')
    } finally {
      setBusy(false)
      setThemeToDelete(null)
    }
  }

  const selectableThemes: Array<{ id: string; label: string; description: string; mode: VisualTheme; profile: AppearanceColorThemeProfile | null }> = [
    { id: 'default-light', label: 'Ravencia — Aydınlık', description: 'Açık çalışma alanı görünümü', mode: 'light', profile: null },
    { id: defaultAppearanceColorTheme.id, label: defaultAppearanceColorTheme.name, description: defaultAppearanceColorTheme.builtIn ? 'Çalışma alanının varsayılan koyu teması' : 'Kayıtlı özel tema', mode: 'dark', profile: defaultAppearanceColorTheme },
    { id: grafitAppearanceColorTheme.id, label: grafitAppearanceColorTheme.name, description: 'Önceki varsayılan koyu tema', mode: 'dark', profile: grafitAppearanceColorTheme },
    ...appearance.draft.colorThemes.filter(theme => !theme.builtIn).map(theme => ({ id: theme.id, label: theme.name, description: 'Kayıtlı özel tema', mode: 'dark' as const, profile: theme }))
  ]
  const activeSelectableTheme = selectableThemes.find(theme => theme.id === (visualTheme === 'light' ? 'default-light' : selectedThemeId)) ?? selectableThemes[0]
  const activeThemeLabel = visualTheme === 'light' ? 'Ravencia — Aydınlık' : appearance.draft.colorThemes.find(theme => theme.id === selectedThemeId)?.name ?? defaultAppearanceColorTheme.name
  const palette = appearance.draft.colors[colorTheme]
  const previewStyle = {
    fontFamily: appearanceFontFamilyCss[appearance.draft.fontFamily],
    fontSize: `${(14 * appearanceFontScale[appearance.draft.fontSize]).toFixed(1)}px`,
    ...Object.fromEntries(appearanceColorTokenOptions.map(({ key }) => [`--p-${key}`, palette[key]]))
  } as CSSProperties
  function updateColor(key: typeof appearanceColorTokenOptions[number]['key'], value: string) {
    appearance.setDraft({ ...appearance.draft, colors: { ...appearance.draft.colors, [colorTheme]: { ...appearance.draft.colors[colorTheme], [key]: value } } })
  }
  const themePalette = (theme: typeof selectableThemes[number]) => theme.mode === 'light' ? appearance.draft.colors.light : theme.id === activeSelectableTheme.id ? appearance.draft.colors.dark : theme.profile?.palette ?? appearance.draft.colors.dark

  const preview = <div className="ss-pv" style={previewStyle}><small>Önizleme</small><div className="ss-pv-card"><strong>Ravencia MarketplaceHub</strong><p>Bu ayar sipariş, iade, ürün ve diğer çalışma ekranlarındaki metinleri etkiler.</p><div className="ss-pv-line"><span>#1972215187 · Fehime Mat</span><span>16.10.2026 18:36</span></div><div className="ss-pv-row"><span className="ss-pv-btn">Kaydet</span><span className="ss-pv-btn2">Vazgeç</span><span className="ss-pv-tag is-sel">Seçili</span><span className="ss-pv-tag is-ok">Teslim Edildi</span><span className="ss-pv-tag is-warn">Gecikiyor</span><span className="ss-pv-tag is-bad">Hata</span></div></div></div>
  const bar = <SettingsBar title="Görünüm Ayarlarını Uygula" dirty={dirty} copy={dirty ? 'Önizlemedesiniz · kaydedilmemiş değişiklikler var' : 'Değişiklikleri hesabınıza kaydetmeden önce önizleyin.'}><button type="button" className="ss-btn" onClick={applyDefaultAppearance} disabled={busy}><SettingsIcon name="reset" /><span className="ss-lbl-long">Varsayılanlara Dön</span><span className="ss-lbl-short">Varsayılan</span></button><button type="button" className="ss-btn is-primary" onClick={() => void save()} disabled={busy || !dirty}><SettingsIcon name="save" />{busy ? 'Kaydediliyor…' : <><span className="ss-lbl-long">Görünüm Ayarlarını Kaydet</span><span className="ss-lbl-short">Kaydet</span></>}</button></SettingsBar>
  const loadError = appearance.isError ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap görünüm ayarları alınamadı; varsayılan görünüm gösteriliyor.</span></div> : null

  return <>
    {notice && <SettingsNotice {...notice} />}
    {section === 'read' && <>
      <SettingsPaneHead section="read" description="Tablo ve işlem ekranlarındaki yazıları hesabınız için özelleştirin." />
      <div className="ss-body">
        {loadError}
        <div className="ss-app-grid">
          <label className="ss-fld"><small>Font Tipi</small><select className="ss-in" value={appearance.draft.fontFamily} onChange={event => appearance.setDraft({ ...appearance.draft, fontFamily: event.target.value as AppearanceSettings['fontFamily'] })}>{appearanceFontFamilyOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></label>
          <label className="ss-fld"><small>Yazı Boyutu</small><select className="ss-in" value={appearance.draft.fontSize} onChange={event => appearance.setDraft({ ...appearance.draft, fontSize: event.target.value as AppearanceSettings['fontSize'] })}>{appearanceFontSizeOptions.map(option => <option key={option.value} value={option.value}>{option.label} — {option.description}</option>)}</select></label>
          <div className="ss-fld"><small>Tema</small><div className="ss-picker" onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setThemeMenuOpen(false) }}>
            <button type="button" className={`ss-pick-btn${themeMenuOpen ? ' is-on' : ''}`} aria-label="Arayüz teması" aria-haspopup="listbox" aria-expanded={themeMenuOpen} onClick={() => setThemeMenuOpen(value => !value)} onKeyDown={event => { if (event.key === 'Escape') setThemeMenuOpen(false) }}><SettingsSwatches palette={themePalette(activeSelectableTheme)} /><span>{activeSelectableTheme.label} <small>— {activeSelectableTheme.description}</small></span><SettingsIcon name="chev" /></button>
            {themeMenuOpen && <div className="ss-pick-menu" role="listbox" aria-label="Arayüz teması seçenekleri">{selectableThemes.map((theme, index) => <Fragment key={theme.id}>{index === 1 ? <div className="ss-pick-sep" /> : null}<button type="button" role="option" aria-selected={theme.id === activeSelectableTheme.id} className={`ss-pick-opt${theme.id === activeSelectableTheme.id ? ' is-on' : ''}`} onMouseDown={event => event.preventDefault()} onClick={() => { if (theme.mode === 'light') applyLightTheme(); else if (theme.profile) applyColorTheme(theme.profile); setThemeMenuOpen(false) }}><SettingsSwatches palette={themePalette(theme)} /><span><b>{theme.label}</b><small>{theme.description}</small></span>{theme.id === activeSelectableTheme.id ? <SettingsGlyph state="done" /> : <span />}</button></Fragment>)}</div>}
          </div></div>
        </div>
        {preview}
      </div>
      {bar}
    </>}
    {section === 'palette' && <>
      <SettingsPaneHead section="palette" description={visualTheme === 'light' ? 'Açık tema renklerini düzenleyin.' : 'Değişiklikler kaydetmeden önce anlık önizlenir.'} actions={<><span className="ss-tag is-ink">{activeThemeLabel}{dirty ? ' · Düzenlendi' : ''}</span>{visualTheme === 'light' && <button type="button" className="ss-btn" onClick={resetLightPalette}><SettingsIcon name="reset" />Varsayılana Döndür</button>}</>} />
      <div className="ss-body">
        {loadError}
        <div className="ss-cgrid">{appearanceColorTokenOptions.map(({ key, label, description }) => <div className="ss-cf" key={key}><span><b>{label}</b><small>{description}</small></span><label className="ss-cctl"><input type="color" value={palette[key]} onChange={event => updateColor(key, event.target.value)} aria-label={`${label} rengi`} /><code>{palette[key].toUpperCase()}</code></label></div>)}</div>
      </div>
      {bar}
    </>}
    {section === 'themes' && <>
      <SettingsPaneHead section="themes" description="Hazır paletleri saklayın, daha sonra yeniden uygulayın. Varsayılan tema korunur ve silinemez." />
      <div className="ss-body">
        {loadError}
        <div className="ss-th-new"><input className="ss-in is-sm" value={newThemeName} maxLength={60} placeholder="Yeni tema adı" aria-label="Yeni tema adı" onChange={event => setNewThemeName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && newThemeName.trim() && !busy) void saveColorTheme() }} /><button type="button" className="ss-btn is-primary" disabled={busy || !newThemeName.trim()} onClick={() => void saveColorTheme()}><SettingsIcon name="plus" />Renk Temasını Kaydet</button></div>
        <div className="ss-themes">{appearance.draft.colorThemes.map(theme => <div key={theme.id} className={`ss-th${visualTheme === 'dark' && selectedThemeId === theme.id ? ' is-on' : ''}`}><button type="button" className="ss-th-pick" onClick={() => applyColorTheme(theme)}><SettingsSwatches palette={theme.palette} large /><span><b>{theme.name}</b><small>{theme.builtIn ? 'Varsayılan tema' : 'Kayıtlı özel tema'}</small></span></button>{theme.builtIn ? <span className="ss-th-prot" title="Varsayılan tema korunur ve silinemez" aria-label="Korunuyor"><SettingsIcon name="lock" /></span> : <button type="button" className="ss-btn is-danger is-quiet is-xs" disabled={busy} onClick={() => setThemeToDelete(theme)} aria-label={`${theme.name} temasını sil`}>Sil</button>}</div>)}</div>
      </div>
      {bar}
    </>}
    {section === 'notify' && <>
      <SettingsPaneHead section="notify" description="Sağ üstte çıkan bildirimlerin seçili temadaki görünümünü deneyin." />
      <div className="ss-body"><NotificationSamples />{preview}</div>
    </>}
    <Modal open={Boolean(themeToDelete)} title="Renk Temasını Sil" description={themeToDelete ? `“${themeToDelete.name}” renk teması kalıcı olarak silinecek. Devam etmek istiyor musunuz?` : undefined} className="ss-modal is-sm" onClose={() => { if (!busy) setThemeToDelete(null) }} footer={<><button type="button" className="ss-btn" disabled={busy} onClick={() => setThemeToDelete(null)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" disabled={busy} onClick={() => void deleteColorTheme()}>{busy ? 'Siliniyor…' : 'Temayı Sil'}</button></>}>
      {themeToDelete && <><div className="ss-th"><SettingsSwatches palette={themeToDelete.palette} large /><span><b>{themeToDelete.name}</b><small>Kayıtlı özel tema{selectedThemeId === themeToDelete.id ? ' · şu an seçili' : ''}</small></span></div><div className="ss-callout"><SettingsIcon name="alert" /><span><b>Bu işlem geri alınamaz.</b> Seçili tema silinirse görünüm varsayılan koyu temaya döner.</span></div></>}
    </Modal>
  </>
}

type ShippingDesignerTab = 'general' | 'text' | 'barcode'
type ShippingResizeDirection = 'n' | 'e' | 's' | 'w'
type ShippingRotateDirection = 'nw' | 'ne' | 'se' | 'sw'
type ShippingLabelTemplate = { id: string; name: string; savedAt: string; settings: ShippingLabelSettings }
const shippingLabelTemplateStorageKey = 'ravencia.shippingLabelTemplates'
const shippingResizeDirections: ShippingResizeDirection[] = ['n', 'e', 's', 'w']
const shippingRotateDirections: ShippingRotateDirection[] = ['nw', 'ne', 'se', 'sw']
const shippingResizeDirectionLabels: Record<ShippingResizeDirection, string> = { n: 'üst kenar', e: 'sağ kenar', s: 'alt kenar', w: 'sol kenar' }
const shippingRotateDirectionLabels: Record<ShippingRotateDirection, string> = { nw: 'sol üst köşe', ne: 'sağ üst köşe', se: 'sağ alt köşe', sw: 'sol alt köşe' }

function loadShippingLabelTemplates(): ShippingLabelTemplate[] {
  try {
    const value = JSON.parse(localStorage.getItem(shippingLabelTemplateStorageKey) ?? '[]')
    return Array.isArray(value) ? value.filter(item => item && typeof item.id === 'string' && typeof item.name === 'string' && item.settings) : []
  } catch { return [] }
}

const shippingBlockLabels: Record<ShippingLabelBlockKind, string> = { trackingBarcode: 'Takip Barkodu', address: 'Teslimat Adresi', orderInfo: 'Paket / Sipariş Bilgileri', packageBarcode: 'Paket Barkodu', sender: 'Gönderici ve Kargo Bilgileri', custom: 'Özel İçerik Bloğu' }
const shippingAlignOptions = [['left', 'Sol'], ['center', 'Orta'], ['right', 'Sağ']] as const

function ShippingLabelSettingsPanel({ section, settings, saved, dirty, saving, loadFailed, format, setFormat, onChange, onSave }: { section: ShippingSection | null; settings: ShippingLabelSettings; saved: ShippingLabelSettings | null; dirty: boolean; saving: boolean; loadFailed: boolean; format: 'a4' | 'sticker'; setFormat: (format: 'a4' | 'sticker') => void; onChange: (value: ShippingLabelSettings) => void; onSave: () => void }) {
  const [selectedId, setSelectedId] = useState<string | null>(settings.layout[format][0]?.id ?? null)
  const [designerTab, setDesignerTab] = useState<ShippingDesignerTab>('general')
  const [customTitle, setCustomTitle] = useState('')
  const [customText, setCustomText] = useState('')
  const [templates, setTemplates] = useState<ShippingLabelTemplate[]>(() => loadShippingLabelTemplates())
  const [selectedTemplateId, setSelectedTemplateId] = useState('')
  const [templateNameDraft, setTemplateNameDraft] = useState('')
  const [templateNameOpen, setTemplateNameOpen] = useState(false)
  const [templateNotice, setTemplateNotice] = useState<SettingsNoticeState | null>(null)
  const [templateDeleteOpen, setTemplateDeleteOpen] = useState(false)
  const [layoutResetOpen, setLayoutResetOpen] = useState(false)
  const pointerDrag = useRef<{ id: string; offsetX: number; offsetY: number } | null>(null)
  const pointerResize = useRef<{ id: string; direction: ShippingResizeDirection; startClientX: number; startClientY: number; position: ReturnType<typeof positionFor> } | null>(null)
  const pointerRotate = useRef<{ id: string; startAngle: number; startRotation: number } | null>(null)
  const [stickerWidthDraft, setStickerWidthDraft] = useState(() => String(settings.stickerWidthMm))
  const [stickerHeightDraft, setStickerHeightDraft] = useState(() => String(settings.stickerHeightMm))
  const [fontSizeDraft, setFontSizeDraft] = useState('')
  const layout = settings.layout[format]
  const activeBlock = layout.find(block => block.id === selectedId) ?? layout[0] ?? null
  const selectedTemplate = templates.find(item => item.id === selectedTemplateId)

  const selectFirstBlock = useEffectEvent(() => setSelectedId(settings.layout[format][0]?.id ?? null))
  useEffect(() => selectFirstBlock(), [format])
  useEffect(() => { setStickerWidthDraft(String(settings.stickerWidthMm)) }, [settings.stickerWidthMm])
  useEffect(() => { setStickerHeightDraft(String(settings.stickerHeightMm)) }, [settings.stickerHeightMm])
  const showBlockFontSize = useEffectEvent(() => setFontSizeDraft(activeBlock ? String(activeBlock.fontSize ?? 14) : ''))
  useEffect(() => showBlockFontSize(), [format, activeBlock?.id])

  function update<K extends keyof ShippingLabelSettings>(key: K, value: ShippingLabelSettings[K]) {
    onChange({ ...settings, [key]: value })
  }
  function updateBlock(blockId: string, patch: Partial<ShippingLabelBlock>) {
    onChange({ ...settings, layout: { ...settings.layout, [format]: layout.map(block => block.id === blockId ? { ...block, ...patch } : block) } })
  }
  function rotateActiveBlock(delta: number) {
    if (!activeBlock) return
    rotateBlock(activeBlock, delta)
  }
  function rotateBlock(block: ShippingLabelBlock, delta: number) {
    const current = block.rotation ?? 0
    updateBlock(block.id, { rotation: (current + delta + 360) % 360 })
  }
  function positionFor(block: ShippingLabelBlock, index: number) {
    return block.position ?? defaultShippingLabelBlockPosition(block.kind, index)
  }
  const snapStep = 2.5
  function snapPosition(value: number, maximum: number) { return Math.min(maximum, Math.max(0, Math.round(value / snapStep) * snapStep)) }
  function snapPositionBetween(value: number, minimum: number, maximum: number) { return Math.min(maximum, Math.max(minimum, Math.round(value / snapStep) * snapStep)) }
  function beginPointerDrag(event: ReactPointerEvent<HTMLElement>, block: ShippingLabelBlock) {
    if (event.button !== 0) return
    const blockRect = event.currentTarget.getBoundingClientRect()
    pointerDrag.current = { id: block.id, offsetX: event.clientX - blockRect.left, offsetY: event.clientY - blockRect.top }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
    event.preventDefault()
  }
  function movePointerDrag(event: ReactPointerEvent<HTMLElement>) {
    const drag = pointerDrag.current
    if (!drag) return
    const paperRect = event.currentTarget.parentElement?.getBoundingClientRect()
    const block = layout.find(item => item.id === drag.id)
    if (!paperRect || !block) return
    const position = positionFor(block, layout.indexOf(block))
    const x = ((event.clientX - paperRect.left - drag.offsetX) / paperRect.width) * 100
    const y = ((event.clientY - paperRect.top - drag.offsetY) / paperRect.height) * 100
    // A rotated block is turned around its centre, so the room it needs on the paper is its turned outline, not its own box.
    const radians = ((block.rotation ?? 0) * Math.PI) / 180
    const widthPx = (position.width / 100) * paperRect.width
    const heightPx = (position.height / 100) * paperRect.height
    const outlineWidth = (Math.abs(widthPx * Math.cos(radians)) + Math.abs(heightPx * Math.sin(radians))) / paperRect.width * 100
    const outlineHeight = (Math.abs(widthPx * Math.sin(radians)) + Math.abs(heightPx * Math.cos(radians))) / paperRect.height * 100
    const minX = (outlineWidth - position.width) / 2
    const minY = (outlineHeight - position.height) / 2
    updateBlock(block.id, { position: { ...position, x: snapPositionBetween(x, minX, 100 - position.width - minX), y: snapPositionBetween(y, minY, 100 - position.height - minY) } })
  }
  function beginPointerResize(event: ReactPointerEvent<HTMLButtonElement>, block: ShippingLabelBlock, direction: ShippingResizeDirection) {
    if (event.button !== 0) return
    event.preventDefault()
    event.stopPropagation()
    const paper = event.currentTarget.closest<HTMLElement>('.shipping-designer-paper')
    if (!paper) return
    pointerResize.current = { id: block.id, direction, startClientX: event.clientX, startClientY: event.clientY, position: positionFor(block, layout.indexOf(block)) }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
  }
  function beginPointerRotate(event: ReactPointerEvent<HTMLButtonElement>, block: ShippingLabelBlock) {
    if (event.button !== 0) return
    event.preventDefault()
    event.stopPropagation()
    const blockElement = event.currentTarget.closest<HTMLElement>('.shipping-designer-block')
    if (!blockElement) return
    const rect = blockElement.getBoundingClientRect()
    const startAngle = Math.atan2(event.clientY - (rect.top + rect.height / 2), event.clientX - (rect.left + rect.width / 2)) * 180 / Math.PI
    pointerRotate.current = { id: block.id, startAngle, startRotation: block.rotation ?? 0 }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
  }
  function movePointerRotate(event: ReactPointerEvent<HTMLElement>) {
    const rotate = pointerRotate.current
    if (!rotate) return
    const blockElement = event.currentTarget.closest<HTMLElement>('.shipping-designer-block')
    const block = layout.find(item => item.id === rotate.id)
    if (!blockElement || !block) return
    const rect = blockElement.getBoundingClientRect()
    const angle = Math.atan2(event.clientY - (rect.top + rect.height / 2), event.clientX - (rect.left + rect.width / 2)) * 180 / Math.PI
    let delta = angle - rotate.startAngle
    if (delta > 180) delta -= 360
    if (delta < -180) delta += 360
    const nextRotation = ((Math.round(rotate.startRotation + delta) % 360) + 360) % 360
    updateBlock(block.id, { rotation: nextRotation })
  }
  function movePointerResize(event: ReactPointerEvent<HTMLElement>) {
    const resize = pointerResize.current
    if (!resize) return
    const paper = event.currentTarget.closest<HTMLElement>('.shipping-designer-paper')
    if (!paper) return
    const rect = paper.getBoundingClientRect()
    const deltaX = ((event.clientX - resize.startClientX) / rect.width) * 100
    const deltaY = ((event.clientY - resize.startClientY) / rect.height) * 100
    const minimumWidth = 8
    const minimumHeight = 5
    const nextPosition = { ...resize.position }
    if (resize.direction.includes('w')) {
      const nextX = snapPosition(resize.position.x + deltaX, resize.position.x + resize.position.width - minimumWidth)
      nextPosition.x = nextX
      nextPosition.width = resize.position.x + resize.position.width - nextX
    } else if (resize.direction.includes('e')) {
      nextPosition.width = Math.max(minimumWidth, snapPosition(resize.position.width + deltaX, 100 - resize.position.x))
    }
    if (resize.direction.includes('n')) {
      const nextY = snapPosition(resize.position.y + deltaY, resize.position.y + resize.position.height - minimumHeight)
      nextPosition.y = nextY
      nextPosition.height = resize.position.y + resize.position.height - nextY
    } else if (resize.direction.includes('s')) {
      nextPosition.height = Math.max(minimumHeight, snapPosition(resize.position.height + deltaY, 100 - resize.position.y))
    }
    updateBlock(resize.id, { position: nextPosition })
  }
  function endPointerInteraction() { pointerDrag.current = null; pointerResize.current = null; pointerRotate.current = null }
  // Keyboard move of the focused block: an arrow key moves it 1% of the paper, with Shift 5%. Like a pointer drag it
  // stays on the paper, counting a turned block's outline (worked out from the paper's own proportions).
  function moveBlockByKey(event: ReactKeyboardEvent<HTMLElement>, block: ShippingLabelBlock, index: number) {
    if (event.target !== event.currentTarget || event.altKey || event.ctrlKey || event.metaKey) return
    const step = event.shiftKey ? 5 : 1
    const delta = ({ ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] } as Record<string, [number, number]>)[event.key]
    if (!delta) return
    event.preventDefault()
    const position = positionFor(block, index)
    const heightPerWidth = format === 'a4' ? 297 / 210 : settings.stickerHeightMm / settings.stickerWidthMm
    const radians = ((block.rotation ?? 0) * Math.PI) / 180
    const cos = Math.abs(Math.cos(radians))
    const sin = Math.abs(Math.sin(radians))
    const outlineWidth = position.width * cos + position.height * heightPerWidth * sin
    const outlineHeight = position.width / heightPerWidth * sin + position.height * cos
    const minX = (outlineWidth - position.width) / 2
    const minY = (outlineHeight - position.height) / 2
    const within = (value: number, minimum: number, maximum: number) => Math.round(Math.min(Math.max(minimum, maximum), Math.max(minimum, value)) * 10) / 10
    updateBlock(block.id, { position: { ...position, x: within(position.x + delta[0], minX, 100 - position.width - minX), y: within(position.y + delta[1], minY, 100 - position.height - minY) } })
  }
  function saveTemplate(name: string) {
    const trimmed = name.trim()
    if (!trimmed) {
      setTemplateNotice({ kind: 'error', message: 'Şablon adı gerekli.' })
      return
    }
    const template: ShippingLabelTemplate = { id: `template-${Date.now()}`, name: trimmed.slice(0, 80), savedAt: new Date().toISOString(), settings: JSON.parse(JSON.stringify(settings)) as ShippingLabelSettings }
    const next = [...templates.filter(item => item.name !== template.name), template]
    try {
      localStorage.setItem(shippingLabelTemplateStorageKey, JSON.stringify(next))
      setTemplates(next)
      setSelectedTemplateId(template.id)
      setTemplateNameOpen(false)
      setTemplateNotice({ kind: 'success', message: `“${template.name}” şablonu kaydedildi.` })
    } catch {
      setTemplateNotice({ kind: 'error', message: 'Şablon kaydedilemedi. Tarayıcı depolamasına erişim izni verin.' })
    }
  }
  function applyTemplate(id: string) {
    setSelectedTemplateId(id)
    const template = templates.find(item => item.id === id)
    if (template) {
      const nextSettings = JSON.parse(JSON.stringify(template.settings)) as ShippingLabelSettings
      const nextLayout = nextSettings.layout[format]
      setSelectedId(nextLayout[0]?.id ?? null)
      setFontSizeDraft(nextLayout[0] ? String(nextLayout[0].fontSize ?? 14) : '')
      onChange(nextSettings)
    }
  }
  function deleteTemplate() {
    if (!selectedTemplateId) return
    const next = templates.filter(item => item.id !== selectedTemplateId)
    setTemplates(next)
    setSelectedTemplateId('')
    try { localStorage.setItem(shippingLabelTemplateStorageKey, JSON.stringify(next)) } catch { /* Private browsing may disallow local storage. */ }
  }
  function commitDimension(key: 'stickerWidthMm' | 'stickerHeightMm', value: string, fallback: number) {
    const parsed = Number(value)
    const next = Number.isFinite(parsed) && value.trim() ? Math.min(300, Math.max(40, parsed)) : fallback
    if (key === 'stickerWidthMm') setStickerWidthDraft(String(next)); else setStickerHeightDraft(String(next))
    update(key, next)
  }
  function resetLayout() {
    const next = defaultShippingLabelSettings.layout[format].map(block => ({ ...block, fields: [...block.fields], position: block.position ? { ...block.position } : undefined }))
    onChange({ ...settings, layout: { ...settings.layout, [format]: next } })
    setSelectedId(next[0]?.id ?? null)
  }
  function toggleField(block: ShippingLabelBlock, field: ShippingLabelField) {
    updateBlock(block.id, { fields: block.fields.includes(field) ? block.fields.filter(value => value !== field) : [...block.fields, field] })
  }
  function removeBlock(blockId: string) {
    const next = layout.filter(block => block.id !== blockId)
    onChange({ ...settings, layout: { ...settings.layout, [format]: next } })
    setSelectedId(next[0]?.id ?? null)
  }
  function addBlock(kind: ShippingLabelBlockKind, x?: number, y?: number) {
    const catalog = shippingLabelBlockCatalog.find(block => block.kind === kind)
    if (!catalog) return null
    const idPrefix = kind === 'custom' ? `custom-${Date.now()}` : kind
    let id = idPrefix
    let suffix = 2
    while (layout.some(block => block.id === id)) id = `${idPrefix}-${suffix++}`
    const fallbackPosition = defaultShippingLabelBlockPosition(kind, layout.length)
    const matchingBlockCount = layout.filter(block => block.kind === kind).length
    const block: ShippingLabelBlock = {
      id,
      kind,
      title: kind === 'custom' ? customTitle.trim() || 'Özel içerik' : catalog.label,
      fields: [...catalog.fields],
      align: kind === 'trackingBarcode' || kind === 'packageBarcode' ? 'center' : 'left',
      text: kind === 'custom' ? customText : '',
      position: { x: Math.min(92, Math.max(0, x ?? fallbackPosition.x)), y: Math.min(92, Math.max(0, y ?? fallbackPosition.y + matchingBlockCount * (fallbackPosition.height + 2))), width: kind === 'trackingBarcode' || kind === 'packageBarcode' ? 84 : 80, height: kind === 'address' ? 25 : kind === 'custom' ? 14 : 13 }
    }
    onChange({ ...settings, layout: { ...settings.layout, [format]: [...layout, block] } })
    setSelectedId(block.id)
    if (kind === 'custom') { setCustomTitle(''); setCustomText('') }
    return block
  }
  function dragStart(event: DragEvent<HTMLElement>, value: string) {
    event.dataTransfer.setData('application/x-ravencia-label', value)
    event.dataTransfer.effectAllowed = value.startsWith('block:') ? 'move' : 'copy'
  }
  function dropOnCanvas(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    const value = event.dataTransfer.getData('application/x-ravencia-label')
    if (!value) return
    const rect = event.currentTarget.getBoundingClientRect()
    const pointX = ((event.clientX - rect.left) / rect.width) * 100
    const pointY = ((event.clientY - rect.top) / rect.height) * 100
    if (value.startsWith('block:')) {
      const blockId = value.slice(6)
      const block = layout.find(item => item.id === blockId)
      if (!block) return
      const position = positionFor(block, layout.indexOf(block))
      updateBlock(block.id, { position: { ...position, x: snapPosition(pointX - position.width / 2, 100 - position.width), y: snapPosition(pointY - position.height / 2, 100 - position.height) } })
      setSelectedId(block.id)
      return
    }
    const kind = value.replace('catalog:', '') as ShippingLabelBlockKind
    const repeatableBarcode = kind === 'trackingBarcode' || kind === 'packageBarcode'
    const existing = layout.find(block => block.kind === kind)
    if (existing && kind !== 'custom' && !repeatableBarcode) {
      const position = positionFor(existing, layout.indexOf(existing))
      updateBlock(existing.id, { position: { ...position, x: snapPosition(pointX - position.width / 2, 100 - position.width), y: snapPosition(pointY - position.height / 2, 100 - position.height) } })
      setSelectedId(existing.id)
      return
    }
    addBlock(kind, snapPosition(pointX - 40, 100 - 80), snapPosition(pointY - 7, 100 - 14))
  }
  const blockTitle = (block: ShippingLabelBlock) => block.kind === 'custom' ? block.title : shippingBlockLabels[block.kind]
  const alignSegment = (block: ShippingLabelBlock) => <div className="ss-seg3" role="group" aria-label="Hizalama">{shippingAlignOptions.map(([value, label]) => <button type="button" key={value} aria-pressed={block.align === value} className={block.align === value ? 'is-on' : undefined} onClick={() => updateBlock(block.id, { align: value })}>{label}</button>)}</div>
  const bar = <SettingsBar title="Etiket Düzenini Yayınla" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : 'Gönderici bilgileri ve kâğıt yerleşimi tüm yazdırma akışlarında kullanılır.'}>{dirty && saved ? <button type="button" className="ss-btn" disabled={saving} onClick={() => onChange(saved)}>Vazgeç</button> : null}<button type="button" className="ss-btn is-primary" disabled={!dirty || saving} onClick={onSave}><SettingsIcon name="save" />{saving ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button></SettingsBar>
  const loadError = loadFailed ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap kargo ayarları alınamadı; geçici yerel ayarlar gösteriliyor.</span></div> : null

  return <>
    {templateNotice && <SettingsNotice {...templateNotice} />}
    {section === 'sender' && <>
      <SettingsPaneHead section="sender" description="Etiketin üzerinde görünen gönderici ve varsayılan çıktı." />
      <div className="ss-body">
        {loadError}
        <div className="ss-pair"><label className="ss-fld"><small>Gönderici Adı</small><input className="ss-in" value={settings.senderName} maxLength={120} onChange={event => update('senderName', event.target.value)} /></label><label className="ss-fld"><small>Varsayılan Çıktı</small><select className="ss-in" value={settings.defaultFormat} onChange={event => update('defaultFormat', event.target.value as ShippingLabelSettings['defaultFormat'])}><option value="a4">Kargo Etiketi · A4</option><option value="sticker">Kargo Etiketi · Sticker</option></select></label></div>
        <label className="ss-fld"><small>Gönderici Adresi</small><textarea className="ss-in" value={settings.senderAddress} maxLength={500} rows={3} onChange={event => update('senderAddress', event.target.value)} placeholder="İsteğe bağlı" /></label>
        <label className="ss-fld"><small>A4 Sayfa Düzeni</small><select className="ss-in" value={settings.a4LabelsPerPage} onChange={event => update('a4LabelsPerPage', Number(event.target.value) as ShippingLabelSettings['a4LabelsPerPage'])}><option value={1}>Sayfada 1 Etiket</option><option value={2}>Sayfada 2 Etiket</option><option value={4}>Sayfada 4 Etiket</option></select></label>
        <div className="ss-pair"><SettingsSwitch label="A4 Yazdırma Butonunu Göster" checked={settings.showA4Button} onChange={checked => update('showA4Button', checked)} /><SettingsSwitch label="Sticker Yazdırma Butonunu Göster" checked={settings.showStickerButton} onChange={checked => update('showStickerButton', checked)} /></div>
      </div>
      {bar}
    </>}
    {section === 'dims' && <>
      <SettingsPaneHead section="dims" description="Sticker boyutu ve alan aralıkları." />
      <div className="ss-body">
        {loadError}
        <div className="ss-pair"><label className="ss-fld"><small>Sticker Genişliği (mm)</small><input className="ss-in" type="number" min={40} max={300} value={stickerWidthDraft} onChange={event => setStickerWidthDraft(event.target.value)} onBlur={() => commitDimension('stickerWidthMm', stickerWidthDraft, settings.stickerWidthMm)} /></label><label className="ss-fld"><small>Sticker Yüksekliği (mm)</small><input className="ss-in" type="number" min={40} max={300} value={stickerHeightDraft} onChange={event => setStickerHeightDraft(event.target.value)} onBlur={() => commitDimension('stickerHeightMm', stickerHeightDraft, settings.stickerHeightMm)} /></label></div>
        <div className="ss-pair"><label className="ss-fld"><small>Bloklar Arası Boşluk (mm)</small><input className="ss-in" type="number" min={0} max={20} value={settings.sectionGapMm} onChange={event => update('sectionGapMm', Math.min(20, Math.max(0, Number(event.target.value) || 0)))} /></label><SettingsSwitch label="Müşteri İletişim Alanını Göster" checked={settings.showCustomerPhone} onChange={checked => update('showCustomerPhone', checked)} /></div>
      </div>
      {bar}
    </>}
    {section === 'designer' && <>
      <SettingsPaneHead section="designer" description="Alanları kütüphaneden kâğıda sürükleyin; seçili alanı sağ panelden düzenleyin." />
      <div className="ss-body is-flush"><div className="ss-dz">
        <div className="ss-dz-bar">
          <span className="ss-seg2 rv-tabs rv-view-tabs" role="tablist" aria-label="Kâğıt biçimi"><button type="button" role="tab" aria-selected={format === 'a4'} className={format === 'a4' ? 'is-on is-active' : undefined} onClick={() => setFormat('a4')}><span className="rv-tab-label"><SettingsIcon name="copy" /><span className="rv-tab-text" data-text="A4">A4</span> <small>{settings.a4LabelsPerPage} Etiket</small></span></button><button type="button" role="tab" aria-selected={format === 'sticker'} className={format === 'sticker' ? 'is-on is-active' : undefined} onClick={() => setFormat('sticker')}><span className="rv-tab-label"><SettingsIcon name="barcode" /><span className="rv-tab-text" data-text="Sticker">Sticker</span> <small>{settings.stickerWidthMm} × {settings.stickerHeightMm} mm</small></span></button></span>
          <span className="ss-grow" />
          <select className="ss-in is-sm ss-dz-template" aria-label="Kayıtlı şablon seçin" value={selectedTemplateId} onChange={event => applyTemplate(event.target.value)}><option value="">Şablon Seçin…</option>{templates.map(template => <option key={template.id} value={template.id}>{template.name}</option>)}</select>
          <button type="button" className="ss-btn" onClick={() => { setTemplateNameDraft(`${format === 'a4' ? 'A4' : 'Sticker'} şablonu`); setTemplateNotice(null); setTemplateNameOpen(true) }}><SettingsIcon name="save" />Şablon Kaydet</button>
          <button type="button" className="ss-btn" disabled={!selectedTemplateId} onClick={() => setTemplateDeleteOpen(true)}><SettingsIcon name="trash" />Şablonu Sil</button>
          <button type="button" className="ss-btn is-quiet" onClick={() => setLayoutResetOpen(true)}><SettingsIcon name="reset" />Varsayılanı Yükle</button>
        </div>
        <div className="ss-dz-work">
          <aside className="ss-dz-lib">
            <div className="ss-dz-head"><b>İçerik Kütüphanesi</b><small>Sürükleyip kâğıda bırakın veya tıklayıp ekleyin</small></div>
            <div className="ss-lib-list">{shippingLabelBlockCatalog.map(item => { const repeatableBarcode = item.kind === 'trackingBarcode' || item.kind === 'packageBarcode'; return <button type="button" key={item.kind} className="ss-lib-it" draggable onDragStart={event => dragStart(event, `catalog:${item.kind}`)} onClick={() => addBlock(item.kind)}><span className="ss-tile is-sm"><SettingsIcon name={repeatableBarcode ? 'barcode' : item.kind === 'custom' ? 'sparkle' : 'layout'} /></span><span><b>{shippingBlockLabels[item.kind]}</b><small>{item.description} · birden fazla eklenebilir</small></span><SettingsIcon name="plus" /></button> })}</div>
            <div className="ss-lib-custom"><label className="ss-fld"><small>Özel Blok Başlığı</small><input className="ss-in" value={customTitle} onChange={event => setCustomTitle(event.target.value)} placeholder="Örn. mağaza notu" /></label><label className="ss-fld"><small>Özel Blok İçeriği</small><textarea className="ss-in" value={customText} onChange={event => setCustomText(event.target.value)} rows={3} placeholder="Bu blokta görünecek metin" /></label></div>
          </aside>
          <main className="ss-dz-stage">
            <div className="ss-dz-sbar"><b>{format === 'a4' ? 'A4 Çalışma Yüzeyi' : 'Sticker Çalışma Yüzeyi'}</b><span>{layout.length} blok yerleşti</span></div>
            {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/no-noninteractive-tabindex -- blocks inside the application area: pointer drags, and on the keyboard Tab focuses (selects) a block and the arrow keys move it (moveBlockByKey); the pointer-only resize/rotate handles stay out of the Tab order */}
            <div className="ss-dz-canvas"><div className={`shipping-designer-paper is-${format}`} onDragOver={event => event.preventDefault()} onDrop={dropOnCanvas} style={{ aspectRatio: format === 'a4' ? '210 / 297' : `${settings.stickerWidthMm} / ${settings.stickerHeightMm}` }} role="application" aria-label={`${format === 'a4' ? 'A4' : 'Sticker'} etiket tasarım alanı`} aria-describedby="shipping-designer-help">{layout.map((block, index) => { const position = positionFor(block, index); return <article key={block.id} className={`shipping-designer-block${activeBlock?.id === block.id ? ' is-selected' : ''}`} tabIndex={0} aria-roledescription="etiket bloğu" aria-label={`${blockTitle(block)} · solda %${position.x}, üstte %${position.y}`} onFocus={() => setSelectedId(block.id)} onKeyDown={event => moveBlockByKey(event, block, index)} draggable onDragStart={event => dragStart(event, `block:${block.id}`)} onPointerDown={event => beginPointerDrag(event, block)} onPointerMove={movePointerDrag} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} onClick={() => setSelectedId(block.id)} style={{ left: `${position.x}%`, top: `${position.y}%`, width: `${position.width}%`, height: `${position.height}%`, textAlign: block.align, fontSize: `${block.fontSize ?? 14}px`, transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined, transformOrigin: 'center' }}>{activeBlock?.id === block.id && <>{shippingRotateDirections.map(direction => <button key={direction} type="button" tabIndex={-1} className={`shipping-designer-resize-handle shipping-designer-rotate-handle handle-${direction}`} aria-label={`${shippingRotateDirectionLabels[direction]} tutamacını sağa veya sola sürükleyerek derece derece döndür`} title={`${shippingRotateDirectionLabels[direction]} tutamacını sürükleyerek derece derece döndür`} onPointerDown={event => beginPointerRotate(event, block)} onPointerMove={movePointerRotate} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}{shippingResizeDirections.map(direction => <button key={direction} type="button" tabIndex={-1} className={`shipping-designer-resize-handle handle-${direction}`} aria-label={`${shippingResizeDirectionLabels[direction]} boyutlandır`} title={`${shippingResizeDirectionLabels[direction]} boyutlandır`} onPointerDown={event => beginPointerResize(event, block, direction)} onPointerMove={movePointerResize} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}</>}<div className="shipping-designer-block-preview"><ShippingPreviewBlock block={block} showCustomerPhone={settings.showCustomerPhone} /></div></article> })}{layout.length === 0 && <div className="shipping-designer-drop-empty"><strong>İçerik Bırakın</strong><span>Kütüphaneden bir alanı buraya sürükleyin.</span></div>}</div></div>
            <div className="ss-dz-help" id="shipping-designer-help">Bloğu sürükleyerek taşıyın · kenar tutamaçlarıyla boyutlandırın · köşe tutamaçlarıyla döndürün · yazıyı sağdaki panelden düzenleyin · klavyede Tab ile bloğu seçip ok tuşlarıyla taşıyın (Shift ile 5 kat).</div>
          </main>
          <aside className="ss-dz-insp">
            <div className="ss-dz-head"><b>Özellikler</b><small>{activeBlock ? `Seçili: ${blockTitle(activeBlock)}` : 'Bir alan seçin'}</small></div>
            <div className="ss-itabs rv-tabs rv-view-tabs" role="tablist" aria-label="Özellik grupları">{([['general', 'Genel', 'layout'], ['text', 'Yazı', 'type'], ['barcode', 'Barkod', 'barcode']] as const).map(([value, label, icon]) => <button type="button" role="tab" key={value} aria-selected={designerTab === value} className={designerTab === value ? 'is-on is-active' : undefined} onClick={() => setDesignerTab(value)}><span className="rv-tab-label"><SettingsIcon name={icon} /><span className="rv-tab-text" data-text={label}>{label}</span></span></button>)}</div>
            {!activeBlock ? <div className="ss-empty">Düzenlemek için kâğıt üzerindeki bir bloğa tıklayın.</div> : <div className="ss-ibody">
              {designerTab === 'general' && <>
                <label className="ss-fld"><small>Blok Türü</small><select className="ss-in" value={activeBlock.kind} onChange={event => { const kind = event.target.value as ShippingLabelBlockKind; const catalog = shippingLabelBlockCatalog.find(item => item.kind === kind); if (catalog) updateBlock(activeBlock.id, { kind, title: kind === 'custom' ? activeBlock.title : catalog.label, fields: [...catalog.fields], text: kind === 'custom' ? activeBlock.text : '' }) }}>{shippingLabelBlockCatalog.map(item => <option key={item.kind} value={item.kind}>{shippingBlockLabels[item.kind]}</option>)}</select></label>
                <div className="ss-fld"><small>Hizalama</small>{alignSegment(activeBlock)}</div>
                <p className="ss-help">Boyutu doğrudan kâğıt üzerindeki tutamaçlardan ayarlayın.</p>
                <label className="ss-fld"><small>Alana Ekle</small><select className="ss-in" value="" onChange={event => { if (event.target.value) toggleField(activeBlock, event.target.value as ShippingLabelField) }}><option value="">Bir alan seçin…</option>{shippingLabelFields.filter(item => !activeBlock.fields.includes(item.id)).map(item => <option value={item.id} key={item.id}>{item.label}</option>)}</select></label>
                <div className="ss-fchips">{activeBlock.fields.length ? activeBlock.fields.map(field => <button type="button" key={field} className="ss-fchip" title="Alanı çıkar" onClick={() => toggleField(activeBlock, field)}>{shippingLabelFields.find(item => item.id === field)?.label}<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><path d="m4 4 8 8M12 4l-8 8" /></svg></button>) : <span className="ss-faint">Alan eklenmedi</span>}</div>
              </>}
              {designerTab === 'text' && <>
                <div className="ss-fld"><small>Yazı Hizası</small>{alignSegment(activeBlock)}</div>
                <div className="ss-rng"><div className="ss-rng-hd"><span>Yazı Boyutu</span><output>{fontSizeDraft || String(activeBlock.fontSize ?? 14)} px</output></div><input type="range" min={8} max={72} step={1} value={Number(fontSizeDraft || activeBlock.fontSize || 14)} aria-label="Yazı boyutu" onChange={event => { const next = Number(event.target.value); setFontSizeDraft(String(next)); updateBlock(activeBlock.id, { fontSize: next }) }} /><div className="ss-rng-scale"><span>8 px</span><span>72 px</span></div></div>
                <p className="ss-help">Yazı rengi sabit olarak siyahtır.</p>
                {activeBlock.kind === 'custom' && <label className="ss-fld"><small>Metin</small><textarea className="ss-in" value={activeBlock.text} maxLength={500} rows={5} onChange={event => updateBlock(activeBlock.id, { text: event.target.value })} /></label>}
                <p className="ss-help">Alanların gerçek değerleri sipariş yazdırılırken otomatik doldurulur.</p>
              </>}
              {designerTab === 'barcode' && <>
                <div className="ss-fld"><b className="ss-ibody-title">{activeBlock.kind === 'packageBarcode' ? 'Paket Barkodu' : activeBlock.kind === 'trackingBarcode' ? 'Takip Barkodu' : 'Barkod Ayarı'}</b><p className="ss-help">Barkod genişliği tuvaldeki blok genişliğine göre otomatik ölçeklenir. Modül aralıkları tarayıcı okunabilirliğini korur.</p></div>
                <label className="ss-fld"><small>Alan</small><select className="ss-in" value={activeBlock.fields[0] ?? ''} onChange={event => { if (event.target.value) updateBlock(activeBlock.id, { fields: [event.target.value as ShippingLabelField] }) }}><option value="">Alan seçin…</option>{shippingLabelFields.filter(field => field.id === 'trackingNumber' || field.id === 'packageNumber').map(field => <option value={field.id} key={field.id}>{field.label}</option>)}</select></label>
              </>}
              <div className="ss-rng" aria-label="Blok döndürme"><div className="ss-rng-hd"><span>Döndürme</span><output>{activeBlock.rotation ?? 0}°</output></div><div className="ss-rot"><button type="button" className="ss-btn" aria-label="Sola 90 derece döndür" title="Sola 90 derece döndür" onClick={() => rotateActiveBlock(-90)}>−90°</button><button type="button" className="ss-btn" aria-label="Sağa 90 derece döndür" title="Sağa 90 derece döndür" onClick={() => rotateActiveBlock(90)}>+90°</button><button type="button" className="ss-btn" aria-label="Döndürmeyi sıfırla" title="Döndürmeyi sıfırla" onClick={() => updateBlock(activeBlock.id, { rotation: 0 })} disabled={!activeBlock.rotation}>0°</button></div></div>
              <button type="button" className="ss-btn is-danger is-quiet ss-blk-del" onClick={() => removeBlock(activeBlock.id)}><SettingsIcon name="trash" />Bloğu Kaldır</button>
            </div>}
          </aside>
        </div>
      </div></div>
      {bar}
    </>}

    <Modal open={templateNameOpen} title="Şablon Kaydet" description="Geçerli kâğıt yerleşimini bir adla saklayın; sonra tek seçimle geri yükleyin." className="ss-modal is-sm" onClose={() => setTemplateNameOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateNameOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" disabled={!templateNameDraft.trim()} onClick={() => saveTemplate(templateNameDraft)}>Kaydet</button></>}>
      <label className="ss-fld"><small>Şablon Adı</small><input className="ss-in" data-autofocus value={templateNameDraft} maxLength={80} onChange={event => setTemplateNameDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') saveTemplate(templateNameDraft) }} /></label>
      <div className="ss-sum"><div><SettingsIcon name="layout" /><span>{format === 'a4' ? `A4 · Sayfada ${settings.a4LabelsPerPage} Etiket` : `Sticker · ${settings.stickerWidthMm} × ${settings.stickerHeightMm} mm`}<small>{layout.length} blok · gönderici {settings.senderName || '—'}</small></span><span /></div></div>
      <p className="ss-lead">Şablonlar bu tarayıcıda saklanır.</p>
    </Modal>
    <Modal open={templateDeleteOpen && Boolean(selectedTemplate)} title="Şablonu Sil" description={selectedTemplate ? `“${selectedTemplate.name}” şablonu bu tarayıcıdan silinecek. Kâğıttaki yerleşim değişmez.` : undefined} className="ss-modal is-sm" onClose={() => setTemplateDeleteOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateDeleteOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" onClick={() => { deleteTemplate(); setTemplateDeleteOpen(false) }}>Şablonu Sil</button></>}>
      <div className="ss-callout"><SettingsIcon name="alert" /><span><b>Bu işlem geri alınamaz.</b> Aynı yerleşimi yeniden kullanmak için önce yeni adla kaydedin.</span></div>
    </Modal>
    <Modal open={layoutResetOpen} title="Varsayılan Yerleşimi Yükle" description="Kâğıttaki blokların yerine varsayılan yerleşim gelir. Kaydedene kadar hesabınızdaki düzen değişmez." className="ss-modal is-sm" onClose={() => setLayoutResetOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setLayoutResetOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" onClick={() => { resetLayout(); setLayoutResetOpen(false) }}>Varsayılanı Yükle</button></>}>
      <div className="ss-sum"><div><SettingsGlyph state="none" /><span>Şu Anki Yerleşim<small>{layout.length} blok · {layout.filter(block => block.rotation).length} döndürülmüş</small></span><span /></div><div><SettingsGlyph state="done" /><span>Varsayılan Yerleşim<small>{defaultShippingLabelSettings.layout[format].length} blok · {defaultShippingLabelSettings.layout[format].map(block => shippingBlockLabels[block.kind].toLocaleLowerCase('tr-TR')).join(', ')}</small></span><span /></div></div>
    </Modal>
  </>
}

const shippingPreviewValues: Record<ShippingLabelField, string> = { trackingNumber: '73300036563130080', packageNumber: '9236253', orderNumber: '#1972215187', customerName: 'FEHİME MAT', address: 'KARAKÖPRÜ / ŞANLIURFA', cargoProvider: 'Trendyol Express', senderName: 'RAVENCIA', senderAddress: '403.CAD.NO:24/2', customerEmail: 'musteri@example.com' }

// Sample content of a block, laid out by the same rules as the printed label (shipping-label-preview.ts).
function ShippingPreviewBlock({ block, showCustomerPhone }: { block: ShippingLabelBlock; showCustomerPhone: boolean }) {
  const parts = shippingLabelBlockParts(block, { showCustomerPhone })
  const fontSize = `${block.fontSize ?? 14}px`
  const barcode = parts[0]?.type === 'barcode' ? parts[0] : null
  const content = parts.map((part, index) => {
    if (part.type === 'title') return <strong key={index} className="shipping-preview-title">{part.text}</strong>
    if (part.type === 'text') return <strong key={index}>{part.text}</strong>
    if (part.type === 'lines') return <span key={index}>{shippingPreviewValues[part.field]}</span>
    if (part.type === 'field') return <span key={index}><b>{shippingLabelFields.find(option => option.id === part.field)?.label}</b>{shippingPreviewValues[part.field]}</span>
    return null
  })
  if (barcode) {
    const value = shippingPreviewValues[barcode.field]
    const bars = code128Bars(value)
    return <div className="shipping-preview-barcode" style={{ fontSize }} aria-label={`Barkod önizlemesi: ${value}`}><span className="shipping-preview-bars" style={{ '--barcode-module-count': bars.length } as CSSProperties} aria-hidden="true">{bars.map((isBar, index) => <i className={isBar ? 'is-bar' : undefined} key={index} />)}</span><strong>{value}</strong>{content}</div>
  }
  return <div className={`shipping-preview-content preview-content-${block.kind}`} style={{ fontSize }}>{content}</div>
}
