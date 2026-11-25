import { useEffect, useEffectEvent, useLayoutEffect, useRef, useState, type DragEvent, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { LoadingState, Modal, TitleHint } from '../../../shared/components'
import { cloneShippingLabelLayout, defaultShippingLabelBlockPosition, defaultShippingLabelSettings, draggedShippingLabelPosition, labelBlockContentHeight, resizedShippingLabelPosition, shippingLabelCaptionStyle, shippingLabelDragStarted, shippingLabelFieldsPerRow, shippingLabelFitsText, shippingLabelShowsBarcodeText, type ShippingLabelCaptionStyle, normalizeShippingLabelSettings, overflowingLabelBlocks, ShippingLabelBlockBody, shippingLabelBlockCatalog, shippingLabelFields, ShippingLabelFit, shippingLabelLayoutIssues, shippingLabelSizeMm, shippingLabelTextSize, shippingLabelTextSizes, stackShippingLabelLayout, type ShippingLabelBlock, type ShippingLabelBlockKind, type ShippingLabelField, type ShippingLabelSettings, type ShippingLabelTemplate, type ShippingLabelValues, useShippingLabelTemplates } from '../../shipping'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { PanelSelect } from '../../../shared/PanelSelect'
import { type ShippingSection, SettingsIcon, SettingsGlyph, SettingsPaneHead, SettingsBar, SettingsSwitch } from '../settings-ui'

type ShippingDesignerTab = 'general' | 'text' | 'barcode'
type ShippingResizeDirection = 'n' | 'e' | 's' | 'w'
type ShippingRotateDirection = 'nw' | 'ne' | 'se' | 'sw'
const shippingResizeDirections: ShippingResizeDirection[] = ['n', 'e', 's', 'w']
const shippingRotateDirections: ShippingRotateDirection[] = ['nw', 'ne', 'se', 'sw']
const shippingResizeDirectionLabels: Record<ShippingResizeDirection, string> = { n: 'üst kenar', e: 'sağ kenar', s: 'alt kenar', w: 'sol kenar' }
const shippingRotateDirectionLabels: Record<ShippingRotateDirection, string> = { nw: 'sol üst köşe', ne: 'sağ üst köşe', se: 'sağ alt köşe', sw: 'sol alt köşe' }

const shippingBlockLabels: Record<ShippingLabelBlockKind, string> = { trackingBarcode: 'Takip Barkodu', address: 'Teslimat Adresi', orderInfo: 'Paket / Sipariş Bilgileri', packageBarcode: 'Paket Barkodu', sender: 'Gönderici ve Kargo Bilgileri', custom: 'Özel İçerik Bloğu' }
const shippingAlignOptions = [['left', 'Sol'], ['center', 'Orta'], ['right', 'Sağ']] as const
const shippingCaptionStyleOptions: Array<[ShippingLabelCaptionStyle, string]> = [['above', 'Başlık Üstte'], ['beside', 'Başlık Yanında'], ['none', 'Başlıksız']]

export function ShippingLabelSettingsPanel({ section, settings, saved, dirty, saving, loadFailed, loading, format, setFormat, onChange, onSave, saveDenied = false, conflict = false, onAcceptServer, onOverwrite }: { section: ShippingSection | null; settings: ShippingLabelSettings; saved: ShippingLabelSettings | null; dirty: boolean; saving: boolean; loadFailed: boolean; loading: boolean; format: 'a4' | 'sticker'; setFormat: (format: 'a4' | 'sticker') => void; onChange: (value: ShippingLabelSettings) => void; onSave: () => void; saveDenied?: boolean; conflict?: boolean; onAcceptServer?: () => void; onOverwrite?: () => void }) {
  const [selectedId, setSelectedId] = useState<string | null>(settings.layout[format][0]?.id ?? null)
  const [designerTab, setDesignerTab] = useState<ShippingDesignerTab>('general')
  const [customTitle, setCustomTitle] = useState('')
  const [customText, setCustomText] = useState('')
  // The account's templates (every device shows the same list); this device's own copy only while they cannot be read.
  const accountTemplates = useShippingLabelTemplates()
  const templates = accountTemplates.templates
  const [templateSaving, setTemplateSaving] = useState(false)
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
  async function saveTemplate(name: string) {
    const trimmed = name.trim()
    if (!trimmed) {
      setTemplateNotice({ kind: 'error', message: 'Şablon adı gerekli.' })
      return
    }
    const template: ShippingLabelTemplate = { id: `template-${Date.now()}`, name: trimmed.slice(0, 80), savedAt: new Date().toISOString(), settings: JSON.parse(JSON.stringify(settings)) as ShippingLabelSettings }
    setTemplateSaving(true)
    try {
      await accountTemplates.saveTemplate(template)
      setSelectedTemplateId(template.id)
      setTemplateNameOpen(false)
      setTemplateNotice({ kind: 'success', message: `“${template.name}” şablonu hesaba kaydedildi; tüm cihazlarda görünür.` })
    } catch (error) {
      setTemplateNotice({ kind: 'error', message: `Şablon kaydedilemedi. ${error instanceof Error ? error.message : ''}`.trim() })
    } finally {
      setTemplateSaving(false)
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
  async function deleteTemplate() {
    if (!selectedTemplateId) return
    setTemplateSaving(true)
    try {
      await accountTemplates.deleteTemplate(selectedTemplateId)
      setSelectedTemplateId('')
    } catch (error) {
      setTemplateNotice({ kind: 'error', message: `Şablon silinemedi. ${error instanceof Error ? error.message : ''}`.trim() })
    } finally {
      setTemplateSaving(false)
    }
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
  // A role the server refuses the save sees no save bar, and says so above the section instead.
  const bar = saveDenied ? null : <SettingsBar title="Etiket Düzenini Yayınla" dirty={dirty} hint="Gönderici bilgileri ve kâğıt yerleşimi tüm yazdırma akışlarında kullanılır." copy={conflict ? 'Ayar başka bir oturumda değişti · taslağınız korunuyor' : dirty ? 'Kaydedilmemiş değişiklikler var' : undefined}>{conflict
    // Newer settings were saved elsewhere while this design held edits: the draft stays until one of these is chosen.
    ? <><button type="button" className="ss-btn" disabled={saving} onClick={onAcceptServer}>Güncel Ayarı Yükle</button><button type="button" className="ss-btn is-primary" disabled={saving} onClick={onOverwrite}><SettingsIcon name="save" />{saving ? 'Kaydediliyor…' : 'Benimkiyle Üzerine Yaz'}</button></>
    : <>{dirty && saved ? <button type="button" className="ss-btn" disabled={saving} onClick={() => onChange(saved)}>Vazgeç</button> : null}<button type="button" className="ss-btn is-primary" disabled={!dirty || saving} onClick={onSave}><SettingsIcon name="save" />{saving ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button></>}</SettingsBar>
  const loadError = <>{saveDenied && <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Kargo etiketi ayarlarını yalnız hesap sahibi ve yöneticiler kaydeder; burada yaptığınız değişiklikler kaydedilmez.</span></div>}{loadFailed ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Hesap kargo ayarları alınamadı; geçici yerel ayarlar gösteriliyor.</span></div> : null}</>
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
          <button type="button" className="ss-btn" disabled={!selectedTemplateId || templateSaving} onClick={() => setTemplateDeleteOpen(true)}><SettingsIcon name="trash" />Şablonu Sil</button>
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

    <Modal open={templateNameOpen} title="Şablon Kaydet" hint="Geçerli kâğıt yerleşimini bir adla saklayın; sonra tek seçimle geri yükleyin. Şablonlar hesabınızda saklanır; tüm bilgisayar ve telefonlarda aynı liste görünür." className="ss-modal is-sm" onClose={() => setTemplateNameOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateNameOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" disabled={!templateNameDraft.trim() || templateSaving} onClick={() => void saveTemplate(templateNameDraft)}>Kaydet</button></>}>
      <label className="ss-fld"><small>Şablon Adı</small><input className="ss-in" data-autofocus value={templateNameDraft} maxLength={80} onChange={event => setTemplateNameDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && !templateSaving) void saveTemplate(templateNameDraft) }} /></label>
      <div className="ss-sum"><div><SettingsIcon name="layout" /><span>{format === 'a4' ? `A4 · Sayfada ${settings.a4LabelsPerPage} Etiket` : `Sticker · ${settings.stickerWidthMm} × ${settings.stickerHeightMm} mm`}<small>{layout.length} blok · gönderici {settings.senderName || '—'}</small></span><span /></div></div>
    </Modal>
    <Modal open={templateDeleteOpen && Boolean(selectedTemplate)} title="Şablonu Sil" description={selectedTemplate ? `“${selectedTemplate.name}” şablonu hesaptan silinecek; hiçbir cihazda görünmez. Kâğıttaki yerleşim değişmez.` : undefined} className="ss-modal is-sm" onClose={() => setTemplateDeleteOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setTemplateDeleteOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-danger is-solid" disabled={templateSaving} onClick={() => { void deleteTemplate(); setTemplateDeleteOpen(false) }}>Şablonu Sil</button></>}>
      <div className="ss-callout"><SettingsIcon name="alert" /><span><b>Bu işlem geri alınamaz.</b> Aynı yerleşimi yeniden kullanmak için önce yeni adla kaydedin.</span></div>
    </Modal>
    <Modal open={layoutResetOpen} title="Varsayılan Yerleşimi Yükle" description={format === 'sticker' ? 'Kâğıttaki blokların yerine Trendyol Express’in 100 × 100 mm sticker’ına benzeyen varsayılan yerleşim gelir; sticker ölçüsü de 100 × 100 mm olur. Kaydedene kadar hesabınızdaki düzen değişmez.' : 'Kâğıttaki blokların yerine varsayılan yerleşim gelir. Kaydedene kadar hesabınızdaki düzen değişmez.'} className="ss-modal is-sm" onClose={() => setLayoutResetOpen(false)} footer={<><button type="button" className="ss-btn" onClick={() => setLayoutResetOpen(false)}>Vazgeç</button><button type="button" className="ss-btn is-primary" onClick={() => { resetLayout(); setLayoutResetOpen(false) }}>Varsayılanı Yükle</button></>}>
      <div className="ss-sum"><div><SettingsGlyph state="none" /><span>Şu Anki Yerleşim<small>{layout.length} blok · {layout.filter(block => block.rotation).length} döndürülmüş</small></span><span /></div><div><SettingsGlyph state="done" /><span>Varsayılan Yerleşim<small>{defaultShippingLabelSettings.layout[format].length} blok · {defaultShippingLabelSettings.layout[format].map(block => shippingBlockLabels[block.kind].toLocaleLowerCase('tr-TR')).join(', ')}</small></span><span /></div></div>
    </Modal>
  </>
}


// What the designer fills its label with: one realistic order, so a block shows the height a real one needs.
const shippingPreviewValues: ShippingLabelValues = { trackingNumber: '73300000000000001', packageNumber: '4290000002', orderNumber: '#11700000009', customerName: 'Fehime Mat', address: 'Gümüşsuyu Mah. Selime Hatun Cami Sok. No:3 D:5\nFındıklı / Beyoğlu / İstanbul', cargoProvider: 'Trendyol Express', senderName: 'Ravencia', senderAddress: '403. Cad. No:24/2\nKarşıyaka / İzmir', customerEmail: 'musteri@example.com' }
