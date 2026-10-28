import { describe, expect, it } from 'vitest'
import { addToast, capToasts, dismissToasts, isPersistentToast, MAX_VISIBLE_TOASTS, type ToastInput, type ToastItem } from './toast-stack'

function ids() {
  let next = 0
  return () => `t${++next}`
}

function push(items: readonly ToastItem[], inputs: ToastInput[], newId = ids()) {
  return inputs.reduce<readonly ToastItem[]>((current, input) => addToast(current, input, newId), items)
}

const timed = (message: string): ToastInput => ({ kind: 'success', message, durationMs: 4000 })
const stuckError = (message: string): ToastInput => ({ kind: 'error', message })

describe('toast stack', () => {
  it('puts a new toast below the ones already shown', () => {
    const items = push([], [timed('Bir'), timed('İki'), timed('Üç')])
    expect(items.map(item => item.message)).toEqual(['Bir', 'İki', 'Üç'])
    expect(new Set(items.map(item => item.id)).size).toBe(3)
  })

  it('shows an identical toast again in place, its timer restarted, instead of a duplicate', () => {
    const newId = ids()
    const first = push([], [{ ...timed('Kaydedildi'), title: 'Ürün' }, timed('Başka')], newId)
    const again = addToast(first, { ...timed('Kaydedildi'), title: 'Ürün' }, newId)
    expect(again.map(item => item.message)).toEqual(['Kaydedildi', 'Başka'])
    expect(again[0].id).toBe(first[0].id)
    expect(again[0].version).toBe(first[0].version + 1)
    // Another title or tone is another toast.
    expect(addToast(again, { ...timed('Kaydedildi'), title: 'Varyant' }, newId)).toHaveLength(3)
    expect(addToast(again, { ...timed('Kaydedildi'), title: 'Ürün', kind: 'info' }, newId)).toHaveLength(3)
  })

  it('turns a keyed running toast into its result in its own place', () => {
    const newId = ids()
    const running = push([], [timed('Önce'), { kind: 'pending', message: 'Kaydediliyor…', key: 'save' }, timed('Sonra')], newId)
    const done = addToast(running, { kind: 'success', message: 'Kaydedildi', key: 'save', durationMs: 6000 }, newId)
    expect(done.map(item => item.message)).toEqual(['Önce', 'Kaydedildi', 'Sonra'])
    expect(done[1]).toMatchObject({ id: running[1].id, kind: 'success', version: 1 })
  })

  it(`keeps at most ${MAX_VISIBLE_TOASTS}: the oldest timed one goes first, persistent errors stay`, () => {
    const items = push([], [stuckError('Hata 1'), timed('Eski'), { kind: 'pending', message: 'Sürüyor' }, stuckError('Hata 2'), timed('Yeni')])
    expect(items.map(item => item.message)).toEqual(['Hata 1', 'Sürüyor', 'Hata 2', 'Yeni'])
    const next = push(items, [timed('En yeni')])
    expect(next.map(item => item.message)).toEqual(['Hata 1', 'Sürüyor', 'Hata 2', 'En yeni'])
  })

  it('drops a running toast before an error, and an error only when nothing else is left; the newest always stays', () => {
    const errors = push([], [stuckError('A'), { kind: 'pending', message: 'Sürüyor' }, stuckError('B'), stuckError('C')])
    expect(push(errors, [stuckError('D')]).map(item => item.message)).toEqual(['A', 'B', 'C', 'D'])
    const onlyErrors = push([], [stuckError('A'), stuckError('B'), stuckError('C'), stuckError('D')])
    expect(push(onlyErrors, [timed('Yeni')]).map(item => item.message)).toEqual(['B', 'C', 'D', 'Yeni'])
    expect(capToasts(onlyErrors, 2).map(item => item.message)).toEqual(['C', 'D'])
  })

  it('closes one toast and leaves the list untouched when nothing matches', () => {
    const items = push([], [timed('Bir'), timed('İki')])
    expect(dismissToasts(items, item => item.id === items[0].id).map(item => item.message)).toEqual(['İki'])
    expect(dismissToasts(items, item => item.id === 'yok')).toBe(items)
  })

  it('treats pending and untimed toasts as persistent', () => {
    expect(isPersistentToast({ kind: 'pending', durationMs: 3000 })).toBe(true)
    expect(isPersistentToast({ kind: 'error' })).toBe(true)
    expect(isPersistentToast({ kind: 'error', durationMs: 8000 })).toBe(false)
  })
})
