import type { NoticeToastKind } from './components/NoticeToast'

/** At most this many toasts stand in the top-right column; a newer one pushes the oldest timed one out. */
export const MAX_VISIBLE_TOASTS = 4
export const DEFAULT_TOAST_DURATION_MS = 5500

export type ToastInput = {
  kind: NoticeToastKind
  message: string
  title?: string
  /** Several outcomes (one per marketplace) shown as a list. */
  lines?: readonly string[]
  /** Time on screen; left out (or 0) the toast stays until it is closed. A pending toast always stays. */
  durationMs?: number
  /** A slot: a later toast with the same key takes this one's place (a running save becomes its result). */
  key?: string
  /** The screen that showed it, so the screen can take back its own toasts (its errors once edited, its running ones). */
  owner?: string
}

export type ToastItem = ToastInput & {
  id: string
  /** Raised whenever the toast is shown again, so its timer and countdown restart. */
  version: number
}

export function isPersistentToast(toast: Pick<ToastInput, 'kind' | 'durationMs'>) {
  return toast.kind === 'pending' || !toast.durationMs
}

function sameContent(left: ToastInput, right: ToastInput) {
  return left.kind === right.kind
    && (left.title ?? '') === (right.title ?? '')
    && left.message === right.message
    && (left.lines ?? []).join('\n') === (right.lines ?? []).join('\n')
}

/**
 * Adds a toast below the ones on screen. A toast with the key of one on screen replaces it in place, and a toast
 * identical to one on screen shows that one again (timer restarted) instead of a duplicate.
 */
export function addToast(items: readonly ToastItem[], input: ToastInput, newId: () => string, max = MAX_VISIBLE_TOASTS): ToastItem[] {
  const index = input.key
    ? items.findIndex(item => item.key === input.key)
    : items.findIndex(item => !item.key && sameContent(item, input))
  if (index >= 0) {
    const current = items[index]
    return items.map((item, position) => position === index ? { ...input, id: current.id, version: current.version + 1 } : item)
  }
  return capToasts([...items, { ...input, id: newId(), version: 0 }], max)
}

/**
 * Keeps at most `max` toasts; the newest always stays. The oldest timed toast goes first, then the oldest running one;
 * a persistent error goes only when nothing else is left to drop.
 */
export function capToasts(items: readonly ToastItem[], max = MAX_VISIBLE_TOASTS): ToastItem[] {
  const result = [...items]
  while (result.length > max) {
    const candidates = result.slice(0, -1)
    let index = candidates.findIndex(item => !isPersistentToast(item))
    if (index < 0) index = candidates.findIndex(item => item.kind === 'pending')
    if (index < 0) index = 0
    result.splice(index, 1)
  }
  return result
}

/** Removes the toasts the test picks; the same list comes back when none matches, so nothing re-renders. */
export function dismissToasts(items: readonly ToastItem[], test: (item: ToastItem) => boolean): readonly ToastItem[] {
  return items.some(test) ? items.filter(item => !test(item)) : items
}
