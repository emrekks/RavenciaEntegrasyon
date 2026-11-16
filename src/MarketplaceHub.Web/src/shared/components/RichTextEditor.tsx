// A rich text editor (visual and HTML modes); the product description editor uses it (styles in the product editor's CSS).
import { useEffect, useEffectEvent, useRef, useState } from 'react'
import { UiIcon, type UiIconName } from './UiIcon'
import { usePromptDialog } from './ConfirmDialog'
import { httpsUrlProblem } from '../safe-url'
import { sanitizeRichText } from '../security/sanitizeHtml'
import { PanelSelect } from '../PanelSelect'

export function RichTextTool({ icon, label, onClick, disabled = false, active = false }: { icon: UiIconName; label: string; onClick: () => void; disabled?: boolean; active?: boolean }) {
  return <button type="button" className={active ? 'rich-text-tool active' : 'rich-text-tool'} title={label} aria-label={label} aria-pressed={active || undefined} onClick={onClick} disabled={disabled}><span className="rich-text-tool-icon" aria-hidden="true"><UiIcon name={icon} /></span></button>
}
export const RICH_TEXT_STATE_COMMANDS = ['bold', 'italic', 'underline', 'justifyLeft', 'justifyCenter', 'justifyRight', 'insertUnorderedList'] as const
export function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [promptAddress, addressDialog] = usePromptDialog()
  const visualEditor = useRef<HTMLDivElement>(null)
  const lastValue = useRef(value)
  // The canvas gets its HTML once; later changes are written by the effects below. A new HTML string on every
  // keystroke made React rewrite the canvas and threw the caret back to the start (Enter on the last line).
  const [initialVisualHtml] = useState(() => sanitizeRichText(value))
  const [htmlMode, setHtmlMode] = useState(false)
  const plainTextLength = value.replace(/<[^>]*>/g, '').trim().length
  const [activeCommands, setActiveCommands] = useState<ReadonlySet<string>>(() => new Set())
  // Reflects the format at the caret (Kalın açık gibi); only reads state, never changes the content.
  useEffect(() => {
    function refresh() {
      const selection = window.getSelection()
      const inside = !htmlMode && !!selection?.anchorNode && !!visualEditor.current?.contains(selection.anchorNode)
      const next = new Set<string>()
      if (inside) for (const command of RICH_TEXT_STATE_COMMANDS) { try { if (document.queryCommandState(command)) next.add(command) } catch { /* unsupported command */ } }
      setActiveCommands(previous => previous.size === next.size && [...next].every(command => previous.has(command)) ? previous : next)
    }
    document.addEventListener('selectionchange', refresh)
    refresh()
    return () => document.removeEventListener('selectionchange', refresh)
  }, [htmlMode])

  useEffect(() => {
    if (!htmlMode && visualEditor.current && lastValue.current !== value) visualEditor.current.innerHTML = sanitizeRichText(value)
    lastValue.current = value
  }, [htmlMode, value])

  // Back from HTML mode the visual editor shows the current text again.
  const showVisualValue = useEffectEvent(() => { if (!htmlMode && visualEditor.current) visualEditor.current.innerHTML = sanitizeRichText(value) })
  useEffect(() => showVisualValue(), [htmlMode])

  // Typing only reports the cleaned value; the canvas is rewritten only after a toolbar command, never mid-typing.
  function syncVisualValue(rewrite = false) {
    const next = sanitizeRichText(visualEditor.current?.innerHTML ?? '')
    if (rewrite && visualEditor.current && visualEditor.current.innerHTML !== next) visualEditor.current.innerHTML = next
    lastValue.current = next
    onChange(next === '<br>' ? '' : next)
  }

  function runCommand(command: string, argument?: string) {
    if (htmlMode || !visualEditor.current) return
    visualEditor.current.focus()
    // Colour and size are written as inline styles (<span style>), which the sanitizer keeps; the default <font> tag
    // is not allowed and was stripped, so the tools did nothing.
    try { document.execCommand('styleWithCSS', false, 'true') } catch { /* unsupported command */ }
    document.execCommand(command, false, argument)
    syncVisualValue(true)
  }

  // The address window takes focus away from the editor: the selection is kept and put back before the command runs.
  function editorSelection() {
    const selection = window.getSelection()
    return selection && selection.rangeCount && visualEditor.current?.contains(selection.anchorNode) ? selection.getRangeAt(0).cloneRange() : null
  }
  async function insertAddress(kind: 'link' | 'image') {
    if (htmlMode || !visualEditor.current) return
    const range = editorSelection()
    const url = await promptAddress(kind === 'link'
      ? { title: 'Bağlantı Ekle', label: 'Bağlantı Adresi', initialValue: 'https://', inputType: 'url', confirmLabel: 'Ekle', validate: httpsUrlProblem }
      : { title: 'Görsel Ekle', label: 'Görsel Adresi', initialValue: 'https://', inputType: 'url', confirmLabel: 'Ekle', validate: httpsUrlProblem })
    if (!url || !visualEditor.current) return
    visualEditor.current.focus()
    if (range) { const selection = window.getSelection(); selection?.removeAllRanges(); selection?.addRange(range) }
    runCommand(kind === 'link' ? 'createLink' : 'insertImage', url)
  }
  const insertLink = () => void insertAddress('link')
  const insertImage = () => void insertAddress('image')

  function clearFormatting() {
    runCommand('removeFormat')
    runCommand('formatBlock', 'p')
  }

  return <div className="rich-text-editor rich-text-editor-pro">{addressDialog}
    <div className="rich-text-editor-head">
      <div><strong>Ürün Açıklaması</strong><span className="rich-text-mode-note">{htmlMode ? 'HTML kodu düzenleniyor' : 'Görsel düzenleyici'}</span></div>
      <div className="rich-text-editor-meta"><span>{plainTextLength} karakter</span><div className="rich-text-mode-switch" role="group" aria-label="Açıklama görünümü"><button type="button" className={!htmlMode ? 'active' : ''} aria-pressed={!htmlMode} onClick={() => setHtmlMode(false)}><UiIcon name="sparkle" /> Görsel</button><button type="button" className={htmlMode ? 'active' : ''} aria-pressed={htmlMode} onClick={() => setHtmlMode(true)}><UiIcon name="code" /> HTML</button></div></div>
    </div>
    <div className="rich-text-toolbar" role="toolbar" aria-label="Açıklama biçimlendirme araçları">
      <div className="rich-text-tool-group" role="group" aria-label="Düzenleme"><RichTextTool icon="undo" label="Geri Al" onClick={() => runCommand('undo')} disabled={htmlMode} /><RichTextTool icon="redo" label="Yinele" onClick={() => runCommand('redo')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" role="group" aria-label="Yazı boyutu"><div role="presentation" onMouseDown={event => event.preventDefault()}><PanelSelect ariaLabel="Yazı Boyutu" value="" placeholder="Boyut" disabled={htmlMode} width={120} options={[{ value: '12', label: 'Küçük' }, { value: '15', label: 'Normal' }, { value: '19', label: 'Büyük' }]} onChange={next => { const map: Record<string, string> = { '12': '2', '15': '3', '19': '5' }; runCommand('fontSize', map[next] ?? '3') }} /></div></div>
      <div className="rich-text-tool-group" role="group" aria-label="Metin biçimi"><RichTextTool icon="bold" label="Kalın" onClick={() => runCommand('bold')} disabled={htmlMode} active={activeCommands.has('bold')} /><RichTextTool icon="italic" label="İtalik" onClick={() => runCommand('italic')} disabled={htmlMode} active={activeCommands.has('italic')} /><RichTextTool icon="underline" label="Altı Çizili" onClick={() => runCommand('underline')} disabled={htmlMode} active={activeCommands.has('underline')} /><RichTextTool icon="textColor" label="Metin Rengi" onClick={() => runCommand('foreColor', '#7652e8')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group rich-text-alignment-group" role="group" aria-label="Paragraf hizası"><RichTextTool icon="alignLeft" label="Sola Hizala" onClick={() => runCommand('justifyLeft')} disabled={htmlMode} active={activeCommands.has('justifyLeft')} /><RichTextTool icon="alignCenter" label="Ortala" onClick={() => runCommand('justifyCenter')} disabled={htmlMode} active={activeCommands.has('justifyCenter')} /><RichTextTool icon="alignRight" label="Sağa Hizala" onClick={() => runCommand('justifyRight')} disabled={htmlMode} active={activeCommands.has('justifyRight')} /></div>
      <div className="rich-text-tool-group" role="group" aria-label="Liste ve paragraf"><RichTextTool icon="list" label="Madde Listesi" onClick={() => runCommand('insertUnorderedList')} disabled={htmlMode} active={activeCommands.has('insertUnorderedList')} /><RichTextTool icon="paragraph" label="Paragraf" onClick={() => runCommand('formatBlock', 'p')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" role="group" aria-label="İçerik ekle"><RichTextTool icon="link" label="Bağlantı Ekle" onClick={insertLink} disabled={htmlMode} /><RichTextTool icon="image" label="Görsel Ekle" onClick={insertImage} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" role="group" aria-label="Temizle"><RichTextTool icon="clearFormatting" label="Biçimi Temizle" onClick={clearFormatting} disabled={htmlMode} /></div>
    </div>
    {htmlMode ? <textarea className="rich-text-html-editor" value={value} onChange={event => { lastValue.current = event.target.value; onChange(event.target.value) }} aria-label="Açıklama HTML kodu" placeholder="<p>Ürünün öne çıkan özelliklerini anlatın…</p>" spellCheck={false} /> : <div ref={visualEditor} className="rich-text-canvas" contentEditable role="textbox" aria-multiline="true" aria-label="Açıklama" data-placeholder="Ürünün öne çıkan özelliklerini anlatın…" suppressContentEditableWarning dangerouslySetInnerHTML={{ __html: initialVisualHtml }} onFocus={() => document.execCommand("defaultParagraphSeparator", false, "p")} onInput={() => syncVisualValue()} />}
  </div>
}
