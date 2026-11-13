import { Fragment, useEffect, useEffectEvent, useLayoutEffect, useRef, useState, type CSSProperties, type DragEvent, type FormEvent, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { useLocation, useSearchParams } from 'react-router'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, hubPoll, loadAllPages, settingsIfMatch } from '../../shared/api'
import { FieldHelp, LoadingState, Modal, StickyActionBar, TitleHint } from '../../shared/components'
import { uiGlyph16, type UiIconName } from '../../shared/components/UiIcon'
import type { DataResetGroup } from './data-reset-groups'
import { NoticeToast, type NoticeToastKind } from '../../shared/components/NoticeToast'
import { cloneShippingLabelLayout, defaultShippingLabelBlockPosition, defaultShippingLabelSettings, draggedShippingLabelPosition, labelBlockContentHeight, resizedShippingLabelPosition, shippingLabelCaptionStyle, shippingLabelDragStarted, shippingLabelFieldsPerRow, shippingLabelFitsText, shippingLabelShowsBarcodeText, type ShippingLabelCaptionStyle, normalizeShippingLabelSettings, overflowingLabelBlocks, ShippingLabelBlockBody, shippingLabelBlockCatalog, shippingLabelFields, ShippingLabelFit, shippingLabelLayoutIssues, shippingLabelSizeMm, shippingLabelTextSize, shippingLabelTextSizes, stackShippingLabelLayout, useShippingLabelSettings, type ShippingLabelBlock, type ShippingLabelBlockKind, type ShippingLabelField, type ShippingLabelSettings, type ShippingLabelValues } from '../shipping'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontFamilyOptions, appearanceFontScale, appearanceFontSizeOptions, defaultAppearanceColorTheme, defaultAppearanceSettings, grafitAppearanceColorTheme, matchingColorThemeId, defaultLightPalette, useAppearanceSettings, type AppearanceColorThemeProfile, type AppearancePalette, type AppearanceSettings } from './appearance-settings'
import { SettingsNotice, type SettingsNoticeState } from './SettingsNotifications'
import { readVisualThemePreference, switchVisualThemeSmoothly, visualThemeChangeEvent, type VisualTheme } from './visual-theme'
import { unknownCount, useRememberedTabCounts } from '../../shared/tab-counts'
import { PanelSelect } from '../../shared/PanelSelect'
import { marketplaceResetCount, marketplaceResetTotal, progressText, runMarketplaceResets, type BulkResetProgress } from './marketplace-bulk-reset'
import { connectionFullResetSupported, type ConnectionFullResetView } from '../marketplace/connection-data-reset'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { UiIcon } from '../../shared/components/UiIcon'
import { PanelFactoryResetSection } from './PanelFactoryResetSection'
import { dataResetConfirmWord, panelResetConfirmed, togglePanelResetCategories, type PanelFactoryResetView } from './panel-factory-reset'
import type { Connection } from '../marketplace/shared'
import './settings-sade.css'
import '../../shared/view-tabs.css'
import { formatIstanbul } from '../../shared/format-date'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { useTabSlideInPlace } from '../../shared/tab-slide'
import { copyText } from '../../shared/copy-text'
import { formatBackupSize, googleCloudSteps, offsiteBackupDefaultFolder, offsiteBackupDirty, offsiteBackupDraft, offsiteBackupDraftError, offsiteBackupSaveBody, offsiteBackupState, offsiteCopyPending, offsiteSetupDone, offsiteSetupPlan, shortAgeKey, firstRunStartedAt, markFirstRun, clearFirstRun, type OffsiteBackupDraft, type OffsiteBackupView } from './offsite-backup'
import { backupKeyFileName, decryptBackup, decryptedFileName, downloadText, generateBackupKeyPair, identityFromKeyFile, saveStream, type BackupKeyPair } from './offsite-backup-crypto'
import { ReauthInputs, reauthReady } from '../../shared/reauth'
import { assignableRoles, availableUserActions, isEmailAddress, reauthBody, userLockReason, userRoleHints, userRoleLabel, userRoleLabels, type ManagedUser, type ManagedUserList, type UserAction, type UserRole } from './user-management'

type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; secret?: string; qrSvg: string; expiresAt: string }
type SessionConfirmation = { action: 'revoke' | 'revoke-others' | 'delete' | 'delete-closed'; sessionId?: string }

// System settings as a section list (docs/tasarim-onerileri/sistem-ayarlari.html, option 2): the four former tabs are
// split into ten small sections; the list on the left shows each section's state, the pane on the right shows one.
type SettingsSectionKey = 'auth' | 'sessions' | 'users' | 'alerts' | 'ai' | 'database' | 'offsite' | 'sender' | 'dims' | 'designer' | 'read' | 'palette' | 'themes' | 'notify'
type ShippingSection = 'sender' | 'dims' | 'designer'
type AppearanceSection = 'read' | 'palette' | 'themes' | 'notify'
type SettingsIconName = 'shield' | 'truck' | 'device' | 'phone' | 'db' | 'user' | 'ruler' | 'layout' | 'type' | 'palette' | 'save' | 'bell' | 'lock' | 'info' | 'alert' | 'logout' | 'trash' | 'reset' | 'plus' | 'barcode' | 'sparkle' | 'copy' | 'back' | 'chev' | 'tag' | 'cloud'
type SettingsGlyphState = 'done' | 'pending' | 'none' | 'bad'

const settingsSectionGroups: Array<{ label: string; icon: SettingsIconName; items: Array<{ key: SettingsSectionKey; label: string; short?: string; icon: SettingsIconName }> }> = [
  { label: 'Güvenlik ve Oturumlar', icon: 'shield', items: [{ key: 'auth', label: 'Authenticator', icon: 'shield' }, { key: 'sessions', label: 'Oturumlar', icon: 'device' }, { key: 'alerts', label: 'Alarm Bildirimleri', short: 'Alarmlar', icon: 'bell' }] },
  { label: 'Kullanıcılar', icon: 'user', items: [{ key: 'users', label: 'Kullanıcılar', icon: 'user' }] },
  { label: 'Veri', icon: 'db', items: [{ key: 'database', label: 'Veritabanı Temizliği', short: 'Temizlik', icon: 'db' }, { key: 'offsite', label: 'Google Drive Yedeği', short: 'Drive Yedeği', icon: 'cloud' }] },
  { label: 'Yapay Zekâ', icon: 'sparkle', items: [{ key: 'ai', label: 'Yapay Zekâ', icon: 'sparkle' }] },
  { label: 'Kargo Ayarları', icon: 'truck', items: [{ key: 'sender', label: 'Gönderici ve Yazdırma', short: 'Gönderici', icon: 'user' }, { key: 'dims', label: 'Etiket Ölçüleri', short: 'Ölçüler', icon: 'ruler' }, { key: 'designer', label: 'Etiket Tasarımı', short: 'Tasarım', icon: 'layout' }] },
  { label: 'Görünüm', icon: 'palette', items: [{ key: 'read', label: 'Okunabilirlik ve Tema', short: 'Tema', icon: 'type' }, { key: 'palette', label: 'Renk Paleti', short: 'Renkler', icon: 'palette' }, { key: 'themes', label: 'Kayıtlı Temalar', short: 'Temalar', icon: 'save' }, { key: 'notify', label: 'Bildirimler', icon: 'bell' }] }
]
const settingsSections = settingsSectionGroups.flatMap(group => group.items.map(item => ({ ...item, group: group.label })))
const settingsSectionOrder = settingsSections.map(item => item.key)
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
  chev: <path d="m4.5 6 3.5 3.5L11.5 6" />,
  tag: <><path d="M2.5 8.2V3.2a.7.7 0 0 1 .7-.7h5l5.3 5.3a1 1 0 0 1 0 1.4l-4.3 4.3a1 1 0 0 1-1.4 0z" /><circle cx="5.5" cy="5.5" r=".9" /></>,
  cloud: <><path d="M4.6 12.5h6.6a2.8 2.8 0 0 0 .5-5.55A3.9 3.9 0 0 0 4.2 6.3a3.1 3.1 0 0 0 .4 6.2z" /></>
}

// The icons that mean what a glyph of the panel's main icon set means draw that glyph.
const sharedSettingsGlyphs: Partial<Record<SettingsIconName, UiIconName>> = {
  shield: 'shield', truck: 'truck', lock: 'lock', bell: 'bell', alert: 'alert', logout: 'logout', trash: 'trash', plus: 'plus', copy: 'copy', save: 'save',
  tag: 'tag', sparkle: 'sparkle', device: 'monitor', layout: 'layout', back: 'chevronLeft', chev: 'chevronDown'
}

function SettingsIcon({ name, size = 16 }: { name: SettingsIconName; size?: number }) {
  const shared = sharedSettingsGlyphs[name]
  return <svg className="ss-icon" width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{shared ? uiGlyph16(shared) : settingsIconPaths[name]}</svg>
}

function SettingsGlyph({ state }: { state: SettingsGlyphState }) {
  if (state === 'done') return <svg className="ss-st is-done" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'pending') return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  if (state === 'bad') return <svg className="ss-st is-bad" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
}

function SettingsPaneHead({ section, description, actions }: { section: SettingsSectionKey; description?: ReactNode; actions?: ReactNode }) {
  const item = settingsSections.find(entry => entry.key === section) ?? settingsSections[0]
  return <header className="ss-head"><div><h2>{item.label}{description ? <TitleHint label={item.label}>{description}</TitleHint> : null}</h2></div>{actions ? <div className="ss-head-actions">{actions}</div> : null}</header>
}

function SettingsBar({ title, copy, hint, dirty = false, children }: { title: string; copy?: ReactNode; hint?: ReactNode; dirty?: boolean; children: ReactNode }) {
  return <StickyActionBar label={title} className="ss-bar"><div className="ss-bar-copy"><strong>{title}{hint ? <TitleHint label={title}>{hint}</TitleHint> : null}</strong>{copy ? <span className={dirty ? 'is-dirty' : undefined} title={typeof copy === 'string' ? copy : undefined}>{copy}</span> : null}</div><div className="ss-bar-actions">{children}</div></StickyActionBar>
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

// The categories of Ayarlar › Veri come from the server (PanelFactoryResetInventory): together they are Paneli Tamamen
// Sıfırla, so nothing of the tenant's records is left out of them.
function resetGroupsOf(view: PanelFactoryResetView | undefined): DataResetGroup[] {
  if (!view) return []
  const sections = Array.from(new Set(view.groups.map(group => group.section)))
  return sections.map(section => ({ label: section, description: '', options: view.groups.filter(group => group.section === section).map(group => ({ scope: group.key, label: group.label, description: group.description, unit: 'kayıt' })) }))
}
const resetNumber = (value: number) => value.toLocaleString('tr-TR')
const resetGroupIcons: Record<string, SettingsIconName> = { 'Satış ve Faturalar': 'barcode', Katalog: 'db', 'Pazaryeri Kayıtları': 'save', 'Sistem Kayıtları': 'bell' }

// Database cleanup: each group has one header with its own select-all and a selected/total count; each data type is
// a whole-row tile (checkbox beside the title, count as a tag); the notes sit in one callout and the sticky bar shows
// what goes as chips, the total, the typed confirmation and the reset button.
type ResetMarketplace = { id: string; platformCode: string; displayName: string; passive: boolean; count: number | undefined }
function SettingsDataReset({ forbidden, groups, counts, countsFailed, picked, onToggle, marketplaces, marketplacePicked, onToggleMarketplaces, progress, confirmation, onConfirmation, password, onPassword, factor, onFactor, mfa, busy, onConfirm }: { forbidden: boolean; groups: DataResetGroup[]; counts?: Record<string, number>; countsFailed: boolean; picked: string[]; onToggle: (scopes: string[], checked: boolean) => void; marketplaces: ResetMarketplace[]; marketplacePicked: string[]; onToggleMarketplaces: (ids: string[], checked: boolean) => void; progress: Record<string, BulkResetProgress>; confirmation: string; onConfirmation: (value: string) => void; password: string; onPassword: (value: string) => void; factor: string; onFactor: (value: string) => void; mfa: boolean; busy: boolean; onConfirm: () => void }) {
  const options = groups.flatMap(group => group.options)
  const going = options.filter(option => picked.includes(option.scope))
  const goingMarketplaces = marketplaces.filter(entry => marketplacePicked.includes(entry.id))
  const marketplaceTotal = marketplaceResetTotal(goingMarketplaces.map(entry => entry.id), Object.fromEntries(marketplaces.map(entry => [entry.id, entry.count])))
  const known = going.every(option => counts?.[option.scope] !== undefined) && marketplaceTotal !== undefined
  const total = going.reduce((sum, option) => sum + (counts?.[option.scope] ?? 0), 0) + (marketplaceTotal ?? 0)
  const goingCount = going.length + goingMarketplaces.length
  // Every data type chosen is Paneli Tamamen Sıfırla: the page says so before the confirmation and asks for its word.
  const fullReset = options.length > 0 && going.length === options.length
  const resetConfirmWord = dataResetConfirmWord(options.length, going.length)
  const confirmed = panelResetConfirmed(confirmation, resetConfirmWord)
  const authenticated = reauthReady(password, factor, mfa)
  const countText = (scope: string, unit: string) => {
    const value = counts?.[scope]
    if (value !== undefined) return `${resetNumber(value)} ${unit}`
    return countsFailed ? 'Sayı alınamadı' : counts ? null : 'Sayılıyor…'
  }
  // The counts, the marketplaces and the reset are the owner's and administrators' (the server answers others 403).
  if (forbidden) return <>
    <SettingsPaneHead section="database" description="Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir." />
    <div className="ss-body"><div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Veritabanı temizliğini yalnız hesap sahibi ve yöneticiler görür ve yapar.</span></div></div>
  </>
  return <>
    <SettingsPaneHead section="database" description="Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir." />
    <div className="ss-body ss-dr" role="group" aria-label="Sıfırlanacak veriler">
      {groups.map(group => {
        const chosen = group.options.filter(option => picked.includes(option.scope)).length
        const all = chosen === group.options.length
        return <section className="ss-dr-group" key={group.label} aria-label={group.label}>
          <header className="ss-dr-gh">
            <span className="ss-tile is-sm ss-dr-ico"><SettingsIcon name={resetGroupIcons[group.label] ?? 'db'} /></span>
            <div><h3>{group.label}</h3></div>
            <span className={`ss-tag ss-dr-sel${chosen ? ' is-on' : ''}`}>{chosen}/{group.options.length} Seçili</span>
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
      {marketplaces.length > 0 && <section className="ss-dr-group" aria-label="Pazaryerleri">
        <header className="ss-dr-gh">
          <span className="ss-tile is-sm ss-dr-ico"><SettingsIcon name="truck" /></span>
          <div><h3>Pazaryerleri</h3><p>Tam Sıfırlama: bağlantının tüm kayıtları silinir, bağlantı ayarları korunur.</p></div>
          <span className={`ss-tag ss-dr-sel${goingMarketplaces.length ? ' is-on' : ''}`}>{goingMarketplaces.length}/{marketplaces.length} Seçili</span>
          <button type="button" className="ss-btn is-xs" disabled={busy} onClick={() => onToggleMarketplaces(marketplaces.map(entry => entry.id), goingMarketplaces.length !== marketplaces.length)}>{goingMarketplaces.length === marketplaces.length ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>
        </header>
        <div className="ss-dr-list">{marketplaces.map(entry => {
          const on = marketplacePicked.includes(entry.id)
          const state = progress[entry.id]
          const text = progressText(state)
          return <label key={entry.id} className={`ss-dr-opt is-mk${on ? ' is-on' : ''}`}>
            <input type="checkbox" className="ss-cb" checked={on} disabled={busy} onChange={event => onToggleMarketplaces([entry.id], event.target.checked)} />
            <PlatformSquareMark code={entry.platformCode} name={entry.displayName} />
            <span className="ss-dr-text"><b>{entry.displayName}</b><small>Sipariş, iade, fatura, ürün bağı, kategori listeleri ve finans kayıtları; ayarlar korunur.{entry.passive ? ' Bağlantı pasif.' : ''}</small>
              {text && <span className={`ss-dr-prog is-${state?.state}`} role="status">{state?.state === 'running' && <i style={{ width: `${state.percent ?? 0}%` }} />}<em>{text}</em></span>}</span>
            {entry.count !== undefined ? <span className="ss-tag ss-dr-count">{resetNumber(entry.count)} kayıt</span> : <span className="ss-tag ss-dr-count">Sayılıyor…</span>}
          </label>
        })}</div>
      </section>}
      <div className="ss-callout ss-dr-note">
        <p className="is-warn"><SettingsIcon name="alert" /><span><b>Geri alınamaz.</b> Yetkili işlemi: bağlı alt kayıtlar güvenli sırayla temizlenir.</span></p>
        <p><SettingsIcon name="info" /><span>Yukarıdaki veri türleri bütün pazaryerleri için birlikte silinir. Yalnız bir pazaryerinin kayıtları için <b>Pazaryerleri</b> bölümünü ya da <b>Entegrasyonlar</b> sayfasında bağlantının <b>Verileri Sıfırla</b> menüsünü kullanın.</span></p>
        {fullReset && <p className="is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Bütün veri türleri seçili: bu, Paneli Tamamen Sıfırla demektir.</b> Bu oturum dışındaki bütün oturumlar kapanır, panelde saklanan bütün dosyalar silinir. Onay için <b>{resetConfirmWord}</b> yazın.</span></p>}
      </div>
      <PanelFactoryResetSection />
    </div>
    <StickyActionBar label="Silinecekler" className="ss-bar ss-dr-bar">
      <div className="ss-dr-sum" role="status">
        <div className="ss-dr-sum-head"><strong>Silinecekler</strong><span className="ss-dr-total">Toplam <b>{goingCount && !known ? '…' : `${resetNumber(total)} kayıt`}</b></span></div>
        {goingCount > 0 && <div className="ss-dr-chips">{goingMarketplaces.map(entry => <span key={entry.id} className="ss-dr-chip">{entry.displayName} (Tam)<b>{entry.count !== undefined ? resetNumber(entry.count) : '…'}</b></span>)}{going.map(option => <span key={option.scope} className="ss-dr-chip">{option.label}{counts?.[option.scope] !== undefined ? <b>{resetNumber(counts[option.scope])}</b> : null}</span>)}</div>}
      </div>
      <div className="ss-bar-actions">
        <label className="ss-dr-ask" htmlFor="settings-reset-confirm">Onaylamak için <b>{resetConfirmWord}</b> yazın{confirmed && <UiIcon name="check" size={14} />}</label>
        <input id="settings-reset-confirm" className={`ss-in ss-dr-in${confirmed ? ' is-match' : ''}`} value={confirmation} onChange={event => onConfirmation(event.target.value)} disabled={busy} autoComplete="off" placeholder={`${resetConfirmWord} yazın`} aria-label={`Onaylamak için ${resetConfirmWord} yazın`} title={`Onaylamak için ${resetConfirmWord} yazın`} />
        <ReauthInputs bare idPrefix="settings-reset" password={password} onPassword={onPassword} factor={factor} onFactor={onFactor} mfa={mfa} disabled={busy} inputClassName="ss-in ss-dr-in ss-dr-auth" />
        <button type="button" className="ss-btn is-danger ss-dr-go" disabled={!goingCount || !confirmed || !authenticated || busy} onClick={onConfirm}><SettingsIcon name="trash" />{busy ? 'Sıfırlanıyor…' : goingMarketplaces.length ? `${goingCount} Kalemi Sıfırla` : going.length ? `${going.length} Veri Türünü Sıfırla` : 'Seçilen Verileri Sıfırla'}</button>
      </div>
    </StickyActionBar>
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
  // The designer opens on the account's default format, which may arrive after this page mounted.
  const labelFormatSynced = useRef(shippingSettings.isSuccess)
  const serverDefaultFormat = shippingSettings.data?.settings != null ? normalizeShippingLabelSettings(shippingSettings.data.settings).defaultFormat : undefined
  useLayoutEffect(() => {
    if (labelFormatSynced.current || !shippingSettings.isSuccess) return
    labelFormatSynced.current = true
    if (serverDefaultFormat) setLabelFormat(serverDefaultFormat)
  }, [serverDefaultFormat, shippingSettings.isSuccess])
  const [labelSaving, setLabelSaving] = useState(false)
  const [appearanceStatus, setAppearanceStatus] = useState({ dirty: false, themeCount: 0 })
  const [resetScopes, setResetScopes] = useState<string[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  // Resets take the password again (and the authenticator code when it is on); one check covers the run's resets.
  const [resetPassword, setResetPassword] = useState('')
  const [resetFactor, setResetFactor] = useState('')
  const [resetBusy, setResetBusy] = useState(false)
  const [marketplacePicked, setMarketplacePicked] = useState<string[]>([])
  const [marketplaceProgress, setMarketplaceProgress] = useState<Record<string, BulkResetProgress>>({})
  const resetCancelled = useRef(false)
  useEffect(() => { resetCancelled.current = false; return () => { resetCancelled.current = true } }, [])
  // What each list holds now, the counts the reset reports when it is done. Owner and administrators only: for the
  // other roles the server answers 403 and the page asks nothing else.
  const resetView = useQuery({ queryKey: ['panel-factory-reset'], queryFn: () => hubPoll<PanelFactoryResetView>('/settings/factory-reset'), enabled: section === 'database', staleTime: 0, retry: false })
  const resetForbidden = resetView.error instanceof ApiRequestError && resetView.error.status === 403
  const resetGroups = resetGroupsOf(resetView.data)
  // The active marketplace connections, each with what its Tam Sıfırlama would remove (the same view its window shows).
  const resetConnections = useQuery({ queryKey: ['connections', 'settings-reset'], queryFn: async () => (await loadAllPages<Connection>('/connections')).items.filter(item => item.status !== 'DELETED' && item.status !== 'HIDDEN' && connectionFullResetSupported(item.platformCode)), enabled: section === 'database' && resetView.isSuccess, staleTime: 0 })
  const resetViews = useQueries({ queries: (resetConnections.data ?? []).map(item => ({ queryKey: ['connection-full-reset', item.id], queryFn: () => hubPoll<ConnectionFullResetView>(`/connections/${item.id}/full-reset`), enabled: section === 'database' && resetView.isSuccess, staleTime: 0 })) })
  const resetMarketplaces: ResetMarketplace[] = (resetConnections.data ?? []).map((item, index) => ({ id: item.id, platformCode: item.platformCode, displayName: item.displayName, passive: item.status !== 'ACTIVE' && item.status !== 'VERIFIED', count: marketplaceResetCount(resetViews[index]?.data) }))
  // Through hubApi: a session that ended shows the sign-in screen instead of an error box.
  const status = useQuery({ queryKey: ['security'], queryFn: () => hubApi<SecurityStatus>('/auth/security-status') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => hubApi<SecuritySession[]>('/auth/sessions') })
  const [sessionCount] = useRememberedTabCounts('Ayarlar oturumlar', [{ value: 'sessions', count: sessions.isError ? unknownCount : sessions.data?.length }])
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
    // A category brings the ones it requires (orders bring their invoices, products their stock and prices).
    setResetScopes(current => togglePanelResetCategories(resetView.data?.groups ?? [], current, scopes, checked))
  }
  function toggleMarketplaces(ids: string[], checked: boolean) {
    setMarketplacePicked(current => checked ? Array.from(new Set([...current, ...ids])) : current.filter(value => !ids.includes(value)))
  }
  async function resetOperationalData() {
    // The page's own confirmation word is the gate for the marketplaces and the catalog alike.
    const resetConfirmWord = dataResetConfirmWord(resetView.data?.groups.length ?? 0, resetScopes.length)
    if ((!resetScopes.length && !marketplacePicked.length) || !panelResetConfirmed(resetConfirmation, resetConfirmWord)) return
    setResetBusy(true); setNotice(null)
    try {
      // The marketplaces first, one after the other; then the catalog cleanup.
      const chosen = (resetConnections.data ?? []).filter(item => marketplacePicked.includes(item.id))
      if (chosen.length) {
        const ok = await runMarketplaceResets(chosen, {
          request: item => hubApi<ConnectionFullResetView>(`/connections/${item.id}/full-reset`, { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation: item.displayName, ...reauthBody(resetPassword, resetFactor) }) }),
          poll: item => hubPoll<ConnectionFullResetView>(`/connections/${item.id}/full-reset`),
          sleep: ms => new Promise(resolve => window.setTimeout(resolve, ms)),
          onProgress: (id, state) => setMarketplaceProgress(current => ({ ...current, [id]: state })),
          isCancelled: () => resetCancelled.current
        })
        if (!ok) { if (!resetCancelled.current) fail('Pazaryeri sıfırlaması tamamlanamadı; sonraki işlemler başlatılmadı. Ayrıntı ilgili satırda.'); return }
        setMarketplacePicked([])
        if (!resetScopes.length) succeed(`${chosen.length} pazaryeri bağlantısı tam sıfırlandı. Otomatik eşitleme açıksa kayıtlar yeniden çekilir.`)
      }
      if (resetScopes.length) {
        // One job for every chosen category (the same one Paneli Tamamen Sıfırla runs); its steps show below.
        const next = await hubApi<PanelFactoryResetView>('/settings/factory-reset', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ categories: resetScopes, confirmation: resetConfirmation.trim(), ...reauthBody(resetPassword, resetFactor) }) })
        client.setQueryData(['panel-factory-reset'], next)
        succeed('Seçilen veriler silinmeye başladı; adımlar sayfanın altındaki Sıfırlama İlerlemesi bölümünde.')
      }
      setResetScopes([]); setResetConfirmation(''); setResetPassword(''); setResetFactor('')
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
  // A group or section tab slides the pane in from its side and starts the page from its top (shared/tab-slide.tsx).
  // The pane stays mounted (the shipping and appearance forms keep their drafts), so the slide plays in place.
  const paneRef = useRef<HTMLDivElement>(null)
  useTabSlideInPlace(paneRef, section, settingsSectionOrder)
  // Phones: the group tabs scroll sideways; bring the selected one into view (a deep link to Görünüm left it off-screen).
  const groupTabsRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const row = groupTabsRef.current
    const tab = row?.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!row || !tab || row.scrollWidth <= row.clientWidth + 1) return
    const rowBox = row.getBoundingClientRect(); const tabBox = tab.getBoundingClientRect()
    row.scrollLeft += tabBox.left - rowBox.left - (rowBox.width - tabBox.width) / 2
  }, [activeGroup])

  return <section className="content ss-page">
    {notice && <SettingsNotice {...notice} />}
    <header className="ss-page-head"><span className="ss-crumb">Ayarlar</span><h1>Sistem Ayarları</h1></header>
    <div ref={groupTabsRef} className="ss-gtabs rv-tabs rv-view-tabs" role="tablist" aria-label="Ayar grupları">{settingsSectionGroups.map(group => <button type="button" role="tab" key={group.label} aria-selected={group === activeGroup} className={group === activeGroup ? 'is-on is-active' : undefined} onClick={() => selectSection(group.items[0].key)}><span className="rv-tab-label"><SettingsIcon name={group.icon} /><span className="rv-tab-text" data-text={group.label}>{group.label}</span></span></button>)}</div>
    <div className="ss-card">
      {activeGroup.items.length > 1 && <div className="ss-stabs"><span className="ss-seg rv-tabs rv-view-tabs" role="tablist" aria-label={`${activeGroup.label} bölümleri`}>{activeGroup.items.map(item => <button type="button" role="tab" key={item.key} aria-selected={section === item.key} className={section === item.key ? 'is-on is-active' : undefined} onClick={() => selectSection(item.key)}><span className="rv-tab-label"><SettingsIcon name={item.icon} />{item.short ? <><span className="rv-tab-text ss-lbl-long" data-text={item.label}>{item.label}</span><span className="rv-tab-text ss-lbl-short" data-text={item.short}>{item.short}</span></> : <span className="rv-tab-text" data-text={item.label}>{item.label}</span>}</span>{navMeta(item.key)}</button>)}</span></div>}
      <div ref={paneRef} className="ss-pane">
        {section === 'auth' && <>
          <SettingsPaneHead section="auth" description="İki adımlı doğrulama ile girişte telefonunuzdaki kod da istenir." />
          <div className="ss-body">{status.isLoading ? <LoadingState>Güvenlik durumu yükleniyor…</LoadingState> : status.isError || !status.data ? <div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Güvenlik durumu alınamadı.</span></div> : <>
            <div className="ss-auth">
              <span className="ss-tile is-lg"><SettingsIcon name="shield" /></span>
              <h3><span>Authenticator</span> {totpEnabled ? <span className="ss-tag is-ink"><SettingsGlyph state="done" />Etkin</span> : <span className="ss-tag"><SettingsGlyph state="none" />Kapalı</span>}</h3>
              {totpEnabled && <div className="ss-auth-meta"><span>Kalan kurtarma kodu <b>{status.data.recoveryCodesRemaining}</b></span></div>}
              <div className="ss-auth-side">{totpEnabled ? <span className="ss-tag is-ink is-lg" aria-label="Authenticator etkin"><SettingsGlyph state="done" />Hesabınız Korunuyor</span> : <button type="button" className="ss-btn is-primary" onClick={openMfa}><SettingsIcon name="shield" />Authenticator’ı Etkinleştir</button>}</div>
            </div>
          </>}</div>
        </>}
        {section === 'sessions' && <>
          <SettingsPaneHead section="sessions"actions={activeOtherSessions.length || closedSessions.length ? <>{activeOtherSessions.length > 0 && <button type="button" className="ss-btn is-danger" onClick={() => setSessionConfirmation({ action: 'revoke-others' })}><span className="ss-lbl-long">Diğer Tüm Oturumları Kapat</span><span className="ss-lbl-short">Diğerlerini Kapat</span> <span className="ss-count">{activeOtherSessions.length}</span></button>}{closedSessions.length > 0 && <button type="button" className="ss-btn" onClick={() => setSessionConfirmation({ action: 'delete-closed' })}><span className="ss-lbl-long">Kapalı Oturumları Sil</span><span className="ss-lbl-short">Kapalıları Sil</span> <span className="ss-count">{closedSessions.length}</span></button>}</> : undefined} />
          <div className="ss-body is-flush">{sessions.isLoading ? <div className="ss-pad"><LoadingState>Oturumlar yükleniyor…</LoadingState></div> : sessions.isError || !sessions.data ? <div className="ss-pad"><div role="alert" className="ss-callout is-danger"><SettingsIcon name="alert" /><span>Oturumlar alınamadı.</span></div></div> : <div className="ss-sess">
            <div className="ss-sess-sub">Aktif <span className="ss-count">{activeSessions.length}</span></div>
            {activeSessions.map(sessionRow)}
            {closedSessions.length ? <><div className="ss-sess-sub">Kapalı <span className="ss-count">{closedSessions.length}</span></div>{closedSessions.map(sessionRow)}</> : null}
          </div>}</div>
        </>}
        {section === 'users' && <UserManagementSection mfa={totpEnabled} />}
        {section === 'alerts' && <AlertEmailSettingsSection />}
        {section === 'offsite' && <OffsiteBackupSection mfa={totpEnabled} />}
        {section === 'ai' && <AiSettingsSection />}
        {section === 'database' && <SettingsDataReset forbidden={resetForbidden} groups={resetGroups} counts={resetView.data ? Object.fromEntries(resetView.data.groups.map(entry => [entry.key, entry.count])) : undefined} countsFailed={resetView.isError} picked={resetScopes} onToggle={toggleResetScopes} marketplaces={resetMarketplaces} marketplacePicked={marketplacePicked} onToggleMarketplaces={toggleMarketplaces} progress={marketplaceProgress} confirmation={resetConfirmation} onConfirmation={setResetConfirmation} password={resetPassword} onPassword={setResetPassword} factor={resetFactor} onFactor={setResetFactor} mfa={totpEnabled} busy={resetBusy} onConfirm={() => void resetOperationalData()} />}
        <ShippingLabelSettingsPanel section={isShipping ? section as ShippingSection : null} settings={labelSettings} saved={savedLabelSettings} dirty={shippingDirty} saving={labelSaving} loadFailed={shippingSettings.isError} loading={shippingSettings.isPending} format={labelFormat} setFormat={setLabelFormat} onChange={shippingSettings.setSettings} onSave={() => void saveLabelSettings()} />
        <AppearanceSettingsSection section={isAppearance ? section as AppearanceSection : null} onStatus={setAppearanceStatus} />
      </div>
    </div>

    <Modal open={mfaStep !== 'closed'} title="Authenticator Kurulumu" hint={mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın. Parolanız yalnız bu kurulum için doğrulanır; kaydedilmez.' : mfaStep === 'verify' ? 'QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın ve üretilen 6 haneli kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'} className="ss-modal is-lg ss-mfa" onClose={closeMfa} footer={mfaStep === 'password' ? <><span className="ss-modal-note">Adım 1 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => setMfaStep('closed')}>Vazgeç</button><button type="submit" form="ss-mfa-password" className="ss-btn is-primary" disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam Et'}</button></> : mfaStep === 'verify' ? <><span className="ss-modal-note">Adım 2 / 3</span><button type="button" className="ss-btn" disabled={busy} onClick={() => { setMfaError(''); setMfaStep('password') }}><SettingsIcon name="back" />Geri</button><button type="submit" form="ss-mfa-verify" className="ss-btn is-primary" disabled={busy}>{busy ? 'Kontrol Ediliyor…' : 'Etkinleştir'}</button></> : <><button type="button" className="ss-btn is-quiet" onClick={() => void copyRecoveryCodes()}><SettingsIcon name="copy" />Kopyala</button><span className="ss-modal-note" /><button type="button" className="ss-btn is-primary" onClick={() => { setMfaError(''); setMfaStep('closed') }}>Kodları Sakladım</button></>}>
      <div className="ss-mfa-split">
        <nav className="ss-mfa-rail" aria-label="Kurulum adımları">{mfaSteps.map((step, index) => <div key={step.key} className={`ss-mfa-step${index === mfaIndex ? ' is-on' : index < mfaIndex ? ' is-done' : ''}`} aria-current={index === mfaIndex ? 'step' : undefined}><SettingsGlyph state={index < mfaIndex ? 'done' : index === mfaIndex ? 'pending' : 'none'} /><span>{step.label}</span><small>{step.hint}</small></div>)}</nav>
        <div className="ss-mfa-pane">
          {mfaStep === 'password' && <form id="ss-mfa-password" className="ss-stack" onSubmit={prepareMfa}><label className="ss-fld"><small>Mevcut Parola</small><input className="ss-in" name="password" type="password" autoComplete="current-password" required data-autofocus placeholder="••••••••" /></label></form>}
          {mfaStep === 'verify' && setup && <form id="ss-mfa-verify" className="ss-qrrow" onSubmit={confirmMfa}><div className="ss-qr"><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /></div><div className="ss-qr-copy"><details className="ss-man"><summary>Kurulum Anahtarını Elle Göster</summary><code>{setup.secret ?? manualSetupKey(setup.otpauthUri)}</code></details><div className="ss-fld"><small>6 Haneli Doğrulama Kodu</small><OtpInput digits={otp} onChange={setOtp} /></div></div></form>}
          {mfaStep === 'recovery' && <><div className="ss-codes">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><div className="ss-callout is-warn"><SettingsIcon name="alert" /><span><b>Bu kodlar yalnızca bir kez gösterilir.</b> Her kod tek kullanımlıktır; telefonunuza erişemediğinizde giriş için kullanılır.</span></div></>}
          {mfaError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{mfaError}</span>}
        </div>
      </div>
    </Modal>

    {sessionConfirmationCopy && <Modal open title={sessionConfirmationCopy.title} description={sessionConfirmationCopy.description} className="ss-modal is-sm" onClose={() => { if (!sessionActionBusy) setSessionConfirmation(null) }} footer={<><button type="button" className="ss-btn" disabled={sessionActionBusy} onClick={() => setSessionConfirmation(null)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" disabled={sessionActionBusy} onClick={() => void confirmSessionAction()}>{sessionActionBusy ? 'İşlem Yapılıyor…' : sessionConfirmationCopy.confirm}</button></>}>
      <div className={`ss-callout${sessionConfirmation?.action.startsWith('delete') ? '' : ' is-warn'}`}><SettingsIcon name="alert" /><span><b>Bu işlem hesap oturumlarını etkiler.</b> Devam etmeden önce yapmak istediğiniz işlemi kontrol edin.</span></div>
      {affectedSessions.length ? <div className="ss-sum">{affectedSessions.map(session => <div key={session.id}><SettingsGlyph state={session.state === 'ACTIVE' ? 'done' : 'none'} /><span>Diğer Oturum<small>Son etkinlik {formatSettingsDate(session.lastSeenAt)}</small></span><span className="ss-sum-r">Bitiş {formatSettingsDate(session.expiresAt)}</span></div>)}</div> : null}
    </Modal>}

  </section>
}

// The form starts from the saved settings: until they are in (the remembered copy is only a head start for the
// colours) it would show the defaults, and its theme picker would keep the default theme it opened with.
function AppearanceSettingsSection({ section, onStatus }: { section: AppearanceSection | null; onStatus: (status: { dirty: boolean; themeCount: number }) => void }) {
  const appearance = useAppearanceSettings()
  if (appearance.isPending || appearance.isPlaceholderData) return section ? <div className="ss-body"><LoadingState>Görünüm ayarları yükleniyor…</LoadingState></div> : null
  return <AppearanceSettingsForm section={section} onStatus={onStatus} />
}

function AppearanceSettingsForm({ section, onStatus }: { section: AppearanceSection | null; onStatus: (status: { dirty: boolean; themeCount: number }) => void }) {
  const appearance = useAppearanceSettings()
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const succeed = (message: string) => setNotice({ kind: 'success', message })
  const fail = (message: string) => setNotice({ kind: 'error', message })
  const note = (message: string) => setNotice({ kind: 'info', message })
  const [busy, setBusy] = useState(false)
  const [newThemeName, setNewThemeName] = useState('')
  const [themeMenuOpen, setThemeMenuOpen] = useState(false)
  // Blur alone missed presses that move no focus (Safari does not focus a pressed button), so the list stayed open.
  const themePickerRef = useRef<HTMLDivElement>(null)
  useDismissable(themeMenuOpen, [themePickerRef], () => setThemeMenuOpen(false))
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

  // The theme event arrives late when the switch runs inside a View Transition, after a theme button already chose its
  // dark theme; it must not overwrite that choice. Coming from light (the top bar switch), the dark theme highlighted is
  // the one whose palette the dark draft carries, not always the default.
  const darkThemeIdFromDraft = useEffectEvent(() => matchingColorThemeId(appearance.draft))
  useEffect(() => {
    const syncVisualTheme = (event: Event) => {
      const nextTheme = (event as CustomEvent<VisualTheme>).detail
      if (nextTheme === 'light' || nextTheme === 'dark') {
        setVisualTheme(nextTheme)
        const draftDarkThemeId = darkThemeIdFromDraft()
        setSelectedThemeId(current => nextTheme === 'light' ? 'default-light' : current === 'default-light' ? draftDarkThemeId : current)
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
    const savedTheme: VisualTheme = readVisualThemePreference()
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
    switchVisualThemeSmoothly('dark')
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
    switchVisualThemeSmoothly('dark')
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
    switchVisualThemeSmoothly('light')
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
  const bar = <SettingsBar title="Görünüm Ayarlarını Uygula" dirty={dirty} copy={dirty ? 'Önizlemedesiniz · kaydedilmemiş değişiklikler var' : undefined}><button type="button" className="ss-btn" onClick={applyDefaultAppearance} disabled={busy}><SettingsIcon name="reset" /><span className="ss-lbl-long">Varsayılanlara Dön</span><span className="ss-lbl-short">Varsayılan</span></button><button type="button" className="ss-btn is-primary" onClick={() => void save()} disabled={busy || !dirty}><SettingsIcon name="save" />{busy ? 'Kaydediliyor…' : <><span className="ss-lbl-long">Görünüm Ayarlarını Kaydet</span><span className="ss-lbl-short">Kaydet</span></>}</button></SettingsBar>
  const loadError = appearance.isError ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap görünüm ayarları alınamadı; varsayılan görünüm gösteriliyor.</span></div> : null

  return <>
    {notice && <SettingsNotice {...notice} />}
    {section === 'read' && <>
      <SettingsPaneHead section="read" />
      <div className="ss-body">
        {loadError}
        <div className="ss-app-grid">
          <label className="ss-fld"><small>Font Tipi</small><PanelSelect ariaLabel="Font Tipi" value={appearance.draft.fontFamily} onChange={next => appearance.setDraft({ ...appearance.draft, fontFamily: next })} options={appearanceFontFamilyOptions.map(option => ({ value: option.value, label: option.label }))} /></label>
          <label className="ss-fld"><small>Yazı Boyutu</small><PanelSelect ariaLabel="Yazı Boyutu" value={appearance.draft.fontSize} onChange={next => appearance.setDraft({ ...appearance.draft, fontSize: next })} options={appearanceFontSizeOptions.map(option => ({ value: option.value, label: `${option.label} — ${option.description}` }))} /></label>
          <div className="ss-fld"><small>Tema</small><div className="ss-picker" ref={themePickerRef} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setThemeMenuOpen(false) }}>
            <button type="button" className={`ss-pick-btn${themeMenuOpen ? ' is-on' : ''}`} aria-label="Arayüz teması" aria-haspopup="listbox" aria-expanded={themeMenuOpen} onClick={() => setThemeMenuOpen(value => !value)} onKeyDown={event => { if (event.key === 'Escape') setThemeMenuOpen(false) }}><SettingsSwatches palette={themePalette(activeSelectableTheme)} /><span>{activeSelectableTheme.label}</span><SettingsIcon name="chev" /></button>
            {themeMenuOpen && <div className="ss-pick-menu" role="listbox" aria-label="Arayüz teması seçenekleri">{selectableThemes.map((theme, index) => <Fragment key={theme.id}>{index === 1 ? <div className="ss-pick-sep" /> : null}<button type="button" role="option" aria-selected={theme.id === activeSelectableTheme.id} className={`ss-pick-opt${theme.id === activeSelectableTheme.id ? ' is-on' : ''}`} onMouseDown={event => event.preventDefault()} onClick={() => { if (theme.mode === 'light') applyLightTheme(); else if (theme.profile) applyColorTheme(theme.profile); setThemeMenuOpen(false) }}><SettingsSwatches palette={themePalette(theme)} /><span><b>{theme.label}</b><small>{theme.description}</small></span>{theme.id === activeSelectableTheme.id ? <SettingsGlyph state="done" /> : <span />}</button></Fragment>)}</div>}
          </div></div>
        </div>
        {preview}
      </div>
      {bar}
    </>}
    {section === 'palette' && <>
      <SettingsPaneHead section="palette" description={visualTheme === 'light' ? undefined : 'Değişiklikler kaydetmeden önce anlık önizlenir.'} actions={<><span className="ss-tag is-ink">{activeThemeLabel}{dirty ? ' · Düzenlendi' : ''}</span>{visualTheme === 'light' && <button type="button" className="ss-btn" onClick={resetLightPalette}><SettingsIcon name="reset" />Varsayılana Döndür</button>}</>} />
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
        <div className="ss-themes">{appearance.draft.colorThemes.map(theme => <div key={theme.id} className={`ss-th${visualTheme === 'dark' && selectedThemeId === theme.id ? ' is-on' : ''}`}><button type="button" className="ss-th-pick" onClick={() => applyColorTheme(theme)}><SettingsSwatches palette={theme.palette} large /><span><b>{theme.name}</b><small>{theme.builtIn ? 'Varsayılan Tema' : 'Kayıtlı Özel Tema'}</small></span></button>{theme.builtIn ? <span className="ss-th-prot" title="Varsayılan tema korunur ve silinemez" aria-label="Korunuyor"><SettingsIcon name="lock" /></span> : <button type="button" className="ss-btn is-danger is-quiet is-xs" disabled={busy} onClick={() => setThemeToDelete(theme)} aria-label={`${theme.name} temasını sil`}>Sil</button>}</div>)}</div>
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
const shippingCaptionStyleOptions: Array<[ShippingLabelCaptionStyle, string]> = [['above', 'Başlık Üstte'], ['beside', 'Başlık Yanında'], ['none', 'Başlıksız']]

function ShippingLabelSettingsPanel({ section, settings, saved, dirty, saving, loadFailed, loading, format, setFormat, onChange, onSave }: { section: ShippingSection | null; settings: ShippingLabelSettings; saved: ShippingLabelSettings | null; dirty: boolean; saving: boolean; loadFailed: boolean; loading: boolean; format: 'a4' | 'sticker'; setFormat: (format: 'a4' | 'sticker') => void; onChange: (value: ShippingLabelSettings) => void; onSave: () => void }) {
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
  // A press on a block: it becomes a drag only after the pointer has moved past the threshold, so a click only selects.
  const pointerDrag = useRef<{ id: string; startClientX: number; startClientY: number; position: ReturnType<typeof positionFor>; dragging: boolean } | null>(null)
  const pointerResize = useRef<{ id: string; direction: ShippingResizeDirection; startClientX: number; startClientY: number; position: ReturnType<typeof positionFor> } | null>(null)
  const pointerRotate = useRef<{ id: string; startAngle: number; startRotation: number } | null>(null)
  const [stickerWidthDraft, setStickerWidthDraft] = useState(() => String(settings.stickerWidthMm))
  const [stickerHeightDraft, setStickerHeightDraft] = useState(() => String(settings.stickerHeightMm))
  const layout = settings.layout[format]
  const activeBlock = layout.find(block => block.id === selectedId) ?? layout[0] ?? null
  const selectedTemplate = templates.find(item => item.id === selectedTemplateId)
  // The paper is the label at its real size in millimetres, scaled to the stage, so its text wraps as it prints.
  const paperSize = shippingLabelSizeMm(format, settings)
  const paperRef = useRef<HTMLDivElement>(null)
  const [overflowingIds, setOverflowingIds] = useState<string[]>([])
  useLayoutEffect(() => {
    const ids = overflowingLabelBlocks(paperRef.current)
    setOverflowingIds(current => current.join('|') === ids.join('|') ? current : ids)
  }, [layout, settings.showCustomerPhone, paperSize.width, paperSize.height, section])
  const layoutIssues = shippingLabelLayoutIssues(layout, paperSize, { trackingNumber: shippingPreviewValues.trackingNumber, packageNumber: shippingPreviewValues.packageNumber })
  const warnedIds = new Set([...overflowingIds, ...layoutIssues.flatMap(issue => issue.kind === 'overlap' ? issue.blockIds : [issue.blockId])])
  const issueTexts = [
    ...overflowingIds.map(id => layout.find(block => block.id === id)).filter(block => block != null).map(block => `${blockTitle(block)}: yazı bloğa sığmıyor; bloğu büyütün ya da yazı boyutunu küçültün.`),
    ...layoutIssues.map(issue => issue.kind === 'overlap'
      ? `${issue.blockIds.map(id => layout.find(block => block.id === id)).filter(block => block != null).map(blockTitle).join(' ve ')} üst üste biniyor.`
      : issue.kind === 'narrowBarcode' ? `${blockTitle(layout.find(block => block.id === issue.blockId)!)}: barkod çok dar (çubuk ${issue.moduleMm.toFixed(2).replace('.', ',')} mm); okutulamayabilir, bloğu genişletin.` : '')
  ].filter(Boolean)
  function tidyLayout() {
    const paper = paperRef.current
    if (!paper) return
    const contentHeights: Record<string, number> = {}
    for (const body of Array.from(paper.querySelectorAll<HTMLElement>('.lbl-body[data-block-id]'))) contentHeights[body.dataset.blockId!] = labelBlockContentHeight(body) / paper.clientHeight * 100
    const next = stackShippingLabelLayout(layout, { contentHeights, gapPercent: settings.sectionGapMm / paperSize.height * 100 })
    if (!next) { setTemplateNotice({ kind: 'error', message: 'Bloklar alt alta etikete sığmıyor; bir bloğu kaldırın ya da yazı boyutunu küçültün.' }); return }
    onChange({ ...settings, layout: { ...settings.layout, [format]: next } })
    setTemplateNotice({ kind: 'success', message: `Bloklar içeriklerine göre boyutlandı ve aralarında ${settings.sectionGapMm} mm boşlukla dizildi.` })
  }

  const selectFirstBlock = useEffectEvent(() => setSelectedId(settings.layout[format][0]?.id ?? null))
  useEffect(() => selectFirstBlock(), [format])
  useEffect(() => { setStickerWidthDraft(String(settings.stickerWidthMm)) }, [settings.stickerWidthMm])
  useEffect(() => { setStickerHeightDraft(String(settings.stickerHeightMm)) }, [settings.stickerHeightMm])

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
  function beginPointerDrag(event: ReactPointerEvent<HTMLElement>, block: ShippingLabelBlock) {
    if (event.button !== 0) return
    pointerDrag.current = { id: block.id, startClientX: event.clientX, startClientY: event.clientY, position: positionFor(block, layout.indexOf(block)), dragging: false }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture?.(event.pointerId)
    event.preventDefault()
  }
  function movePointerDrag(event: ReactPointerEvent<HTMLElement>) {
    const drag = pointerDrag.current
    if (!drag) return
    const delta = { x: event.clientX - drag.startClientX, y: event.clientY - drag.startClientY }
    if (!drag.dragging && !shippingLabelDragStarted(delta)) return
    drag.dragging = true
    // The paper as drawn (its zoom included), the same space as the pointer's movement.
    const paperRect = event.currentTarget.closest<HTMLElement>('.shipping-designer-paper')?.getBoundingClientRect()
    const block = layout.find(item => item.id === drag.id)
    if (!paperRect || !paperRect.width || !paperRect.height || !block) return
    updateBlock(block.id, { position: draggedShippingLabelPosition(drag.position, block.rotation ?? 0, delta, paperRect) })
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
    const block = layout.find(item => item.id === resize.id)
    if (!rect.width || !rect.height || !block) return
    // In the block's own, turned axes: the dragged edge follows the pointer and the opposite edge stays put.
    updateBlock(resize.id, { position: resizedShippingLabelPosition(resize.position, block.rotation ?? 0, resize.direction, { x: event.clientX - resize.startClientX, y: event.clientY - resize.startClientY }, rect) })
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
      const nextSettings = normalizeShippingLabelSettings(template.settings) // templates saved before the text sizes or bounds were set come back normalized
      const nextLayout = nextSettings.layout[format]
      setSelectedId(nextLayout[0]?.id ?? null)
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
    const next = cloneShippingLabelLayout(defaultShippingLabelSettings.layout[format])
    // The sticker default is drawn for Trendyol Express' 100 x 100 mm sticker, so it brings that size with it.
    const size = format === 'sticker' ? { stickerWidthMm: defaultShippingLabelSettings.stickerWidthMm, stickerHeightMm: defaultShippingLabelSettings.stickerHeightMm } : {}
    onChange({ ...settings, ...size, layout: { ...settings.layout, [format]: next } })
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
  function blockTitle(block: ShippingLabelBlock) { return block.kind === 'custom' ? block.title : shippingBlockLabels[block.kind] }
  const alignSegment = (block: ShippingLabelBlock) => <div className="ss-seg3" role="group" aria-label="Hizalama">{shippingAlignOptions.map(([value, label]) => <button type="button" key={value} aria-pressed={block.align === value} className={block.align === value ? 'is-on' : undefined} onClick={() => updateBlock(block.id, { align: value })}>{label}</button>)}</div>
  const bar = <SettingsBar title="Etiket Düzenini Yayınla" dirty={dirty} hint="Gönderici bilgileri ve kâğıt yerleşimi tüm yazdırma akışlarında kullanılır." copy={dirty ? 'Kaydedilmemiş değişiklikler var' : undefined}>{dirty && saved ? <button type="button" className="ss-btn" disabled={saving} onClick={() => onChange(saved)}>Vazgeç</button> : null}<button type="button" className="ss-btn is-primary" disabled={!dirty || saving} onClick={onSave}><SettingsIcon name="save" />{saving ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button></SettingsBar>
  const loadError = loadFailed ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap kargo ayarları alınamadı; geçici yerel ayarlar gösteriliyor.</span></div> : null
  // The browser's copy is shown only when the account's settings cannot be read, never before they arrive.
  if (loading) return section ? <div className="ss-body"><LoadingState>Kargo ayarları yükleniyor…</LoadingState></div> : null

  return <>
    {templateNotice && <SettingsNotice {...templateNotice} />}
    {section === 'sender' && <>
      <SettingsPaneHead section="sender" />
      <div className="ss-body">
        {loadError}
        <div className="ss-pair"><label className="ss-fld"><small>Gönderici Adı</small><input className="ss-in" value={settings.senderName} maxLength={120} onChange={event => update('senderName', event.target.value)} /></label><label className="ss-fld"><small>Varsayılan Kargo Çıktısı</small><PanelSelect ariaLabel="Varsayılan Kargo Çıktısı" value={settings.defaultFormat} onChange={next => update('defaultFormat', next)} options={[{ value: 'a4', label: 'A4' }, { value: 'sticker', label: 'Sticker' }]} /></label></div>
        <label className="ss-fld"><small>Gönderici Adresi</small><textarea className="ss-in" value={settings.senderAddress} maxLength={500} rows={3} onChange={event => update('senderAddress', event.target.value)} placeholder="İsteğe bağlı" /></label>
        <label className="ss-fld"><small>A4 Sayfa Düzeni</small><PanelSelect ariaLabel="A4 Sayfa Düzeni" value={String(settings.a4LabelsPerPage)} onChange={next => update('a4LabelsPerPage', Number(next) as ShippingLabelSettings['a4LabelsPerPage'])} options={[{ value: '1', label: 'Sayfada 1 Etiket' }, { value: '2', label: 'Sayfada 2 Etiket' }, { value: '4', label: 'Sayfada 4 Etiket' }]} /></label>
        <div className="ss-pair"><SettingsSwitch label="A4 Yazdırma Butonunu Göster" checked={settings.showA4Button} onChange={checked => update('showA4Button', checked)} /><SettingsSwitch label="Sticker Yazdırma Butonunu Göster" checked={settings.showStickerButton} onChange={checked => update('showStickerButton', checked)} /></div>
      </div>
      {bar}
    </>}
    {section === 'dims' && <>
      <SettingsPaneHead section="dims" />
      <div className="ss-body">
        {loadError}
        <div className="ss-pair"><label className="ss-fld"><small>Sticker Genişliği (mm)</small><input className="ss-in" type="number" min={40} max={300} value={stickerWidthDraft} onChange={event => setStickerWidthDraft(event.target.value)} onBlur={() => commitDimension('stickerWidthMm', stickerWidthDraft, settings.stickerWidthMm)} /></label><label className="ss-fld"><small>Sticker Yüksekliği (mm)</small><input className="ss-in" type="number" min={40} max={300} value={stickerHeightDraft} onChange={event => setStickerHeightDraft(event.target.value)} onBlur={() => commitDimension('stickerHeightMm', stickerHeightDraft, settings.stickerHeightMm)} /></label></div>
        <div className="ss-pair"><label className="ss-fld"><small>Bloklar Arası Boşluk (mm)</small><input className="ss-in" type="number" min={0} max={20} value={settings.sectionGapMm} onChange={event => update('sectionGapMm', Math.min(20, Math.max(0, Number(event.target.value) || 0)))} /></label><SettingsSwitch label="Müşteri İletişim Alanını Göster" checked={settings.showCustomerPhone} onChange={checked => update('showCustomerPhone', checked)} /></div>
      </div>
      {bar}
    </>}
    {section === 'designer' && <>
      <SettingsPaneHead section="designer" />
      <div className="ss-body is-flush"><div className="ss-dz">
        <div className="ss-dz-bar">
          <span className="ss-seg2 rv-tabs rv-view-tabs" role="tablist" aria-label="Kâğıt biçimi"><button type="button" role="tab" aria-selected={format === 'a4'} className={format === 'a4' ? 'is-on is-active' : undefined} onClick={() => setFormat('a4')}><span className="rv-tab-label"><SettingsIcon name="copy" /><span className="rv-tab-text" data-text="A4">A4</span> <small>{settings.a4LabelsPerPage} Etiket</small></span></button><button type="button" role="tab" aria-selected={format === 'sticker'} className={format === 'sticker' ? 'is-on is-active' : undefined} onClick={() => setFormat('sticker')}><span className="rv-tab-label"><SettingsIcon name="barcode" /><span className="rv-tab-text" data-text="Sticker">Sticker</span> <small>{settings.stickerWidthMm} × {settings.stickerHeightMm} mm</small></span></button></span>
          <span className="ss-grow" />
          <PanelSelect className="ss-dz-template" ariaLabel="Kayıtlı şablon seçin" value={selectedTemplateId} onChange={applyTemplate} placeholder="Şablon Seçin…" options={templates.map(template => ({ value: template.id, label: template.name }))} />
          <button type="button" className="ss-btn" onClick={() => { setTemplateNameDraft(`${format === 'a4' ? 'A4' : 'Sticker'} şablonu`); setTemplateNotice(null); setTemplateNameOpen(true) }}><SettingsIcon name="save" />Şablon Kaydet</button>
          <button type="button" className="ss-btn" disabled={!selectedTemplateId} onClick={() => setTemplateDeleteOpen(true)}><SettingsIcon name="trash" />Şablonu Sil</button>
          <button type="button" className="ss-btn" title="Blokları içeriklerine göre boyutlandırıp aralarında eşit boşlukla alt alta dizer" onClick={tidyLayout}><SettingsIcon name="layout" />Düzeni Toparla</button>
          <button type="button" className="ss-btn is-quiet" onClick={() => setLayoutResetOpen(true)}><SettingsIcon name="reset" />Varsayılanı Yükle</button>
        </div>
        <div className="ss-dz-work">
          <aside className="ss-dz-lib">
            <div className="ss-dz-head"><b>İçerik Kütüphanesi<TitleHint label="İçerik Kütüphanesi">Sürükleyip kâğıda bırakın veya tıklayıp ekleyin.</TitleHint></b></div>
            <div className="ss-lib-list">{shippingLabelBlockCatalog.map(item => { const repeatableBarcode = item.kind === 'trackingBarcode' || item.kind === 'packageBarcode'; return <button type="button" key={item.kind} className="ss-lib-it" draggable onDragStart={event => dragStart(event, `catalog:${item.kind}`)} onClick={() => addBlock(item.kind)}><span className="ss-tile is-sm"><SettingsIcon name={repeatableBarcode ? 'barcode' : item.kind === 'custom' ? 'sparkle' : 'layout'} /></span><span><b>{shippingBlockLabels[item.kind]}</b><small>{item.description} · birden fazla eklenebilir</small></span><SettingsIcon name="plus" /></button> })}</div>
            <div className="ss-lib-custom"><label className="ss-fld"><small>Özel Blok Başlığı</small><input className="ss-in" value={customTitle} onChange={event => setCustomTitle(event.target.value)} placeholder="Örn. mağaza notu" /></label><label className="ss-fld"><small>Özel Blok İçeriği</small><textarea className="ss-in" value={customText} onChange={event => setCustomText(event.target.value)} rows={3} placeholder="Bu blokta görünecek metin" /></label></div>
          </aside>
          <main className="ss-dz-stage">
            <div className="ss-dz-sbar"><b>{format === 'a4' ? 'A4 Çalışma Yüzeyi' : 'Sticker Çalışma Yüzeyi'}</b><span>{layout.length} blok yerleşti</span></div>
            {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/no-noninteractive-tabindex -- blocks inside the application area: pointer drags, and on the keyboard Tab focuses (selects) a block and the arrow keys move it (moveBlockByKey); the pointer-only resize/rotate handles stay out of the Tab order */}
            <div className="ss-dz-canvas"><ShippingLabelFit widthMm={paperSize.width} heightMm={paperSize.height} maxHeight={720}><div ref={paperRef} className={`shipping-designer-paper lbl-sheet is-${format}`} onDragOver={event => event.preventDefault()} onDrop={dropOnCanvas} style={{ width: `${paperSize.width}mm`, height: `${paperSize.height}mm` }} role="application" aria-label={`${format === 'a4' ? 'A4' : 'Sticker'} etiket tasarım alanı`} aria-describedby="shipping-designer-help">{layout.map((block, index) => { const position = positionFor(block, index); return <article key={block.id} className={`shipping-designer-block${activeBlock?.id === block.id ? ' is-selected' : ''}${warnedIds.has(block.id) ? ' is-warning' : ''}`} tabIndex={0} aria-roledescription="etiket bloğu" aria-label={`${blockTitle(block)} · solda %${position.x}, üstte %${position.y}`} onFocus={() => setSelectedId(block.id)} onKeyDown={event => moveBlockByKey(event, block, index)} onPointerDown={event => beginPointerDrag(event, block)} onPointerMove={movePointerDrag} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} onClick={() => setSelectedId(block.id)} style={{ left: `${position.x}%`, top: `${position.y}%`, width: `${position.width}%`, height: `${position.height}%`, transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined, transformOrigin: 'center' }}>{activeBlock?.id === block.id && <>{shippingRotateDirections.map(direction => <button key={direction} type="button" tabIndex={-1} className={`shipping-designer-resize-handle shipping-designer-rotate-handle handle-${direction}`} aria-label={`${shippingRotateDirectionLabels[direction]} tutamacını sağa veya sola sürükleyerek derece derece döndür`} title={`${shippingRotateDirectionLabels[direction]} tutamacını sürükleyerek derece derece döndür`} onPointerDown={event => beginPointerRotate(event, block)} onPointerMove={movePointerRotate} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}{shippingResizeDirections.map(direction => <button key={direction} type="button" tabIndex={-1} className={`shipping-designer-resize-handle handle-${direction}`} aria-label={`${shippingResizeDirectionLabels[direction]} boyutlandır`} title={`${shippingResizeDirectionLabels[direction]} boyutlandır`} onPointerDown={event => beginPointerResize(event, block, direction)} onPointerMove={movePointerResize} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}</>}<ShippingLabelBlockBody block={block} values={shippingPreviewValues} showCustomerPhone={settings.showCustomerPhone} /></article> })}{layout.length === 0 && <div className="shipping-designer-drop-empty"><strong>İçerik Bırakın</strong><span>Kütüphaneden bir alanı buraya sürükleyin.</span></div>}</div></ShippingLabelFit></div>
            {issueTexts.length > 0 && <div className="ss-callout is-warning ss-dz-issues" role="status"><SettingsIcon name="alert" /><span><b>Baskıda Sorun Çıkabilir</b>{issueTexts.map(text => <small key={text}>{text}</small>)}</span></div>}
            <span className="sr-only" id="shipping-designer-help">Tab ile bloğu seçip ok tuşlarıyla taşıyın (Shift ile 5 kat).</span>
          </main>
          <aside className="ss-dz-insp">
            <div className="ss-dz-head"><b>Özellikler<TitleHint label="Özellikler">{designerTab === 'general' ? 'Boyutu kâğıt üzerindeki tutamaçlardan ayarlayın.' : designerTab === 'text' ? 'Değerler kalın, başlıklar küçük ve normal yazılır; yazı tipi tek, renk siyahtır. Gerçek değerler yazdırırken doldurulur.' : 'Barkod genişliği blok genişliğine göre ölçeklenir; okunabilirlik korunur.'}</TitleHint></b><small>{activeBlock ? `Seçili: ${blockTitle(activeBlock)}` : 'Bir alan seçin'}</small></div>
            <div className="ss-itabs rv-tabs rv-view-tabs" role="tablist" aria-label="Özellik grupları">{([['general', 'Genel', 'layout'], ['text', 'Yazı', 'type'], ['barcode', 'Barkod', 'barcode']] as const).map(([value, label, icon]) => <button type="button" role="tab" key={value} aria-selected={designerTab === value} className={designerTab === value ? 'is-on is-active' : undefined} onClick={() => setDesignerTab(value)}><span className="rv-tab-label"><SettingsIcon name={icon} /><span className="rv-tab-text" data-text={label}>{label}</span></span></button>)}</div>
            {!activeBlock ? <div className="ss-empty">Düzenlemek için kâğıt üzerindeki bir bloğa tıklayın.</div> : <div className="ss-ibody">
              {designerTab === 'general' && <>
                <label className="ss-fld"><small>Blok Türü</small><PanelSelect ariaLabel="Blok Türü" value={activeBlock.kind} onChange={next => { const kind = next as ShippingLabelBlockKind; const catalog = shippingLabelBlockCatalog.find(item => item.kind === kind); if (catalog) updateBlock(activeBlock.id, { kind, title: kind === 'custom' ? activeBlock.title : catalog.label, fields: [...catalog.fields], text: kind === 'custom' ? activeBlock.text : '' }) }} options={shippingLabelBlockCatalog.map(item => ({ value: item.kind, label: shippingBlockLabels[item.kind] }))} /></label>
                <div className="ss-fld"><small>Hizalama</small>{alignSegment(activeBlock)}</div>
                {activeBlock.fields.length > 0 && activeBlock.kind !== 'trackingBarcode' && activeBlock.kind !== 'packageBarcode' && <>
                  <label className="ss-fld"><small>Alan Başlığı</small><PanelSelect ariaLabel="Alan Başlığı" value={shippingLabelCaptionStyle(activeBlock)} onChange={next => { const style = next as ShippingLabelCaptionStyle; updateBlock(activeBlock.id, { inline: undefined, captionStyle: style === 'above' ? undefined : style }) }} options={shippingCaptionStyleOptions.map(([value, label]) => ({ value, label }))} /></label>
                  <div className="ss-fld"><small>Satırdaki Alan Sayısı</small><div className="ss-seg3" role="group" aria-label="Satırdaki alan sayısı">{([1, 2, 3] as const).map(count => <button type="button" key={count} aria-pressed={shippingLabelFieldsPerRow(activeBlock) === count} className={shippingLabelFieldsPerRow(activeBlock) === count ? 'is-on' : undefined} title={count === 1 ? 'Her alan kendi satırında' : `${count} alan yan yana, aynı satırda`} onClick={() => updateBlock(activeBlock.id, { fieldsPerRow: count === 1 ? undefined : count })}>{count}</button>)}</div></div>
                </>}
                <div className="ss-fld"><small>Çerçeve</small><div className="ss-seg3 is-two" role="group" aria-label="Çerçeve">{([[false, 'Yok'], [true, 'Var']] as const).map(([boxed, label]) => <button type="button" key={label} aria-pressed={Boolean(activeBlock.boxed) === boxed} className={Boolean(activeBlock.boxed) === boxed ? 'is-on' : undefined} onClick={() => updateBlock(activeBlock.id, { boxed: boxed || undefined })}>{label}</button>)}</div></div>
                <label className="ss-fld"><small>Alana Ekle</small><PanelSelect ariaLabel="Alana Ekle" value="" onChange={next => { if (next) toggleField(activeBlock, next as ShippingLabelField) }} placeholder="Bir Alan Seçin…" options={shippingLabelFields.filter(item => !activeBlock.fields.includes(item.id)).map(item => ({ value: item.id, label: item.label }))} /></label>
                <div className="ss-fchips">{activeBlock.fields.length ? activeBlock.fields.map(field => <button type="button" key={field} className="ss-fchip" title="Alanı çıkar" onClick={() => toggleField(activeBlock, field)}>{shippingLabelFields.find(item => item.id === field)?.label}<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><path d="m4 4 8 8M12 4l-8 8" /></svg></button>) : <span className="ss-faint">Alan eklenmedi</span>}</div>
              </>}
              {designerTab === 'text' && <>
                <div className="ss-fld"><small>Yazı Hizası</small>{alignSegment(activeBlock)}</div>
                <div className="ss-fld"><small>Yazı Boyutu</small><div className="ss-seg3" role="group" aria-label="Yazı boyutu">{shippingLabelTextSizes.map(size => <button type="button" key={size.px} aria-pressed={shippingLabelTextSize(activeBlock.fontSize) === size.px} className={shippingLabelTextSize(activeBlock.fontSize) === size.px ? 'is-on' : undefined} onClick={() => updateBlock(activeBlock.id, { fontSize: size.px })}>{size.label}</button>)}</div></div>
                <div className="ss-fld"><small>Büyük Harf</small><div className="ss-seg3 is-two" role="group" aria-label="Büyük harf">{([[false, 'Kapalı'], [true, 'Açık']] as const).map(([uppercase, label]) => <button type="button" key={label} aria-pressed={Boolean(activeBlock.uppercase) === uppercase} className={Boolean(activeBlock.uppercase) === uppercase ? 'is-on' : undefined} onClick={() => updateBlock(activeBlock.id, { uppercase: uppercase || undefined })}>{label}</button>)}</div></div>
                <div className="ss-fld"><small>Yazıyı Kutuya Sığdır</small><div className="ss-seg3 is-two" role="group" aria-label="Yazıyı kutuya sığdır">{([[false, 'Kapalı'], [true, 'Açık']] as const).map(([fit, label]) => <button type="button" key={label} aria-pressed={shippingLabelFitsText(activeBlock) === fit} className={shippingLabelFitsText(activeBlock) === fit ? 'is-on' : undefined} title={fit ? 'Yazı, bloğu dolduracak kadar büyür ya da sığacak kadar küçülür' : 'Yazı seçilen boyutta kalır'} onClick={() => updateBlock(activeBlock.id, { fitText: fit })}>{label}</button>)}</div></div>
                {activeBlock.kind === 'custom' && <label className="ss-fld"><small>Metin</small><textarea className="ss-in" value={activeBlock.text} maxLength={500} rows={5} onChange={event => updateBlock(activeBlock.id, { text: event.target.value })} /></label>}
              </>}
              {designerTab === 'barcode' && <>
                <div className="ss-fld"><b className="ss-ibody-title">{activeBlock.kind === 'packageBarcode' ? 'Paket Barkodu' : activeBlock.kind === 'trackingBarcode' ? 'Takip Barkodu' : 'Barkod Ayarı'}</b></div>
                <label className="ss-fld"><small>Alan</small><PanelSelect ariaLabel="Alan" value={activeBlock.fields[0] ?? ''} onChange={next => { if (next) updateBlock(activeBlock.id, { fields: [next as ShippingLabelField] }) }} placeholder="Alan Seçin…" options={shippingLabelFields.filter(field => field.id === 'trackingNumber' || field.id === 'packageNumber').map(field => ({ value: field.id, label: field.label }))} /></label>
                {(activeBlock.kind === 'trackingBarcode' || activeBlock.kind === 'packageBarcode') && <div className="ss-fld"><small>Barkod Numarası</small><div className="ss-seg3 is-two" role="group" aria-label="Barkod numarası">{([[true, 'Göster'], [false, 'Gizle']] as const).map(([show, label]) => <button type="button" key={label} aria-pressed={shippingLabelShowsBarcodeText(activeBlock) === show} className={shippingLabelShowsBarcodeText(activeBlock) === show ? 'is-on' : undefined} onClick={() => updateBlock(activeBlock.id, { barcodeText: show })}>{label}</button>)}</div></div>}
              </>}
              <div className="ss-rng" aria-label="Blok döndürme"><div className="ss-rng-hd"><span>Döndürme</span><output>{activeBlock.rotation ?? 0}°</output></div><div className="ss-rot"><button type="button" className="ss-btn" aria-label="Sola 90 derece döndür" title="Sola 90 derece döndür" onClick={() => rotateActiveBlock(-90)}>−90°</button><button type="button" className="ss-btn" aria-label="Sağa 90 derece döndür" title="Sağa 90 derece döndür" onClick={() => rotateActiveBlock(90)}>+90°</button><button type="button" className="ss-btn" aria-label="Döndürmeyi sıfırla" title="Döndürmeyi sıfırla" onClick={() => updateBlock(activeBlock.id, { rotation: 0 })} disabled={!activeBlock.rotation}>0°</button></div></div>
              <button type="button" className="ss-btn is-danger is-quiet ss-blk-del" onClick={() => removeBlock(activeBlock.id)}><SettingsIcon name="trash" />Bloğu Kaldır</button>
            </div>}
          </aside>
        </div>
      </div></div>
      {bar}
    </>}

    <Modal open={templateNameOpen} title="Şablon Kaydet" hint="Geçerli kâğıt yerleşimini bir adla saklayın; sonra tek seçimle geri yükleyin. Şablonlar bu tarayıcıda saklanır." className="ss-modal is-sm" onClose={() => setTemplateNameOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateNameOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" disabled={!templateNameDraft.trim()} onClick={() => saveTemplate(templateNameDraft)}>Kaydet</button></>}>
      <label className="ss-fld"><small>Şablon Adı</small><input className="ss-in" data-autofocus value={templateNameDraft} maxLength={80} onChange={event => setTemplateNameDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') saveTemplate(templateNameDraft) }} /></label>
      <div className="ss-sum"><div><SettingsIcon name="layout" /><span>{format === 'a4' ? `A4 · Sayfada ${settings.a4LabelsPerPage} Etiket` : `Sticker · ${settings.stickerWidthMm} × ${settings.stickerHeightMm} mm`}<small>{layout.length} blok · gönderici {settings.senderName || '—'}</small></span><span /></div></div>
    </Modal>
    <Modal open={templateDeleteOpen && Boolean(selectedTemplate)} title="Şablonu Sil" description={selectedTemplate ? `“${selectedTemplate.name}” şablonu bu tarayıcıdan silinecek. Kâğıttaki yerleşim değişmez.` : undefined} className="ss-modal is-sm" onClose={() => setTemplateDeleteOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateDeleteOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" onClick={() => { deleteTemplate(); setTemplateDeleteOpen(false) }}>Şablonu Sil</button></>}>
      <div className="ss-callout"><SettingsIcon name="alert" /><span><b>Bu işlem geri alınamaz.</b> Aynı yerleşimi yeniden kullanmak için önce yeni adla kaydedin.</span></div>
    </Modal>
    <Modal open={layoutResetOpen} title="Varsayılan Yerleşimi Yükle" description={format === 'sticker' ? 'Kâğıttaki blokların yerine Trendyol Express’in 100 × 100 mm sticker’ına benzeyen varsayılan yerleşim gelir; sticker ölçüsü de 100 × 100 mm olur. Kaydedene kadar hesabınızdaki düzen değişmez.' : 'Kâğıttaki blokların yerine varsayılan yerleşim gelir. Kaydedene kadar hesabınızdaki düzen değişmez.'} className="ss-modal is-sm" onClose={() => setLayoutResetOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setLayoutResetOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" onClick={() => { resetLayout(); setLayoutResetOpen(false) }}>Varsayılanı Yükle</button></>}>
      <div className="ss-sum"><div><SettingsGlyph state="none" /><span>Şu Anki Yerleşim<small>{layout.length} blok · {layout.filter(block => block.rotation).length} döndürülmüş</small></span><span /></div><div><SettingsGlyph state="done" /><span>Varsayılan Yerleşim<small>{defaultShippingLabelSettings.layout[format].length} blok · {defaultShippingLabelSettings.layout[format].map(block => shippingBlockLabels[block.kind].toLocaleLowerCase('tr-TR')).join(', ')}</small></span><span /></div></div>
    </Modal>
  </>
}


// What the designer fills its label with: one realistic order, so a block shows the height a real one needs.
const shippingPreviewValues: ShippingLabelValues = { trackingNumber: '73300036563130080', packageNumber: '4293901515', orderNumber: '#11771694031', customerName: 'Fehime Mat', address: 'Gümüşsuyu Mah. Selime Hatun Cami Sok. No:3 D:5\nFındıklı / Beyoğlu / İstanbul', cargoProvider: 'Trendyol Express', senderName: 'Ravencia', senderAddress: '403. Cad. No:24/2\nKarşıyaka / İzmir', customerEmail: 'musteri@example.com' }

// Sistem Ayarları › Google Drive Yedeği (offsite-backup.ts, OffsiteBackupEndpoints): the worker sends the newest verified
// backup set, encrypted to the owner's age PUBLIC key, to the owner's Google Drive twice a day. The owner's own Google OAuth
// client connects it ("Google ile Bağlan"); the client secret and the Google token are write-only (never shown again).
// Saving, connecting and disconnecting take the password again (and the authenticator code when it is on). The key pair is
// made in the browser and a copy is opened in the browser (offsite-backup-crypto.ts): no extra program is needed.
type OffsiteConfirm = 'save' | 'connect' | 'disconnect' | 'setup'
const offsiteConfirmCopy: Record<OffsiteConfirm, { title: string; hint: string; action: string; busy: string; danger?: boolean }> = {
  save: { title: 'Yedek Ayarlarını Kaydet', hint: 'Yedeğin nereye ve hangi anahtarla gönderildiği değişir; parolanızla onaylayın.', action: 'Kaydet', busy: 'Kaydediliyor…' },
  setup: { title: 'Google Drive’a Bağlan', hint: 'Parolanızı onaylayın: ayarlar kaydedilir ve Google’ın izin sayfası açılır. Parola yalnız bir kez sorulur.', action: 'Kaydet ve Google’a Git', busy: 'Hazırlanıyor…' },
  connect: { title: 'Google ile Bağlan', hint: 'Parolanızı onaylayınca Google’ın izin sayfası açılır; yedeğin gideceği Google hesabıyla giriş yapıp izin verin.', action: 'Google’a Git', busy: 'Hazırlanıyor…' },
  disconnect: { title: 'Bağlantıyı Kes', hint: 'Google yetkisi Google’da iptal edilir ve panelden silinir; yedek, yeniden bağlanana kadar gönderilmez. Drive’daki kopyalar silinmez.', action: 'Bağlantıyı Kes', busy: 'Kesiliyor…', danger: true }
}

function OffsiteBackupSection({ mfa }: { mfa: boolean }) {
  const client = useQueryClient()
  const location = useLocation()
  const [askedAt, setAskedAt] = useState<number | null>(null)
  const query = useQuery({
    queryKey: ['offsite-backup'],
    queryFn: () => hubPoll<OffsiteBackupView>('/settings/offsite-backup'),
    retry: false,
    // While a copy is running or asked for, the status follows it every 5 s, and it refreshes when the tab gets focus again.
    refetchInterval: current => offsiteCopyPending(current.state.data, askedAt) ? 5_000 : false,
    refetchOnWindowFocus: 'always'
  })
  const saved = query.data
  const [draft, setDraft] = useState<OffsiteBackupDraft | null>(null)
  const [loadedVersion, setLoadedVersion] = useState<number | null>(null)
  const [changingSecret, setChangingSecret] = useState(false)
  const [busy, setBusy] = useState<OffsiteConfirm | 'test' | 'run' | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(() => (location.state as { offsiteNotice?: SettingsNoticeState } | null)?.offsiteNotice ?? null)
  const [actionResult, setActionResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [confirm, setConfirm] = useState<OffsiteConfirm | null>(null)
  const [password, setPassword] = useState('')
  const [factor, setFactor] = useState('')
  const [confirmError, setConfirmError] = useState('')
  const [keyPair, setKeyPair] = useState<BackupKeyPair | null>(null)
  const [keyOpen, setKeyOpen] = useState(false)
  const [keyError, setKeyError] = useState('')
  const [keyDownloaded, setKeyDownloaded] = useState(false)
  const [keyStored, setKeyStored] = useState(false)
  const [setupKey, setSetupKey] = useState<BackupKeyPair | null>(null)
  const [setupError, setSetupError] = useState('')
  const [restoreOpen, setRestoreOpen] = useState(false)
  const [openRows, setOpenRows] = useState<string[]>([])
  const toggleRow = (id: string) => setOpenRows(rows => rows.includes(id) ? rows.filter(row => row !== id) : [...rows, id])
  const [pasteKey, setPasteKey] = useState(false)
  // The form starts from the saved settings, and again after a save (a new version).
  if (saved && loadedVersion !== saved.version) {
    setLoadedVersion(saved.version)
    setDraft(offsiteBackupDraft(saved))
    setChangingSecret(!saved.clientSecretSaved)
  }

  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const dirty = !!saved && !!draft && offsiteBackupDirty(saved, draft)
  const draftError = draft && saved ? offsiteBackupDraftError(draft, saved.clientSecretSaved && !changingSecret) : null
  const state = saved ? offsiteBackupState(saved) : null
  const update = (patch: Partial<OffsiteBackupDraft>) => setDraft(current => current ? { ...current, ...patch } : current)

  function openConfirm(kind: OffsiteConfirm) {
    setPassword(''); setFactor(''); setConfirmError(''); setActionResult(null); setConfirm(kind)
  }
  // The one button: key (made here, file downloaded) -> one password -> save with the defaults -> Google's consent page.
  async function startSetup() {
    if (!saved || !draft) return
    const plan = offsiteSetupPlan(saved, draft, saved.clientSecretSaved && !changingSecret)
    setSetupError('')
    if (plan.error) { setSetupError(plan.error); return }
    if (plan.needsKey) {
      try {
        const pair = await generateBackupKeyPair()
        downloadText(pair.file, backupKeyFileName)
        setSetupKey(pair)
      } catch { setSetupError('Bu tarayıcı anahtar üretemedi; güncel bir Chrome, Edge, Firefox ya da Safari deneyin.'); return }
    } else setSetupKey(null)
    setKeyStored(false)
    openConfirm('setup')
  }
  async function submitConfirm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!confirm || !saved || !draft) return
    const reauth = reauthBody(password, factor)
    setBusy(confirm); setConfirmError('')
    try {
      if (confirm === 'save') {
        const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ ...offsiteBackupSaveBody(draft, changingSecret), ...reauth }) })
        client.setQueryData(['offsite-backup'], next)
        setNotice({ kind: 'success', message: next.connected ? 'Google Drive yedeği ayarları kaydedildi.' : 'Ayarlar kaydedildi. Şimdi “Google ile Bağlan” ile Drive hesabınızı bağlayın.' })
      } else if (confirm === 'setup') {
        if (setupKey && !keyStored) return
        const plan = offsiteSetupPlan(saved, draft, saved.clientSecretSaved && !changingSecret)
        if (plan.needsSave) {
          const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ ...offsiteBackupSaveBody({ ...draft, enabled: true, ageRecipient: setupKey?.recipient ?? draft.ageRecipient }, changingSecret), ...reauth }) })
          client.setQueryData(['offsite-backup'], next)
        }
        const { url } = await hubApi<{ url: string }>('/settings/offsite-backup/google/authorize', { method: 'POST', body: JSON.stringify(reauth) })
        markFirstRun()
        window.location.assign(url)
        return
      } else if (confirm === 'connect') {
        const { url } = await hubApi<{ url: string }>('/settings/offsite-backup/google/authorize', { method: 'POST', body: JSON.stringify(reauth) })
        window.location.assign(url)
        return
      } else {
        const next = await hubApi<OffsiteBackupView>('/settings/offsite-backup/google/disconnect', { method: 'POST', body: JSON.stringify(reauth) })
        client.setQueryData(['offsite-backup'], next)
        setNotice({ kind: 'success', message: 'Google Drive bağlantısı kesildi ve yetki iptal edildi.' })
      }
      setConfirm(null)
    } catch (reason) {
      setConfirmError(mfaErrorMessage(reason, 'İşlem tamamlanamadı.'))
    } finally { setBusy(null) }
  }
  async function testConnection() {
    setBusy('test'); setActionResult(null)
    try {
      await hubApi<{ ok: boolean }>('/settings/offsite-backup/test', { method: 'POST' })
      setActionResult({ kind: 'done', message: 'Bağlantı çalışıyor: Google yetkisi geçerli ve Drive klasörü hazır.' })
    } catch (reason) {
      setActionResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Bağlantı denenemedi.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: ['offsite-backup'] })
    }
  }
  async function sendNow() {
    setBusy('run'); setActionResult(null)
    try {
      await hubApi('/settings/offsite-backup/run', { method: 'POST' })
      setAskedAt(Date.now())
      setActionResult({ kind: 'done', message: 'Gönderim sıraya alındı; bir dakika içinde başlar. Durum burada güncellenir.' })
    } catch (reason) {
      setActionResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Gönderim başlatılamadı.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: ['offsite-backup'] })
    }
  }
  async function openKeyMaker() {
    setKeyOpen(true); setKeyPair(null); setKeyError(''); setKeyDownloaded(false); setKeyStored(false)
    try { setKeyPair(await generateBackupKeyPair()) } catch { setKeyError('Bu tarayıcı anahtar üretemedi; güncel bir Chrome, Edge, Firefox ya da Safari deneyin.') }
  }
  function closeKeyMaker() {
    // The private key lives only in this window and in the downloaded file.
    setKeyOpen(false); setKeyPair(null)
  }

  const setupDone = !!saved && offsiteSetupDone(saved)
  // After the one-button setup: the first copy was asked for on return; say so when it has reached Drive.
  const firstSuccessAt = saved?.lastSuccessAt ?? null
  useEffect(() => {
    const started = firstRunStartedAt()
    if (started === null || !firstSuccessAt || Date.parse(firstSuccessAt) < started - 5_000) return
    clearFirstRun()
    setNotice({ kind: 'success', message: 'İlk yedek Drive’a gönderildi.' })
  }, [firstSuccessAt])
  const clientDone = !!saved && !!saved.clientId && saved.clientSecretSaved
  const keyDone = !!saved && !!saved.ageRecipient
  const factsList = saved && <dl className="ss-ob-facts">
    <div><dt>Son Gönderim</dt><dd>{saved.lastSuccessAt ? formatSettingsDate(saved.lastSuccessAt) : '—'}</dd></div>
    <div><dt>Set ve Boyut</dt><dd title={saved.lastSet ?? undefined}>{saved.lastSet ? formatBackupSize(saved.lastSize) : '—'}{saved.lastSet && <span className="ss-ob-set"> · {saved.lastSet}</span>}</dd></div>
    <div><dt>Sonraki Gönderim</dt><dd>{saved.runningSince ? 'Şimdi gönderiliyor' : saved.nextRunAt ? formatSettingsDate(saved.nextRunAt) : '—'}</dd></div>
    <div><dt>Drive’daki Kopya</dt><dd>{saved.remoteCopies ?? '—'}</dd></div>
  </dl>
  const clientFields = saved && draft && <>
    <details className="ss-ob-how"><summary>Nasıl Alınır?</summary>
      <ol className="ss-ob-steps">{googleCloudSteps.map(step => <li key={step}>{step}</li>)}</ol>
      <div className="ss-fld"><small>Yönlendirme Adresi</small><div className="ss-alert-row"><input className="ss-in" readOnly value={saved.redirectUri} aria-label="Yönlendirme Adresi" onFocus={event => event.currentTarget.select()} /><button type="button" className="ss-btn" onClick={() => void copyText(saved.redirectUri).then(() => setActionResult({ kind: 'done', message: 'Yönlendirme adresi kopyalandı.' }))}><SettingsIcon name="copy" />Kopyala</button></div><span className="ss-help">İzinler: {saved.scopes}</span></div>
    </details>
    <div className="ss-pair">
      <label className="ss-fld"><small>İstemci Kimliği</small><input className="ss-in" autoComplete="off" spellCheck={false} value={draft.clientId} maxLength={120} placeholder="…apps.googleusercontent.com" onChange={event => update({ clientId: event.target.value })} /></label>
      <div className="ss-fld"><small>İstemci Gizli Anahtarı</small>{saved.clientSecretSaved && !changingSecret
        ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingSecret(true)}>Değiştir</button></div>
        : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="İstemci Gizli Anahtarı" value={draft.clientSecret} maxLength={100} placeholder="GOCSPX-…" onChange={event => update({ clientSecret: event.target.value })} />{saved.clientSecretSaved && <button type="button" className="ss-btn is-xs" onClick={() => { update({ clientSecret: '' }); setChangingSecret(false) }}>Vazgeç</button>}</div>}
      </div>
    </div>
  </>
  const keyFields = saved && draft && <div className="ss-fld">
    <div className="ss-ob-inline">
      {(pasteKey || !!draft.ageRecipient) && <input className="ss-in" autoComplete="off" spellCheck={false} aria-label="age Açık Anahtarı" value={draft.ageRecipient} maxLength={80} placeholder="age1…" onChange={event => update({ ageRecipient: event.target.value })} />}
      <button type="button" className={`ss-btn${!keyDone && clientDone ? ' is-primary' : ''}`} onClick={() => void openKeyMaker()}><SettingsIcon name="lock" />Şifreleme Anahtarı Oluştur</button>
      {!pasteKey && !draft.ageRecipient && <button type="button" className="ss-link" onClick={() => setPasteKey(true)}>Elimde Anahtar Var</button>}
    </div>
    <span className="ss-help">{saved.ageRecipient && draft.ageRecipient.trim() !== saved.ageRecipient ? 'Anahtar değişirse yeni kopyalar yeni anahtarla açılır; eski anahtar dosyanızı saklayın.' : 'Özel anahtar yalnız size dosya olarak iner; kaybolursa yedekler açılamaz.'}</span>
  </div>
  const folderFields = saved && draft && <>
    <div className="ss-fld"><small>Drive Klasörü<FieldHelp label="Drive Klasörü">{draft.folderMode === 'NAME' ? 'Uygulama Drive’ınızın ana dizininde bu adla kendi klasörünü oluşturur ve kimliğini saklar.' : 'Yalnız uygulamanın daha önce oluşturduğu bir klasörün ID’si çalışır: Drive izni (drive.file) başka bir uygulamanın ya da elle oluşturulan klasörü göremez.'}{saved.folderId ? ` Kullanılan klasör ID’si: ${saved.folderId}` : ''}</FieldHelp></small>
      <div className="ss-ob-folder">
        <div className="ss-seg3 is-two" role="group" aria-label="Klasör Seçimi">
          <button type="button" aria-pressed={draft.folderMode === 'NAME'} className={draft.folderMode === 'NAME' ? 'is-on' : undefined} onClick={() => update({ folderMode: 'NAME', folderValue: saved.folderMode === 'NAME' ? saved.folderValue : offsiteBackupDefaultFolder })}>Uygulama Klasörü</button>
          <button type="button" aria-pressed={draft.folderMode === 'ID'} className={draft.folderMode === 'ID' ? 'is-on' : undefined} onClick={() => update({ folderMode: 'ID', folderValue: saved.folderMode === 'ID' ? saved.folderValue : saved.folderId ?? '' })}>Klasör ID’si</button>
        </div>
        <input className="ss-in" autoComplete="off" spellCheck={false} aria-label={draft.folderMode === 'NAME' ? 'Klasör Adı' : 'Klasör ID’si'} value={draft.folderValue} maxLength={draft.folderMode === 'NAME' ? 80 : 200} placeholder={draft.folderMode === 'NAME' ? offsiteBackupDefaultFolder : '1AbC…'} onChange={event => update({ folderValue: event.target.value })} />
      </div>
    </div>
    <div className="ss-pair">
      <label className="ss-fld"><small>Günlük Saklama (Gün)<FieldHelp label="Günlük Saklama">Bu kadar günün bütün kopyaları Drive’da kalır; daha eskileri silinir (her ayın ilk kopyası hariç).</FieldHelp></small><input className="ss-in" type="number" inputMode="numeric" min={3} max={365} value={draft.keepDays} onChange={event => update({ keepDays: event.target.value })} /></label>
      <label className="ss-fld"><small>Aylık Saklama (Ay)<FieldHelp label="Aylık Saklama">Son bu kadar ayın her birinin ilk kopyası ayrıca saklanır. 0: aylık kopya tutulmaz. Uygulama yalnız kendi yüklediği yedek dosyalarını siler.</FieldHelp></small><input className="ss-in" type="number" inputMode="numeric" min={0} max={120} value={draft.keepMonths} onChange={event => update({ keepMonths: event.target.value })} /></label>
    </div>
  </>
  const summaryRow = (id: 'client' | 'key' | 'folder', label: string, value: ReactNode, body: ReactNode) => <div className="ss-ob-row">
    <div className="ss-ob-row-line"><b>{label}</b><span aria-hidden="true">·</span><span className="ss-ob-row-value">{value}</span><button type="button" className="ss-btn is-xs" aria-label={`${label} ${openRows.includes(id) ? 'Gizle' : 'Değiştir'}`} aria-expanded={openRows.includes(id)} onClick={() => toggleRow(id)}>{openRows.includes(id) ? 'Gizle' : 'Değiştir'}</button></div>
    {openRows.includes(id) && <div className="ss-ob-row-body">{body}</div>}
  </div>
 const ready = !!saved && saved.connected && !saved.authorizationInvalid && !!saved.ageRecipient
  const bar = <SettingsBar title="Google Drive Yedeği" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || !!draftError || busy !== null} title={draftError ?? undefined} onClick={() => openConfirm('save')}><SettingsIcon name="save" />Ayarları Kaydet</button>
  </SettingsBar>
  const copy = confirm ? offsiteConfirmCopy[confirm] : null

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="offsite" description="Sunucudaki doğrulanmış yedek, şifreleme anahtarınızla şifrelenip günde iki kez (12:30 ve 00:30) Google Drive’ınıza gönderilir. Özel anahtar sunucuya hiç girmez; yedeği yalnız siz açabilirsiniz." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Yedek ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Google Drive yedeğini yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved || !draft || !state ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Yedek ayarları alınamadı.</span></div> : <>
        {setupDone ? <>
          <section className="ss-ob-status" aria-label="Yedek Durumu">
            <div className="ss-ob-state"><span className="ss-tile"><SettingsIcon name="cloud" /></span><span className={`ss-tag is-lg${state.tone === 'done' ? ' is-ink' : ''}`}><SettingsGlyph state={state.tone} />{state.label}</span><span className="ss-ob-account">{saved.account ?? 'Google hesabı'}{saved.connectedAt ? ` · bağlandı ${formatSettingsDate(saved.connectedAt)}` : ''}</span></div>
            {saved.authorizationInvalid && <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span><b>Google yetkisi geçersiz; yedek gönderilmiyor.</b> {saved.authorizationError ?? ''} “Google ile Yeniden Bağlan”a basın.</span></div>}
            {factsList}
            {saved.lastError && saved.consecutiveFailures > 0 && <div className="ss-callout is-danger"><SettingsIcon name="alert" /><span><b>Son hata{saved.lastErrorAt ? ` (${formatSettingsDate(saved.lastErrorAt)})` : ''}:</b> {saved.lastError}{saved.consecutiveFailures > 1 ? ` · ${saved.consecutiveFailures} deneme` : ''}</span></div>}
            <div className="ss-alert-row">
              <button type="button" className={`ss-btn${saved.authorizationInvalid ? '' : ' is-primary'}`} disabled={busy !== null || dirty || !ready || !saved.enabled} title={!saved.enabled ? 'Önce otomatik gönderimi açıp kaydedin.' : undefined} onClick={() => void sendNow()}><SettingsIcon name="cloud" />{busy === 'run' ? 'İsteniyor…' : 'Şimdi Gönder'}</button>
              <button type="button" className="ss-btn" disabled={busy !== null} onClick={() => void testConnection()}><SettingsIcon name="info" />{busy === 'test' ? 'Deneniyor…' : 'Bağlantıyı Test Et'}</button>
              <button type="button" className="ss-btn" onClick={() => setRestoreOpen(true)}><SettingsIcon name="lock" />Yedeği Aç</button>
              <button type="button" className={`ss-btn${saved.authorizationInvalid ? ' is-primary' : ''}`} disabled={busy !== null || dirty} onClick={() => openConfirm('connect')}><SettingsIcon name="reset" />{saved.authorizationInvalid ? 'Google ile Yeniden Bağlan' : 'Yeniden Bağlan'}</button>
              <button type="button" className="ss-btn is-danger" disabled={busy !== null} onClick={() => openConfirm('disconnect')}><SettingsIcon name="logout" />Bağlantıyı Kes</button>
            </div>
            {actionResult ? <span role="status" className="ss-help ss-ob-result"><SettingsGlyph state={actionResult.kind} />{actionResult.message}</span>
              : dirty ? <span className="ss-help">Göndermek için önce değişiklikleri kaydedin.</span> : null}
          </section>
          <section className="ss-ob-rows" aria-label="Yedek Ayarları">
            <SettingsSwitch label="Otomatik Gönderim" checked={draft.enabled} onChange={enabled => update({ enabled })} />
            {summaryRow('key', 'Şifreleme Anahtarı', <><span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span><code>{shortAgeKey(saved.ageRecipient)}</code></>, keyFields)}
            {summaryRow('client', 'Google İstemcisi', <span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span>, clientFields)}
            {summaryRow('folder', 'Klasör ve Saklama', `${draft.folderMode === 'NAME' ? (draft.folderValue.trim() || offsiteBackupDefaultFolder) : 'Klasör ID’si'} · ${draft.keepDays} gün / ${draft.keepMonths} ay`, folderFields)}
            {draftError && dirty && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{draftError}</span>}
          </section>
        </> : <>
          <div className="ss-ob-wizard">
            <section className="ss-ob-step" aria-label="Google Drive’a Bağlan">
              <div className="ss-ob-step-head"><span className="ss-tile is-sm"><SettingsIcon name="cloud" /></span><h3>Google Drive’a Bağlan</h3></div>
              <div className="ss-ob-step-body">
                {clientDone ? summaryRow('client', 'Google İstemcisi', <span className="ss-tag is-ink"><SettingsGlyph state="done" />Kayıtlı</span>, clientFields) : clientFields}
                <div className="ss-alert-row"><button type="button" className="ss-btn is-primary" disabled={busy !== null} onClick={() => void startSetup()}><SettingsIcon name="cloud" />Google Drive’a Bağlan</button></div>
                <span className="ss-help">{keyDone || draft.ageRecipient.trim() ? 'Parolanızı onaylayınca ayarlar kaydedilir ve Google’ın izin sayfası açılır.' : 'Şifreleme anahtarı bu tarayıcıda üretilir ve özel anahtar dosyası inerken parolanız bir kez sorulur; ardından Google izin sayfası açılır.'}</span>
                {setupError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{setupError}</span>}
              </div>
            </section>
            {draftError && dirty && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{draftError}</span>}
            <details className="ss-ob-adv"><summary>Gelişmiş Ayarlar</summary>
              <div className="ss-ob-adv-body">
                <div className="ss-fld"><small>Kendi Açık Anahtarınız (İsteğe Bağlı)</small>
                  <input className="ss-in" autoComplete="off" spellCheck={false} aria-label="age Açık Anahtarı" value={draft.ageRecipient} maxLength={80} placeholder="age1…" onChange={event => update({ ageRecipient: event.target.value })} />
                  <span className="ss-help">Boş bırakırsanız anahtar çifti sizin için üretilir. Elinizde bir age açık anahtarı varsa buraya yapıştırın.</span>
                </div>
                {folderFields}
              </div>
            </details>
            <div><button type="button" className="ss-btn" onClick={() => setRestoreOpen(true)}><SettingsIcon name="lock" />Yedeği Aç</button></div>
          </div>
        </>}
      </>}
    </div>
    {saved && !forbidden && dirty && setupDone ? bar : null}
    <Modal open={confirm !== null} title={copy?.title ?? ''} hint={copy?.hint} className="ss-modal is-sm" onClose={() => { if (busy === null) { setConfirm(null); setSetupKey(null) } }} footer={<><button type="button" className="ss-btn" disabled={busy !== null} onClick={() => { setConfirm(null); setSetupKey(null) }}>Vazgeç</button><button type="submit" form="ss-offsite-confirm" className={`ss-btn ${copy?.danger ? 'is-danger is-solid' : 'is-primary'}`} disabled={busy !== null || !password || (confirm === 'setup' && !!setupKey && !keyStored)}>{busy === confirm ? copy?.busy : copy?.action}</button></>}>
      <form id="ss-offsite-confirm" className="ss-stack" onSubmit={submitConfirm}>
        {confirm === 'setup' && setupKey && <>
          <div className="ss-callout is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Özel anahtar dosyası indirildi ({backupKeyFileName}).</b> Güvenli bir yerde saklayın; kaybolursa Drive’daki yedekler açılamaz, panel ve sunucu bir kopyasını tutmaz.</span></div>
          <div className="ss-alert-row"><button type="button" className="ss-btn" onClick={() => downloadText(setupKey.file, backupKeyFileName)}><SettingsIcon name="save" />Dosyayı Yeniden İndir</button></div>
          <label className="ss-ob-check"><input type="checkbox" checked={keyStored} onChange={event => setKeyStored(event.target.checked)} /><span>Dosyayı indirdim ve sakladım.</span></label>
        </>}
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {confirmError && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{confirmError}</span>}
      </form>
    </Modal>
    <Modal open={keyOpen} title="Şifreleme Anahtarı Oluştur" hint="Anahtar çifti bu tarayıcıda üretildi. Sunucuya yalnız açık anahtar gider." className="ss-modal is-lg" onClose={closeKeyMaker} footer={<><button type="button" className="ss-btn" onClick={closeKeyMaker}>Vazgeç</button><button type="button" className="ss-btn is-primary" disabled={!keyPair || !keyDownloaded || !keyStored} onClick={() => { if (keyPair) update(setupDone ? { ageRecipient: keyPair.recipient } : { ageRecipient: keyPair.recipient, enabled: true }); closeKeyMaker(); setNotice({ kind: 'success', message: 'Yeni açık anahtar forma yazıldı. Kullanmak için Ayarları Kaydet’e basın.' }) }}>Açık Anahtarı Kullan</button></>}>
      <div className="ss-stack">
        {keyError ? <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{keyError}</span> : !keyPair ? <LoadingState>Anahtar üretiliyor…</LoadingState> : <>
          <div className="ss-callout is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Özel anahtar dosyasını indirip güvenli bir yerde saklayın.</b> Kaybolursa Drive’daki yedekler hiçbir şekilde açılamaz; panel ve sunucu bu dosyanın kopyasını tutmaz. Parola yöneticinize ve ayrıca çevrimdışı bir yere (USB) koyun, kimseyle paylaşmayın.</span></div>
          <label className="ss-fld"><small>Açık Anahtar</small><input className="ss-in" readOnly value={keyPair.recipient} onFocus={event => event.currentTarget.select()} /></label>
          <div className="ss-alert-row"><button type="button" className={`ss-btn${keyDownloaded ? '' : ' is-primary'}`} onClick={() => { downloadText(keyPair.file, backupKeyFileName); setKeyDownloaded(true) }}><SettingsIcon name="save" />{keyDownloaded ? 'Yeniden İndir' : 'Özel Anahtarı İndir'}</button><span className="ss-help">{keyDownloaded ? <><SettingsGlyph state="done" />{backupKeyFileName} indirildi.</> : backupKeyFileName}</span></div>
          <label className="ss-ob-check"><input type="checkbox" checked={keyStored} disabled={!keyDownloaded} onChange={event => setKeyStored(event.target.checked)} /><span>Özel anahtar dosyasını indirdim ve güvenli bir yere kaydettim.</span></label>
        </>}
      </div>
    </Modal>
    {restoreOpen && <OffsiteRestoreModal onClose={() => setRestoreOpen(false)} />}
  </>
}

// Yedeği Aç: the copy and the key file are read and decrypted in this browser only.
function OffsiteRestoreModal({ onClose }: { onClose: () => void }) {
  const [backup, setBackup] = useState<File | null>(null)
  const [keyFile, setKeyFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  async function open() {
    if (!backup || !keyFile) return
    setBusy(true); setResult(null)
    try {
      const identity = identityFromKeyFile(await keyFile.text())
      if (!identity) { setResult({ kind: 'bad', message: 'Anahtar dosyasında AGE-SECRET-KEY-1 ile başlayan özel anahtar yok.' }); return }
      let stream: ReadableStream<Uint8Array>
      try { stream = await decryptBackup(backup, identity) } catch { setResult({ kind: 'bad', message: 'Yedek bu anahtarla açılamadı: anahtar bu yedeğe ait değil ya da dosya bozuk.' }); return }
      await saveStream(stream, decryptedFileName(backup.name))
      setResult({ kind: 'done', message: `${decryptedFileName(backup.name)} kaydedildi. İçinden database.dump, private-volumes.tar.gz ve SHA256SUMS çıkar.` })
    } catch (reason) {
      if (reason instanceof DOMException && reason.name === 'AbortError') return
      setResult({ kind: 'bad', message: 'Yedek açılamadı: dosya eksik ya da bozuk olabilir.' })
    } finally { setBusy(false) }
  }
  return <Modal open title="Yedeği Aç" hint="Seçtiğiniz dosyalar yalnız bu tarayıcıda okunur; sunucuya hiçbir şey gönderilmez." className="ss-modal is-lg" onClose={() => { if (!busy) onClose() }} footer={<><button type="button" className="ss-btn" disabled={busy} onClick={onClose}>Kapat</button><button type="button" className="ss-btn is-primary" disabled={busy || !backup || !keyFile} onClick={() => void open()}>{busy ? 'Açılıyor…' : 'Çöz ve Kaydet'}</button></>}>
    <div className="ss-stack">
      <label className="ss-fld"><small>Yedek Dosyası (.tar.age)</small><input className="ss-in" type="file" accept=".age" onChange={event => { setBackup(event.target.files?.[0] ?? null); setResult(null) }} /></label>
      <label className="ss-fld"><small>Özel Anahtar Dosyası</small><input className="ss-in" type="file" accept=".txt,text/plain" onChange={event => { setKeyFile(event.target.files?.[0] ?? null); setResult(null) }} /></label>
      {result && <span role="status" className={result.kind === 'bad' ? 'ss-ferr' : 'ss-help'}><SettingsGlyph state={result.kind} />{result.message}</span>}
    </div>
  </Modal>
}

type AlertEmailSettings = { senderAddress: string | null; recipients: string[]; appPasswordSaved: boolean; updatedAt: string | null; version: number }
const maximumAlertRecipients = 5

// Settings → Alarm Bildirimleri: the Gmail account that sends the server's and the panel's alerts (security plan Y1).
// The app password is write-only: the server never returns it, the form shows "Kayıtlı" and "Değiştir". The host
// keeps a root-only copy for alerts while the panel is down (deploy/scripts/alert-settings-sync.sh).
function AlertEmailSettingsSection() {
  const client = useQueryClient()
  const query = useQuery({ queryKey: ['alert-email-settings'], queryFn: () => hubApi<AlertEmailSettings>('/settings/alert-email'), retry: false })
  const saved = query.data
  const [sender, setSender] = useState('')
  const [recipients, setRecipients] = useState<string[]>([''])
  const [password, setPassword] = useState('')
  const [changingPassword, setChangingPassword] = useState(false)
  const [busy, setBusy] = useState<'save' | 'test' | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const [testResult, setTestResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [loadedVersion, setLoadedVersion] = useState<number | null>(null)
  // The form starts from the saved settings, and again after a save (a new version).
  if (saved && loadedVersion !== saved.version) {
    setLoadedVersion(saved.version)
    setSender(saved.senderAddress ?? '')
    setRecipients(saved.recipients.length ? saved.recipients : [''])
    setPassword('')
    setChangingPassword(!saved.appPasswordSaved)
  }

  const cleanRecipients = recipients.map(value => value.trim().toLowerCase()).filter(Boolean)
  const dirty = !!saved && (sender.trim().toLowerCase() !== (saved.senderAddress ?? '') || cleanRecipients.join(',') !== saved.recipients.join(',') || password.trim().length > 0)
  const configured = !!saved?.senderAddress && saved.recipients.length > 0 && saved.appPasswordSaved

  async function save() {
    if (!saved) return
    setBusy('save'); setNotice(null); setTestResult(null)
    try {
      const next = await hubApi<AlertEmailSettings>('/settings/alert-email', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ senderAddress: sender.trim(), recipients: cleanRecipients, appPassword: changingPassword ? password : null }) })
      client.setQueryData(['alert-email-settings'], next)
      setNotice({ kind: 'success', message: 'Alarm e-postası ayarları kaydedildi. Sunucu sonraki alarmda bu ayarları kullanır.' })
    } catch (reason) {
      setNotice({ kind: 'error', message: reason instanceof Error ? reason.message : 'Alarm e-postası ayarları kaydedilemedi.' })
    } finally { setBusy(null) }
  }
  async function sendTest() {
    setBusy('test'); setTestResult(null)
    try {
      const result = await hubApi<{ sent: boolean; recipients: string[] }>('/settings/alert-email/test', { method: 'POST' })
      setTestResult({ kind: 'done', message: `Test e-postası gönderildi: ${result.recipients.join(', ')}. Gelen kutusunu (ve spam klasörünü) kontrol edin.` })
    } catch (reason) {
      setTestResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Test e-postası gönderilemedi.' })
    } finally { setBusy(null) }
  }

  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const bar = <SettingsBar title="Alarm Bildirimleri" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || busy !== null} onClick={() => void save()}><SettingsIcon name="save" />{busy === 'save' ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
  </SettingsBar>

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="alerts" description="Sağlık izleme, yedek, deploy ve geri alma alarmları bu Gmail hesabından e-postayla gönderilir." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Alarm ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Alarm bildirimlerini yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Alarm ayarları alınamadı.</span></div> : <>
        <div className="ss-pair">
          <label className="ss-fld"><small>Gönderen Gmail Adresi</small><input className="ss-in" type="email" inputMode="email" autoComplete="off" spellCheck={false} value={sender} maxLength={254} placeholder="ravencia.alarm@gmail.com" onChange={event => setSender(event.target.value)} /></label>
          <div className="ss-fld"><small>Uygulama Şifresi<FieldHelp label="Uygulama Şifresi"><b>İki Adımlı Doğrulama</b> açıkken <a href="https://myaccount.google.com/apppasswords" target="_blank" rel="noreferrer">myaccount.google.com/apppasswords</a> sayfasında “Ravencia” adıyla şifre oluşturup 16 harfi yapıştırın. Google hesap şifrenizi girmeyin.</FieldHelp></small>{saved.appPasswordSaved && !changingPassword
            ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingPassword(true)}>Değiştir</button></div>
            : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="Uygulama Şifresi" value={password} maxLength={40} placeholder="abcd efgh ijkl mnop" onChange={event => setPassword(event.target.value)} />{saved.appPasswordSaved && <button type="button" className="ss-btn is-xs" onClick={() => { setPassword(''); setChangingPassword(false) }}>Vazgeç</button>}</div>}
          </div>
        </div>
        <div className="ss-fld" role="group" aria-label="Alıcılar"><small>Alıcılar</small>
          {recipients.map((recipient, index) => <div key={index} className="ss-alert-row">
            <input className="ss-in" type="email" inputMode="email" autoComplete="off" spellCheck={false} aria-label={`${index + 1}. Alıcı`} value={recipient} maxLength={254} placeholder="ornek@ravencia.com" onChange={event => setRecipients(current => current.map((value, position) => position === index ? event.target.value : value))} />
            {recipients.length > 1 && <button type="button" className="ss-btn is-xs is-quiet" aria-label={`${index + 1}. Alıcıyı Kaldır`} onClick={() => setRecipients(current => current.filter((_, position) => position !== index))}><SettingsIcon name="trash" /></button>}
          </div>)}
          {recipients.length < maximumAlertRecipients && <div><button type="button" className="ss-btn is-xs" onClick={() => setRecipients(current => [...current, ''])}><SettingsIcon name="plus" />Alıcı Ekle</button></div>}
        </div>
        <div className="ss-alert-row">
          <button type="button" className="ss-btn" disabled={!configured || dirty || busy !== null} onClick={() => void sendTest()}><SettingsIcon name="bell" />{busy === 'test' ? 'Gönderiliyor…' : 'Test E-postası Gönder'}</button>
          {testResult ? <span role="status" className="ss-help"><SettingsGlyph state={testResult.kind} />{testResult.message}</span>
            : <span className="ss-help">{dirty ? 'Test için önce değişiklikleri kaydedin.' : configured ? '' : 'Test için önce gönderen adresi, alıcıları ve uygulama şifresini kaydedin.'}</span>}
        </div>
      </>}
    </div>
    {saved && !forbidden ? bar : null}
  </>
}

type UserDialog = { kind: 'add' } | { kind: 'manage'; userId: string }
type OneTimePassword = { title: string; email: string | null; password: string }

const userActionCopy: Record<UserAction, { label: string; hint: string; icon: SettingsIconName; confirm: string; danger?: boolean; warning?: string }> = {
  role: { label: 'Rol Değiştir', hint: 'Bu çalışma alanındaki yetkisini değiştirir.', icon: 'user', confirm: 'Rolü Kaydet' },
  'password-reset': { label: 'Şifre Sıfırla', hint: 'Tek kullanımlık yeni parola üretir; ilk girişte değiştirilir.', icon: 'lock', confirm: 'Şifreyi Sıfırla', danger: true, warning: 'Mevcut parolası geçersiz olur ve bütün oturumları kapanır. Yeni parola yalnız bir kez gösterilir.' },
  'mfa-reset': { label: 'İki Adımlı Doğrulamayı Sıfırla', hint: 'Authenticator ve kurtarma kodlarını kaldırır.', icon: 'shield', confirm: 'Doğrulamayı Sıfırla', danger: true, warning: 'Authenticator ve kurtarma kodları silinir, bütün oturumları kapanır. Kullanıcı girişten sonra yeniden kurabilir.' },
  sessions: { label: 'Oturumları Kapat', hint: 'Bu çalışma alanındaki açık oturumlarını sonlandırır.', icon: 'logout', confirm: 'Oturumları Kapat', danger: true, warning: 'Kullanıcının açık oturumları kapanır; yeniden giriş yapması gerekir.' },
  disable: { label: 'Pasife Al', hint: 'Bu çalışma alanına girişini durdurur.', icon: 'logout', confirm: 'Pasife Al', danger: true, warning: 'Kullanıcı bu çalışma alanına giremez ve açık oturumları kapanır. Kayıtları silinmez; dilediğiniz an etkinleştirebilirsiniz.' },
  enable: { label: 'Etkinleştir', hint: 'Bu çalışma alanına yeniden girebilir.', icon: 'reset', confirm: 'Etkinleştir' }
}

function userStatusTag(user: ManagedUser) {
  if (!user.active) return <span className="ss-tag"><SettingsGlyph state="none" />Pasif</span>
  if (user.lockedOut) return <span className="ss-tag"><SettingsGlyph state="bad" />Kilitli</span>
  if (user.passwordChangePending) return <span className="ss-tag" title="İlk girişte parolasını değiştirecek"><SettingsGlyph state="pending" />Parola Bekliyor</span>
  return <span className="ss-tag is-ink"><SettingsGlyph state="done" />Etkin</span>
}

// The caller's password, and with an authenticator on, its code: every change on this page asks for them again.
function ReauthFields({ password, onPassword, factor, onFactor, mfa }: { password: string; onPassword: (value: string) => void; factor: string; onFactor: (value: string) => void; mfa: boolean }) {
  return <div className={mfa ? 'ss-pair' : 'ss-stack'}>
    <label className="ss-fld"><small>Parolanız</small><input className="ss-in" type="password" autoComplete="current-password" required value={password} onChange={event => onPassword(event.target.value)} placeholder="••••••••" /></label>
    {mfa && <label className="ss-fld"><small>Doğrulama Kodu<FieldHelp label="Doğrulama Kodu">Authenticator uygulamanızdaki 6 haneli kod; telefonunuz yanınızda değilse bir kurtarma kodu.</FieldHelp></small><input className="ss-in" inputMode="text" autoComplete="one-time-code" required value={factor} maxLength={20} onChange={event => onFactor(event.target.value)} placeholder="123456" /></label>}
  </div>
}

// Ayarlar › Kullanıcılar: owners and administrators list, add and change the workspace's users (user-management.ts).
function UserManagementSection({ mfa }: { mfa: boolean }) {
  const client = useQueryClient()
  const query = useQuery({ queryKey: ['managed-users'], queryFn: () => hubApi<ManagedUserList>('/settings/users'), retry: false })
  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const [dialog, setDialog] = useState<UserDialog | null>(null)
  const [step, setStep] = useState<UserAction | null>(null)
  const [password, setPassword] = useState('')
  const [factor, setFactor] = useState('')
  const [role, setRole] = useState<UserRole>('OPERATIONS')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [oneTime, setOneTime] = useState<OneTimePassword | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const list = query.data
  const actorRole = list?.currentRole ?? ''
  const roles = assignableRoles(actorRole)
  const managed = dialog?.kind === 'manage' ? list?.items.find(user => user.id === dialog.userId) ?? null : null

  function open(next: UserDialog) {
    setDialog(next); setStep(null); setPassword(''); setFactor(''); setError('')
    if (next.kind === 'add') { setEmail(''); setDisplayName(''); setRole('OPERATIONS') }
  }
  function close() { if (!busy) { setDialog(null); setStep(null); setPassword(''); setFactor(''); setError('') } }
  function chooseStep(action: UserAction | null) {
    setStep(action); setPassword(''); setFactor(''); setError('')
    if (action === 'role' && managed) setRole(managed.role)
  }

  async function submitAdd(event: FormEvent) {
    event.preventDefault()
    if (!isEmailAddress(email)) { setError('Geçerli bir e-posta adresi girin.'); return }
    setBusy(true); setError('')
    try {
      const result = await hubApi<{ userId: string; existingAccount: boolean; temporaryPassword: string | null }>('/settings/users', { method: 'POST', body: JSON.stringify({ email: email.trim(), displayName: displayName.trim(), role, ...reauthBody(password, factor) }) })
      await client.invalidateQueries({ queryKey: ['managed-users'] })
      setDialog(null)
      if (result.temporaryPassword) setOneTime({ title: 'Kullanıcı Eklendi', email: email.trim(), password: result.temporaryPassword })
      else setNotice({ kind: 'success', message: `${email.trim()} hesabı bu çalışma alanına ${userRoleLabel(role)} olarak eklendi; mevcut parolasıyla girer.` })
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Kullanıcı eklenemedi.')
    } finally { setBusy(false) }
  }

  async function submitAction(event: FormEvent) {
    event.preventDefault()
    if (!managed || !step) return
    setBusy(true); setError('')
    const reauth = reauthBody(password, factor)
    const base = `/settings/users/${managed.id}`
    try {
      if (step === 'role') await hubApi(`${base}/role`, { method: 'PUT', body: JSON.stringify({ role, ...reauth }) })
      else if (step === 'disable' || step === 'enable') await hubApi(`${base}/status`, { method: 'PUT', body: JSON.stringify({ active: step === 'enable', ...reauth }) })
      else if (step === 'mfa-reset') await hubApi(`${base}/mfa-reset`, { method: 'POST', body: JSON.stringify(reauth) })
      else if (step === 'sessions') await hubApi(`${base}/sessions/revoke`, { method: 'POST', body: JSON.stringify(reauth) })
      else {
        const result = await hubApi<{ temporaryPassword: string }>(`${base}/password-reset`, { method: 'POST', body: JSON.stringify(reauth) })
        setOneTime({ title: 'Şifre Sıfırlandı', email: managed.email, password: result.temporaryPassword })
      }
      await client.invalidateQueries({ queryKey: ['managed-users'] })
      const done: Record<UserAction, string> = { role: `${managed.displayName} artık ${userRoleLabel(role)}.`, 'password-reset': '', 'mfa-reset': `${managed.displayName} için iki adımlı doğrulama sıfırlandı.`, sessions: `${managed.displayName} için açık oturumlar kapatıldı.`, disable: `${managed.displayName} pasife alındı.`, enable: `${managed.displayName} etkinleştirildi.` }
      if (done[step]) setNotice({ kind: 'success', message: done[step] })
      setDialog(null); setStep(null); setPassword(''); setFactor('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.')
    } finally { setBusy(false) }
  }

  async function copyOneTime() {
    if (!oneTime) return
    setNotice(await copyText(oneTime.password) ? { kind: 'success', message: 'Tek kullanımlık parola panoya kopyalandı.' } : { kind: 'error', message: 'Parola kopyalanamadı; elle not alın.' })
  }

  const actionCopy = step ? userActionCopy[step] : null
  const lockReason = managed ? userLockReason(managed, actorRole) : null
  const actions = managed ? availableUserActions(managed, actorRole) : []
  const activeCount = list?.items.filter(user => user.active).length ?? 0

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="users" description="Bu çalışma alanının kullanıcıları. Yalnız hesap sahibi ve yöneticiler görür; her değişiklik parolanızla onaylanır ve kayda geçer." actions={list && !forbidden ? <button type="button" className="ss-btn is-primary" onClick={() => open({ kind: 'add' })}><SettingsIcon name="plus" />Kullanıcı Ekle</button> : undefined} />
    {query.isPending ? <div className="ss-body"><LoadingState>Kullanıcılar yükleniyor…</LoadingState></div>
      : forbidden ? <div className="ss-body"><div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Kullanıcıları yalnız hesap sahibi ve yöneticiler görür ve yönetir.</span></div></div>
        : query.isError || !list ? <div className="ss-body"><div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Kullanıcılar alınamadı.</span></div></div>
          : <div className="ss-body is-flush"><div className="ss-sess-sub">Etkin <span className="ss-count">{activeCount}</span>{list.items.length > activeCount ? <> · Pasif <span className="ss-count">{list.items.length - activeCount}</span></> : null}</div>
            <table className="ss-users">
              <thead><tr><th scope="col">Ad Soyad ve E-posta</th><th scope="col">Rol</th><th scope="col">2FA</th><th scope="col">Son Giriş</th><th scope="col">Durum</th><th scope="col" aria-label="İşlemler" /></tr></thead>
              <tbody>{list.items.map(user => <tr key={user.id} className={user.active ? undefined : 'is-off'}>
                <td className="ss-users-name"><span><b title={user.displayName}>{user.displayName}</b>{user.self && <span className="ss-tag is-ink">Siz</span>}</span><small title={user.email ?? undefined}>{user.email ?? '—'}</small></td>
                <td data-label="Rol">{userRoleLabel(user.role)}</td>
                <td data-label="2FA"><span className={`ss-tag${user.mfaEnabled ? ' is-ink' : ''}`}><SettingsGlyph state={user.mfaEnabled ? 'done' : 'none'} />{user.mfaEnabled ? 'Açık' : 'Kapalı'}</span></td>
                <td data-label="Son Giriş" className="is-num">{user.lastLoginAt ? formatSettingsDate(user.lastLoginAt) : 'Hiç Girmedi'}</td>
                <td data-label="Durum">{userStatusTag(user)}</td>
                <td className="ss-users-act"><button type="button" className="ss-btn is-xs" onClick={() => open({ kind: 'manage', userId: user.id })} aria-label={`${user.displayName} kullanıcısını yönet`}>Yönet</button></td>
              </tr>)}</tbody>
            </table>
          </div>}

    <Modal open={dialog?.kind === 'add'} title="Kullanıcı Ekle" hint="Panel e-posta daveti göndermez: yeni hesaba tek kullanımlık bir parola üretilir, yalnız size bir kez gösterilir. Kullanıcı ilk girişte kendi parolasını belirler." className="ss-modal is-md" onClose={close} footer={<><button type="button" className="ss-btn" disabled={busy} onClick={close}>Vazgeç</button><button type="submit" form="ss-user-add" className="ss-btn is-primary" disabled={busy}><SettingsIcon name="plus" />{busy ? 'Ekleniyor…' : 'Kullanıcı Ekle'}</button></>}>
      <form id="ss-user-add" className="ss-stack" onSubmit={event => void submitAdd(event)}>
        <div className="ss-pair">
          <label className="ss-fld"><small>Ad Soyad</small><input className="ss-in" required maxLength={160} autoComplete="off" value={displayName} onChange={event => setDisplayName(event.target.value)} data-autofocus /></label>
          <label className="ss-fld"><small>E-posta</small><input className="ss-in" type="email" inputMode="email" required maxLength={256} autoComplete="off" spellCheck={false} value={email} onChange={event => setEmail(event.target.value)} placeholder="ad@ravencia.com" /></label>
        </div>
        <div className="ss-fld"><small>Rol</small><PanelSelect ariaLabel="Rol" value={role} onChange={setRole} options={roles.map(value => ({ value, label: userRoleLabels[value] }))} /><span className="ss-help">{userRoleHints[role]}</span></div>
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {error && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{error}</span>}
      </form>
    </Modal>

    <Modal open={dialog?.kind === 'manage' && !!managed} title={managed ? managed.displayName : 'Kullanıcı'} description={managed ? `${managed.email ?? ''} · ${userRoleLabel(managed.role)}` : undefined} className="ss-modal is-md" onClose={close}
      footer={step ? <><button type="button" className="ss-btn" disabled={busy} onClick={() => chooseStep(null)}><SettingsIcon name="back" />Geri</button><button type="submit" form="ss-user-action" className={`ss-btn${actionCopy?.danger ? ' is-danger is-solid' : ' is-primary'}`} disabled={busy || (step === 'role' && role === managed?.role)}>{busy ? 'İşlem Yapılıyor…' : actionCopy?.confirm}</button></> : <button type="button" className="ss-btn" onClick={close}>Kapat</button>}>
      {managed && !step && <>
        <div className="ss-sum">
          <div><SettingsGlyph state={managed.mfaEnabled ? 'done' : 'none'} /><span>İki Adımlı Doğrulama</span><span className="ss-sum-r">{managed.mfaEnabled ? 'Açık' : 'Kapalı'}</span></div>
          <div><SettingsGlyph state={managed.openSessions ? 'done' : 'none'} /><span>Açık Oturum</span><span className="ss-sum-r">{managed.openSessions}</span></div>
          <div><SettingsGlyph state={managed.lastLoginAt ? 'done' : 'none'} /><span>Son Giriş</span><span className="ss-sum-r">{managed.lastLoginAt ? formatSettingsDate(managed.lastLoginAt) : 'Hiç Girmedi'}</span></div>
        </div>
        {lockReason ? <div className="ss-callout" role="note"><SettingsIcon name="lock" /><span>{lockReason}</span></div>
          : <div className="ss-users-actions" role="group" aria-label="Kullanıcı işlemleri">{actions.map(action => <button type="button" key={action} className={`ss-users-action${userActionCopy[action].danger ? ' is-danger' : ''}`} onClick={() => chooseStep(action)}><span className="ss-tile is-sm"><SettingsIcon name={userActionCopy[action].icon} /></span><span><b>{userActionCopy[action].label}</b><small>{userActionCopy[action].hint}</small></span><SettingsIcon name="chev" /></button>)}</div>}
      </>}
      {managed && step && actionCopy && <form id="ss-user-action" className="ss-stack" onSubmit={event => void submitAction(event)}>
        <h3 className="ss-users-step">{actionCopy.label}</h3>
        {step === 'role' && <div className="ss-fld"><small>Yeni Rol</small><PanelSelect ariaLabel="Yeni Rol" value={role} onChange={setRole} options={roles.map(value => ({ value, label: userRoleLabels[value] }))} /><span className="ss-help">{userRoleHints[role]}</span></div>}
        {actionCopy.warning && <div className="ss-callout is-warn"><SettingsIcon name="alert" /><span>{actionCopy.warning}</span></div>}
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {error && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{error}</span>}
      </form>}
    </Modal>

    <Modal open={!!oneTime} title={oneTime?.title ?? ''} description={oneTime?.email ?? undefined} className="ss-modal is-sm" onClose={() => setOneTime(null)} footer={<><button type="button" className="ss-btn is-quiet" onClick={() => void copyOneTime()}><SettingsIcon name="copy" />Kopyala</button><span className="ss-modal-note" /><button type="button" className="ss-btn is-primary" onClick={() => setOneTime(null)}>Parolayı Not Aldım</button></>}>
      {oneTime && <><div className="ss-codes ss-users-otp"><code>{oneTime.password}</code></div><div className="ss-callout is-warn"><SettingsIcon name="alert" /><span><b>Bu parola yalnız şimdi gösterilir.</b> Kullanıcıya güvenli bir yoldan iletin; ilk girişte kendi parolasını belirlemesi istenir.</span></div></>}
    </Modal>
  </>
}

type AiSkillView = { key: string; title: string; description: string; bundleHash: string; uploaded: { skillId: string; version: string; uploadedAt: string; bundleHash: string; source?: AiSkillSource; fileName?: string | null } | null; upToDate: boolean }
// FILE: a .skill file the owner uploaded (it is the one generation uses); BUNDLED: the copy that comes with the panel.
type AiSkillSource = 'FILE' | 'BUNDLED'
const maxSkillFileBytes = 10 * 1024 * 1024
type AiSettings = { apiKeySaved: boolean; defaultModel: string; updatedAt: string | null; version: number; skills: AiSkillView[] }
type AiUsageEntry = { id: string; createdAt: string; feature: string; featureLabel: string; userName: string | null; subjectLabel: string | null; model: string; inputTokens: number | null; outputTokens: number | null; costUsd: number | null; status: 'SUCCEEDED' | 'FAILED' | 'CANCELLED'; error: string | null; durationMs: number }
const defaultAiModel = 'claude-sonnet-5-5'
// The Claude models the panel offers, with their list price per million tokens (input / output, USD).
const aiModelOptions = [
  { value: 'claude-sonnet-5-5', label: 'Claude Sonnet 5.5 — dengeli (2 $ / 10 $)' },
  { value: 'claude-opus-5-5', label: 'Claude Opus 5.5 — güçlü (4 $ / 20 $)' },
  { value: 'claude-haiku-5-5', label: 'Claude Haiku 5.5 — en ucuz (0,10 $ / 0,50 $)' }
]

function aiCost(value: number | null) {
  return value == null ? '—' : `$${value.toFixed(4)}`
}

function aiTokens(entry: AiUsageEntry) {
  return entry.inputTokens == null && entry.outputTokens == null ? '—' : `${(entry.inputTokens ?? 0).toLocaleString('tr-TR')} / ${(entry.outputTokens ?? 0).toLocaleString('tr-TR')}`
}

function fileSize(bytes: number) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} MB` : `${Math.max(1, Math.round(bytes / 1024)).toLocaleString('tr-TR')} KB`
}

// Which copy of a skill Claude runs, as the Beceriler row says it.
function aiSkillState(skill: AiSkillView) {
  const uploaded = skill.uploaded
  if (!uploaded) return 'Yüklenmedi'
  if (uploaded.source === 'FILE') return `Yüklendi · Sürüm ${uploaded.version} · ${uploaded.fileName ?? 'beceri dosyası'} · ${formatSettingsDate(uploaded.uploadedAt)}`
  return skill.upToDate ? `Yüklendi · Sürüm ${uploaded.version} · Panelle gelen sürüm · ${formatSettingsDate(uploaded.uploadedAt)}` : `Güncel Değil · Sürüm ${uploaded.version} · Panelle gelen eski sürüm`
}

// Settings → Yapay Zekâ: the Claude API key every AI feature of the panel uses (the size chart is the first), the default
// model, the Agent Skills the server uploads, and the log of every run. Owner and administrators only. The key is
// write-only like the alert app password: the server never returns it, the form shows "Kayıtlı" and "Değiştir".
function AiSettingsSection() {
  const client = useQueryClient()
  const query = useQuery({ queryKey: ['ai-settings'], queryFn: () => hubApi<AiSettings>('/settings/ai'), retry: false })
  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const usage = useQuery({ queryKey: ['ai-usage'], queryFn: () => hubApi<{ items: AiUsageEntry[] }>('/settings/ai/usage?limit=50'), retry: false, enabled: query.isSuccess })
  const saved = query.data
  const [apiKey, setApiKey] = useState('')
  const [changingKey, setChangingKey] = useState(false)
  const [model, setModel] = useState(defaultAiModel)
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const [testResult, setTestResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [skillResult, setSkillResult] = useState<{ key: string; kind: 'done' | 'bad'; message: string } | null>(null)
  const [skillFile, setSkillFile] = useState<{ key: string; label: string } | null>(null)
  const skillInputs = useRef(new Map<string, HTMLInputElement>())
  const [loadedVersion, setLoadedVersion] = useState<number | null>(null)
  // The form starts from the saved settings, and again after a save (a new version); the key field always starts empty.
  if (saved && loadedVersion !== saved.version) {
    setLoadedVersion(saved.version)
    setApiKey('')
    setChangingKey(!saved.apiKeySaved)
    setModel(saved.defaultModel || defaultAiModel)
  }

  const dirty = !!saved && (model.trim() !== (saved.defaultModel || defaultAiModel) || apiKey.trim().length > 0)
  const configured = !!saved?.apiKeySaved

  async function save() {
    if (!saved) return
    setBusy('save'); setNotice(null); setTestResult(null)
    try {
      const next = await hubApi<AiSettings>('/settings/ai', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ apiKey: changingKey && apiKey.trim() ? apiKey.trim() : null, defaultModel: model.trim() }) })
      setApiKey('')
      client.setQueryData(['ai-settings'], next)
      // O9: a new key belongs to another workspace; the skills uploaded with the old one are forgotten.
      const skillsForgotten = saved.skills.some(skill => skill.uploaded) && !next.skills.some(skill => skill.uploaded)
      setNotice({ kind: 'success', message: skillsForgotten ? 'Yapay zekâ ayarları kaydedildi. Yeni anahtar için becerileri “Beceriyi Yükle” ile yeniden yükleyin; beden tablosu ancak ondan sonra oluşturulabilir.' : 'Yapay zekâ ayarları kaydedildi.' })
    } catch (reason) {
      setNotice({ kind: 'error', message: reason instanceof Error ? reason.message : 'Yapay zekâ ayarları kaydedilemedi.' })
    } finally { setBusy(null) }
  }
  async function testConnection() {
    setBusy('test'); setTestResult(null)
    try {
      const result = await hubApi<{ ok: boolean; model: string; message: string }>('/settings/ai/test', { method: 'POST' })
      setTestResult({ kind: 'done', message: result.message || `Bağlantı çalışıyor (${result.model}).` })
    } catch (reason) {
      setTestResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Bağlantı sınanamadı.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: ['ai-usage'] })
    }
  }
  // file: the owner's .skill / .zip file; none: the copy bundled with the panel (only offered while nothing is uploaded).
  async function uploadSkill(skill: AiSkillView, file?: File) {
    setSkillResult(null)
    if (file && !/\.(skill|zip)$/i.test(file.name)) { setSkillResult({ key: skill.key, kind: 'bad', message: `${file.name}: yalnız .skill ya da .zip dosyası yüklenebilir.` }); return }
    if (file && (file.size === 0 || file.size > maxSkillFileBytes)) { setSkillResult({ key: skill.key, kind: 'bad', message: `${file.name} (${fileSize(file.size)}): dosya boş olamaz ve en fazla 10 MB olabilir.` }); return }
    setBusy(`skill:${skill.key}`)
    setSkillFile(file ? { key: skill.key, label: `${file.name} · ${fileSize(file.size)}` } : null)
    try {
      let body: FormData | undefined
      if (file) { body = new FormData(); body.append('file', file, file.name) }
      const next = await hubApi<AiSettings>(`/settings/ai/skills/${encodeURIComponent(skill.key)}/upload`, { method: 'POST', body })
      client.setQueryData(['ai-settings'], next)
      const updated = next.skills.find(item => item.key === skill.key)
      setSkillResult({ key: skill.key, kind: 'done', message: updated ? `${aiSkillState(updated)}.` : 'Beceri yüklendi.' })
    } catch (reason) {
      setSkillResult({ key: skill.key, kind: 'bad', message: reason instanceof Error ? reason.message : 'Beceri yüklenemedi.' })
    } finally {
      setBusy(null)
      setSkillFile(null)
      void client.invalidateQueries({ queryKey: ['ai-usage'] })
    }
  }

  const bar = <SettingsBar title="Yapay Zekâ" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || busy !== null || !model.trim()} onClick={() => void save()}><SettingsIcon name="save" />{busy === 'save' ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
  </SettingsBar>

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="ai" description="Claude API anahtarı ve beceriler; panelin yapay zekâ özellikleri (ilk olarak beden tablosu) bunları kullanır." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Yapay zekâ ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Yapay zekâ ayarlarını yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Yapay zekâ ayarları alınamadı.</span></div> : <>
        <div className="ss-pair">
          <div className="ss-fld"><small>Claude API Anahtarı<FieldHelp label="Claude API Anahtarı">Anahtarı Claude Console’da (platform.claude.com) oluşturup yapıştırın. Şifreli saklanır; ekranda ve kayıtlarda görünmez.</FieldHelp></small>{saved.apiKeySaved && !changingKey
            ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingKey(true)}>Değiştir</button></div>
            : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="Claude API Anahtarı" value={apiKey} maxLength={300} placeholder="sk-ant-…" onChange={event => setApiKey(event.target.value)} />{saved.apiKeySaved && <button type="button" className="ss-btn is-xs" onClick={() => { setApiKey(''); setChangingKey(false) }}>Vazgeç</button>}</div>}
          </div>
          <label className="ss-fld"><small>Varsayılan Model</small><PanelSelect ariaLabel="Varsayılan Model" value={model} onChange={setModel} options={[...aiModelOptions.map(option => ({ value: option.value, label: option.label })), ...(aiModelOptions.some(option => option.value === model) ? [] : [{ value: model, label: model }])]} /></label>
        </div>
        <div className="ss-alert-row">
          <button type="button" className="ss-btn" disabled={!configured || dirty || busy !== null} onClick={() => void testConnection()}><SettingsIcon name="sparkle" />{busy === 'test' ? 'Sınanıyor…' : 'Bağlantıyı Sına'}</button>
          {testResult ? <span role="status" className="ss-help"><SettingsGlyph state={testResult.kind} />{testResult.message}</span>
            : <span className="ss-help">{dirty ? 'Sınamak için önce değişiklikleri kaydedin.' : configured ? '' : 'Sınamak için önce API anahtarını kaydedin.'}</span>}
        </div>
        <div className="ss-fld" role="group" aria-label="Beceriler"><small>Beceriler</small>
          <div className="ss-ai-skills">{saved.skills.map(skill => {
            const blocked = !configured || dirty || busy !== null
            return <div key={skill.key} className="ss-ai-skill">
              <span className="ss-ai-skill-copy"><b>{skill.title}</b><small>{skill.description}</small><span className="ss-tag"><SettingsGlyph state={!skill.uploaded ? 'none' : skill.upToDate ? 'done' : 'pending'} />{aiSkillState(skill)}</span></span>
              <input ref={element => { if (element) skillInputs.current.set(skill.key, element); else skillInputs.current.delete(skill.key) }} type="file" accept=".skill,.zip" hidden aria-label={`${skill.title} beceri dosyası`}
                onChange={event => { const file = event.currentTarget.files?.[0]; event.currentTarget.value = ''; if (file) void uploadSkill(skill, file) }} />
              <span className="ss-alert-row">
                <button type="button" className="ss-btn" disabled={blocked} onClick={() => skillInputs.current.get(skill.key)?.click()}><SettingsIcon name="plus" />{busy === `skill:${skill.key}` ? 'Yükleniyor…' : skill.uploaded ? 'Beceriyi Güncelle' : 'Beceriyi Yükle'}</button>
                {!skill.uploaded && <button type="button" className="ss-btn is-xs" disabled={blocked} onClick={() => void uploadSkill(skill)}>Panelle Gelen Sürümü Yükle</button>}
              </span>
              {skillFile?.key === skill.key && <span role="status" className="ss-help ss-ai-skill-result">{skillFile.label} yükleniyor…</span>}
              {skillResult?.key === skill.key && <span role="status" className="ss-help ss-ai-skill-result"><SettingsGlyph state={skillResult.kind} />{skillResult.message}</span>}
            </div>
          })}{saved.skills.length === 0 && <span className="ss-help">Yüklenecek beceri yok.</span>}</div>
        </div>
        <div className="ss-fld" role="group" aria-label="Kullanım Kaydı"><small>Kullanım Kaydı</small>
          {usage.isPending ? <LoadingState>Kullanım kaydı yükleniyor…</LoadingState> : usage.isError ? <span className="ss-help">Kullanım kaydı alınamadı.</span> : !usage.data?.items.length ? <span className="ss-help">Henüz yapay zekâ kullanımı yok.</span> : <div className="ss-ai-usage-wrap"><table className="ss-ai-usage">
            <thead><tr><th scope="col">Tarih</th><th scope="col">Özellik</th><th scope="col">Kişi</th><th scope="col">Konu</th><th scope="col">Token (Girdi / Çıktı)</th><th scope="col">Maliyet</th><th scope="col">Durum</th></tr></thead>
            <tbody>{usage.data.items.map(entry => <tr key={entry.id}>
              <td>{formatSettingsDate(entry.createdAt)}</td>
              <td>{entry.featureLabel || entry.feature}</td>
              <td>{entry.userName ?? '—'}</td>
              <td>{entry.subjectLabel ?? '—'}</td>
              <td className="is-num">{aiTokens(entry)}</td>
              <td className="is-num">{aiCost(entry.costUsd)}</td>
              <td title={entry.error ?? undefined}><span className="ss-tag"><SettingsGlyph state={entry.status === 'SUCCEEDED' ? 'done' : entry.status === 'CANCELLED' ? 'none' : 'bad'} />{entry.status === 'SUCCEEDED' ? 'Başarılı' : entry.status === 'CANCELLED' ? 'İptal Edildi' : 'Başarısız'}</span>{entry.error ? <small className="ss-ai-usage-error">{entry.error}</small> : null}</td>
            </tr>)}</tbody>
          </table></div>}
        </div>
      </>}
    </div>
    {saved && !forbidden ? bar : null}
  </>
}
