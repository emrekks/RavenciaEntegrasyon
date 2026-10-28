import { useEffect, useRef, type RefObject } from 'react'

// One rule for every dropdown, popover, menu and picker: it closes on a press anywhere outside it (also inside another
// popover, on a scrollbar or on an element that stops the event), on Escape (focus goes back to its trigger), on a route
// change, and when another one opens. Selecting an item is the component's own close.
//
// The press is caught on the capture phase of the document, so an element that stops pointerdown/mousedown (modals do,
// to keep their backdrop from closing them) cannot keep a popover open. `refs` lists everything that belongs to the
// popover: its trigger or wrapper and, for a portalled panel, the panel itself.

export type DismissReason = 'outside' | 'escape' | 'route' | 'other-opened'

type Entry = { refs: ReadonlyArray<RefObject<Element | null>>; isInside?: (node: Node) => boolean; close: (reason: DismissReason) => void }
const openEntries = new Set<Entry>()

const contains = (entry: Entry, node: Node | null) => !!node && (entry.refs.some(ref => ref.current?.contains(node)) || !!entry.isInside?.(node))

/** Closes every open popover (the app calls it when the route changes). */
export function closeAllDismissables(reason: DismissReason = 'route') {
  for (const entry of [...openEntries]) entry.close(reason)
}

type Options = {
  /** Where focus goes back after Escape; the first ref's first focusable element when omitted. */
  triggerRef?: RefObject<HTMLElement | null>
  /** false keeps the popover open when another one opens (a nested picker inside a popover is always kept). */
  exclusive?: boolean
  /** For popovers found by class rather than by ref (one of several column filters): true when a node belongs to it. */
  isInside?: (node: Node) => boolean
}

export function useDismissable(open: boolean, refs: ReadonlyArray<RefObject<Element | null>>, onClose: (reason: DismissReason) => void, options: Options = {}) {
  const latest = useRef({ refs, onClose, options })
  latest.current = { refs, onClose, options }

  useEffect(() => {
    if (!open) return
    const focusTrigger = () => {
      const { triggerRef } = latest.current.options
      const host = latest.current.refs[0]?.current
      const target = triggerRef?.current
        ?? (host instanceof HTMLElement && host.matches('button, [tabindex], input, select, a[href]') ? host : host?.querySelector<HTMLElement>('button, [tabindex]:not([tabindex="-1"]), input, select, a[href]'))
      target?.focus()
    }
    const entry: Entry = {
      get refs() { return latest.current.refs },
      isInside: node => latest.current.options.isInside?.(node) ?? false,
      close: reason => {
        if (!openEntries.has(entry)) return
        openEntries.delete(entry)
        latest.current.onClose(reason)
        if (reason === 'escape') focusTrigger()
      }
    }
    // Another popover opening closes this one, unless one of them sits inside the other (a picker inside a drawer).
    if (latest.current.options.exclusive !== false) {
      const anchor = latest.current.refs[0]?.current ?? null
      for (const other of [...openEntries]) {
        const nested = contains(other, anchor) || other.refs.some(ref => contains(entry, ref.current))
        if (!nested) other.close('other-opened')
      }
    }
    openEntries.add(entry)
    const press = (event: Event) => {
      const target = event.target as Node | null
      if (contains(entry, target)) return
      // A press inside a popover nested in this one (portalled elsewhere) counts as inside.
      for (const other of openEntries) if (other !== entry && contains(other, target) && other.refs.some(ref => contains(entry, ref.current))) return
      entry.close('outside')
    }
    const key = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return
      // Only the most recently opened popover answers Escape; the window behind it stays open.
      const last = [...openEntries].pop()
      if (last !== entry) return
      event.preventDefault()
      event.stopPropagation()
      entry.close('escape')
    }
    // mousedown too: it follows pointerdown on a real press (the second call finds the entry closed) and is what
    // synthetic presses in tests and some assistive tools send.
    document.addEventListener('pointerdown', press, true)
    document.addEventListener('mousedown', press, true)
    document.addEventListener('keydown', key, true)
    return () => {
      openEntries.delete(entry)
      document.removeEventListener('pointerdown', press, true)
      document.removeEventListener('mousedown', press, true)
      document.removeEventListener('keydown', key, true)
    }
  }, [open])
}

/** An `isInside` test for popovers found by class: true for a node inside an element matching `selector`. */
export function insideSelector(selector: string) {
  return (node: Node) => Boolean((node instanceof Element ? node : node.parentElement)?.closest(selector))
}
