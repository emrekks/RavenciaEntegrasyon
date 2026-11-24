import { Fragment, useEffect, useEffectEvent, useRef, useState, type CSSProperties } from 'react'
import { LoadingState, Modal } from '../../../shared/components'
import { NoticeToast, type NoticeToastKind } from '../../../shared/components/NoticeToast'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontFamilyOptions, appearanceFontScale, appearanceFontSizeOptions, defaultAppearanceColorTheme, defaultAppearanceSettings, grafitAppearanceColorTheme, matchingColorThemeId, defaultLightPalette, useAppearanceSettings, type AppearanceColorThemeProfile, type AppearancePalette, type AppearanceSettings } from '../appearance-settings'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { readVisualThemePreference, switchVisualThemeSmoothly, visualThemeChangeEvent, type VisualTheme } from '../visual-theme'
import { PanelSelect } from '../../../shared/PanelSelect'
import { useDismissable } from '../../../shared/hooks/useDismissable'
import { useLacksElevatedRole } from '../../elevated-role'
import { type AppearanceSection, type SettingsGlyphState, SettingsIcon, SettingsGlyph, SettingsPaneHead, SettingsBar } from '../settings-ui'

function SettingsSwatches({ palette, large = false }: { palette: AppearancePalette; large?: boolean }) {
  return <span className={`ss-sws${large ? ' is-lg' : ''}`} aria-hidden="true">{[palette.bg, palette.surface, palette.primary, palette.accent].map((color, index) => <i key={index} style={{ backgroundColor: color }} />)}</span>
}

const notificationSamples: Array<{ kind: NoticeToastKind; label: string; glyph: SettingsGlyphState; message: string }> = [
  { kind: 'success', label: 'Başarılı', glyph: 'done', message: '#4570000002 için fatura oluşturuldu ve Hepsiburada’ya iletildi.' },
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

// The form starts from the saved settings: until they are in (the remembered copy is only a head start for the
// colours) it would show the defaults, and its theme picker would keep the default theme it opened with.
export function AppearanceSettingsSection({ section, onStatus }: { section: AppearanceSection | null; onStatus: (status: { dirty: boolean; themeCount: number }) => void }) {
  const appearance = useAppearanceSettings()
  if (appearance.isPending || appearance.isPlaceholderData) return section ? <div className="ss-body"><LoadingState>Görünüm ayarları yükleniyor…</LoadingState></div> : null
  return <AppearanceSettingsForm section={section} onStatus={onStatus} />
}

function AppearanceSettingsForm({ section, onStatus }: { section: AppearanceSection | null; onStatus: (status: { dirty: boolean; themeCount: number }) => void }) {
  const appearance = useAppearanceSettings()
  // The account's appearance is written by the owner and administrators only (PUT settings/appearance answers 403 to
  // other roles): they may preview, but nothing offers a save that would be refused.
  const saveDenied = useLacksElevatedRole()
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

  async function save(overwrite = false) {
    setBusy(true)
    setNotice(null)
    try {
      await (overwrite ? appearance.overwrite() : appearance.save(appearance.draft))
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
  const bar = saveDenied ? null : <SettingsBar title="Görünüm Ayarlarını Uygula" dirty={dirty} copy={appearance.conflict ? 'Ayar başka bir oturumda değişti · taslağınız korunuyor' : dirty ? 'Önizlemedesiniz · kaydedilmemiş değişiklikler var' : undefined}>{appearance.conflict
    // Newer settings were saved elsewhere while this draft held edits: the draft stays until one of these is chosen.
    ? <><button type="button" className="ss-btn" onClick={appearance.acceptServer} disabled={busy}>Güncel Ayarı Yükle</button><button type="button" className="ss-btn is-primary" onClick={() => void save(true)} disabled={busy}><SettingsIcon name="save" />{busy ? 'Kaydediliyor…' : 'Benimkiyle Üzerine Yaz'}</button></>
    : <><button type="button" className="ss-btn" onClick={applyDefaultAppearance} disabled={busy}><SettingsIcon name="reset" /><span className="ss-lbl-long">Varsayılanlara Dön</span><span className="ss-lbl-short">Varsayılan</span></button><button type="button" className="ss-btn is-primary" onClick={() => void save()} disabled={busy || !dirty}><SettingsIcon name="save" />{busy ? 'Kaydediliyor…' : <><span className="ss-lbl-long">Görünüm Ayarlarını Kaydet</span><span className="ss-lbl-short">Kaydet</span></>}</button></>}</SettingsBar>
  const loadError = <>{saveDenied && <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Görünüm ayarlarını yalnız hesap sahibi ve yöneticiler kaydeder; burada yaptığınız değişiklikler yalnız önizlemedir.</span></div>}{appearance.isError ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap görünüm ayarları alınamadı; varsayılan görünüm gösteriliyor.</span></div> : null}</>

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
        <div className="ss-th-new"><input className="ss-in is-sm" value={newThemeName} maxLength={60} placeholder="Yeni tema adı" aria-label="Yeni tema adı" onChange={event => setNewThemeName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && newThemeName.trim() && !busy && !saveDenied) void saveColorTheme() }} /><button type="button" className="ss-btn is-primary" disabled={busy || saveDenied || !newThemeName.trim()} onClick={() => void saveColorTheme()}><SettingsIcon name="plus" />Renk Temasını Kaydet</button></div>
        <div className="ss-themes">{appearance.draft.colorThemes.map(theme => <div key={theme.id} className={`ss-th${visualTheme === 'dark' && selectedThemeId === theme.id ? ' is-on' : ''}`}><button type="button" className="ss-th-pick" onClick={() => applyColorTheme(theme)}><SettingsSwatches palette={theme.palette} large /><span><b>{theme.name}</b><small>{theme.builtIn ? 'Varsayılan Tema' : 'Kayıtlı Özel Tema'}</small></span></button>{theme.builtIn ? <span className="ss-th-prot" title="Varsayılan tema korunur ve silinemez" aria-label="Korunuyor"><SettingsIcon name="lock" /></span> : <button type="button" className="ss-btn is-danger is-quiet is-xs" disabled={busy || saveDenied} onClick={() => setThemeToDelete(theme)} aria-label={`${theme.name} temasını sil`}>Sil</button>}</div>)}</div>
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
