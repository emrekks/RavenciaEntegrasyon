import { useLayoutEffect } from 'react'

// Page state the stylesheets read from <html> (html[data-…]) instead of a :has() anchored on html, body or the shell.
// Chrome re-checks such a :has() on every change anywhere below its anchor and then restyles the anchor's whole subtree
// (5000+ elements on Siparişler with 50 rows, about half a second on a slow phone for one row selection); a plain
// attribute on <html> restyles only the elements its rules name. html[data-rv-layer] (useScrollLock.ts) works the same way.
// Every owner holds its own mark: the attribute stays while any owner holds it (the latest owner's value wins) and goes
// with the last one, so two components that set and clear it in either order never leave it wrong.
type Owner = { value: string }
const owners = new Map<string, Owner[]>()

function sync(name: string) {
  const list = owners.get(name)
  if (list?.length) document.documentElement.setAttribute(name, list[list.length - 1].value)
  else document.documentElement.removeAttribute(name)
}

/** Sets `name` on <html> until the returned function is called (counted: other owners keep it). */
export function holdRootMark(name: string, value = '') {
  const owner: Owner = { value }
  owners.set(name, [...(owners.get(name) ?? []), owner])
  sync(name)
  let held = true
  return () => {
    if (!held) return
    held = false
    const rest = (owners.get(name) ?? []).filter(item => item !== owner)
    if (rest.length) owners.set(name, rest); else owners.delete(name)
    sync(name)
  }
}

/** Holds `name` on <html> with `value` while value is a string (set before the browser paints, so there is no flash); null or false clears this owner's hold. */
export function useRootMark(name: string, value: string | null | false) {
  useLayoutEffect(() => {
    if (value === null || value === false) return undefined
    return holdRootMark(name, value)
  }, [name, value])
}

// A text box, list box or editable area (not a check box, radio, button, range, file or colour input): the phone keyboard is open while one has focus.
export const textEntrySelector = 'input:not([type="checkbox"], [type="radio"], [type="button"], [type="submit"], [type="range"], [type="file"], [type="color"]), textarea, select, [contenteditable=""], [contenteditable="true"]'

const typing = () => {
  const active = document.activeElement
  const focused = typeof document.hasFocus !== 'function' || document.hasFocus()
  return focused && active instanceof Element && active !== document.body && active.matches(textEntrySelector)
}

/**
 * Holds `name` on <html> while a text entry has focus (what `body:has(<text entry>:focus)` matched). Focus moves are
 * followed through focusin/focusout (window blur and focus included); a focused field that is removed without a focusout
 * is caught by a mutation observer that runs only while the mark is held.
 */
export function useTextEntryFocusMark(name: string) {
  useLayoutEffect(() => {
    let release: (() => void) | null = null
    let observer: MutationObserver | null = null
    let disposed = false
    const update = () => {
      if (disposed) return
      const now = typing()
      if (now && !release) {
        release = holdRootMark(name)
        if (typeof MutationObserver === 'function') { observer = new MutationObserver(() => { if (!typing()) update() }); observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['type', 'contenteditable'] }) }
      } else if (!now && release) {
        observer?.disconnect(); observer = null
        release(); release = null
      }
    }
    // While focusout runs the focus has normally left already; the second look after it covers a browser that still reports the old field.
    const left = () => { update(); queueMicrotask(update) }
    update()
    document.addEventListener('focusin', update)
    document.addEventListener('focusout', left)
    window.addEventListener('focus', update)
    window.addEventListener('blur', update)
    return () => {
      disposed = true
      document.removeEventListener('focusin', update)
      document.removeEventListener('focusout', left)
      window.removeEventListener('focus', update)
      window.removeEventListener('blur', update)
      observer?.disconnect()
      release?.()
      release = null
    }
  }, [name])
}
