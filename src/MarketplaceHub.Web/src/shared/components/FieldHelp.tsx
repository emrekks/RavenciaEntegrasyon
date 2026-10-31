import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './UiIcon'
import './field-help.css'

// A field's explanation behind a small question mark next to its label: shown while the pointer is on the mark or it
// has keyboard focus, and toggled by a tap (a tap elsewhere or Esc closes it). The text stays in the page (hidden) so
// the field can name it with aria-describedby={fieldHelpId}.
export function FieldHelp({ id, label, children }: { id?: string; label: string; children: ReactNode }) {
  const generated = useId()
  const tipId = id ?? `field-help-${generated}`
  const buttonRef = useRef<HTMLButtonElement>(null)
  const tipRef = useRef<HTMLSpanElement>(null)
  const [open, setOpen] = useState(false)
  const [place, setPlace] = useState<{ left: number; top: number; above: boolean } | null>(null)

  useLayoutEffect(() => {
    if (!open || !buttonRef.current) return
    const mark = buttonRef.current.getBoundingClientRect()
    const width = Math.min(280, window.innerWidth - 24)
    const left = Math.min(Math.max(12, mark.left + mark.width / 2 - width / 2), window.innerWidth - width - 12)
    const above = mark.top > 140
    setPlace({ left, top: above ? mark.top - 8 : mark.bottom + 8, above })
  }, [open])

  useEffect(() => {
    if (!open) return undefined
    const close = () => setOpen(false)
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); close() } }
    const onPointer = (event: PointerEvent) => { const target = event.target as Node; if (!buttonRef.current?.contains(target) && !tipRef.current?.contains(target)) close() }
    document.addEventListener('keydown', onKey, true)
    document.addEventListener('pointerdown', onPointer, true)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      document.removeEventListener('pointerdown', onPointer, true)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  return <>
    <button ref={buttonRef} type="button" className={`field-help${open ? ' is-open' : ''}`} aria-label={`${label}: açıklama`} aria-describedby={tipId} aria-expanded={open}
      onPointerEnter={event => { if (event.pointerType === 'mouse') setOpen(true) }}
      onPointerLeave={event => { if (event.pointerType === 'mouse') setOpen(false) }}
      onFocus={event => { if (event.currentTarget.matches(':focus-visible')) setOpen(true) }}
      onBlur={() => setOpen(false)}
      onClick={event => { event.preventDefault(); event.stopPropagation(); const mouse = (event.nativeEvent as PointerEvent).pointerType === 'mouse'; setOpen(value => mouse || !value) }}>
      <UiIcon name="help" size={15} />
    </button>
    {createPortal(<span ref={tipRef} id={tipId} role="tooltip" className={`field-help-tip${place?.above ? ' is-above' : ''}`} hidden={!open || !place} style={place ? { left: place.left, top: place.top } : undefined}>{children}</span>, document.body)}
  </>
}

// The text of a field's label with its help mark beside it, as one line above the control.
export function FieldLabel({ text, help, helpId }: { text: ReactNode; help?: ReactNode; helpId?: string }) {
  return <span className="field-label">{text}{help ? <FieldHelp id={helpId} label={typeof text === 'string' ? text : 'Alan'}>{help}</FieldHelp> : null}</span>
}
