import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { UiIcon } from '../../../shared/components'
import { useDismissable } from '../../../shared/hooks/useDismissable'

export type VariantMediaBulkSelectOption = { id: string; label: string; assigned?: boolean }

// A native select cannot show an icon, so the bulk image picker lists its option groups and values in its own listbox:
// a value that already has images carries the round check the image tiles use. The placeholder stays the first option,
// as in the native select it replaced, so a choice can be cleared again.
export function VariantMediaBulkSelect({ label, value, placeholder, options, disabled = false, onChange }: {
  label: string
  value: string
  placeholder: string
  options: VariantMediaBulkSelectOption[]
  disabled?: boolean
  onChange: (value: string) => void
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const id = useId()
  const selected = options.find(option => option.id === value)
  const items: VariantMediaBulkSelectOption[] = [{ id: '', label: placeholder }, ...options]

  useDismissable(open, [rootRef], () => setOpen(false), { triggerRef })

  // Opening moves focus onto the chosen option (the placeholder when nothing is chosen), as a native select does.
  useEffect(() => {
    if (!open) return
    const buttons = [...listRef.current?.querySelectorAll<HTMLButtonElement>('[role="option"]') ?? []]
    ;(buttons.find(button => button.getAttribute('aria-selected') === 'true') ?? buttons[0])?.focus()
  }, [open])

  function choose(next: string) {
    setOpen(false)
    triggerRef.current?.focus()
    if (next !== value) onChange(next)
  }

  function triggerKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
    event.preventDefault()
    setOpen(true)
  }

  function listKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Tab') { setOpen(false); return }
    const buttons = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="option"]')]
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement)
    const next = event.key === 'ArrowDown' ? Math.min(index + 1, buttons.length - 1)
      : event.key === 'ArrowUp' ? Math.max(index - 1, 0)
        : event.key === 'Home' ? 0
          : event.key === 'End' ? buttons.length - 1
            : -1
    if (next < 0) return
    event.preventDefault()
    buttons[next]?.focus()
  }

  return <div className="variant-media-bulk-field category-attribute-dropdown" ref={rootRef}>
    <span id={`${id}-label`}>{label}</span>
    <button ref={triggerRef} type="button" className={`category-attribute-select-trigger active${selected ? '' : ' is-placeholder'}`} aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? `${id}-list` : undefined} aria-labelledby={`${id}-label ${id}-value`} disabled={disabled} onClick={() => setOpen(current => !current)} onKeyDown={triggerKeyDown}>
      <span id={`${id}-value`}>{selected?.label ?? placeholder}{selected?.assigned && <span className="sr-only">, görsel atandı</span>}</span>
      {selected?.assigned && <i className="variant-media-value-check" aria-hidden="true"><UiIcon name="check" /></i>}
      <UiIcon name="chevronDown" />
    </button>
    {open && <div ref={listRef} id={`${id}-list`} className="category-attribute-dropdown-menu" role="listbox" tabIndex={-1} aria-labelledby={`${id}-label`} onKeyDown={listKeyDown}>
      <div className="category-attribute-dropdown-options">
        {items.map(option => {
          const isSelected = option.id === value
          return <button type="button" role="option" aria-selected={isSelected} tabIndex={-1} className={`${isSelected ? 'active' : ''}${option.id ? '' : ' is-placeholder'}`} key={option.id || 'placeholder'} onClick={() => choose(option.id)}>
            <span>{option.label}{option.assigned && <span className="sr-only">, görsel atandı</span>}</span>
            {option.assigned ? <i className="variant-media-value-check" aria-hidden="true"><UiIcon name="check" /></i> : null}
          </button>
        })}
      </div>
    </div>}
  </div>
}
