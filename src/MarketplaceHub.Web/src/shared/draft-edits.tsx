import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { DockActions, useDockHost } from './components/dock-actions'
import './draft-edits.css'

// Typing into a stock or price box only makes a draft; nothing is saved (and so nothing reaches a marketplace) until
// the person presses Kaydet in the DraftSaveBar and confirms. One hook and one bar for every window that edits values
// in place (Hızlı Stok, Hızlı Fiyat, Stok ve Fiyat Güncelle).
export type DraftEntry<V> = { value: V; original: V }
export type DraftEdits<V> = {
  entries: ReadonlyMap<string, DraftEntry<V>>
  count: number
  get: (key: string) => DraftEntry<V> | undefined
  // A value equal to the original is no change: the draft is dropped.
  set: (key: string, value: V, original: V) => void
  revert: (key: string) => void
  revertAll: () => void
  remove: (keys: Iterable<string>) => void
}

export function useDraftEdits<V>(equal: (left: V, right: V) => boolean = (left, right) => left === right): DraftEdits<V> {
  const [entries, setEntries] = useState<ReadonlyMap<string, DraftEntry<V>>>(() => new Map())
  const set = useCallback((key: string, value: V, original: V) => {
    setEntries(current => {
      const next = new Map(current)
      if (equal(value, original)) next.delete(key); else next.set(key, { value, original: current.get(key)?.original ?? original })
      return next
    })
  }, [equal])
  const remove = useCallback((keys: Iterable<string>) => setEntries(current => { const next = new Map(current); for (const key of keys) next.delete(key); return next }), [])
  const revert = useCallback((key: string) => remove([key]), [remove])
  const revertAll = useCallback(() => setEntries(new Map()), [])
  const get = useCallback((key: string) => entries.get(key), [entries])
  return useMemo(() => ({ entries, count: entries.size, get, set, revert, revertAll, remove }), [entries, get, set, revert, revertAll, remove])
}

// The browser's own "leave the page?" question while a draft exists (a closed tab, a reload, a link out of the app).
export function useUnsavedChangesGuard(active: boolean) {
  useEffect(() => {
    if (!active) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [active])
}

// The save bar of every in-place editor. In a window it is the window's own foot (variant "foot", the one place to save): the state
// on the left ("3 Değişiklik · Kaydedilmedi", "Değişiklik Yok"), "Vazgeç" (drops the drafts; "Kapat" when there are none) and the
// primary "Kaydet" on the right. Kaydet first shows one confirmation (what will change, where it goes); leaving with a draft asks
// in the same place. On a page (variant "strip") it is a sticky strip with the same words.
export function DraftSaveBar({ variant = 'strip', count, busy = false, invalid = '', lead, summary, notice, leaving = false, onSave, onDiscard, onStay, onLeave, onClose }: {
  variant?: 'strip' | 'foot'
  // A short phrase before the status on a phone ("5 Seçili"); it is not drawn on a wide screen.
  lead?: string
  count: number
  busy?: boolean
  // Why Kaydet is off (a draft that does not pass its checks); empty when it can be saved.
  invalid?: string
  summary: ReactNode
  notice?: ReactNode
  leaving?: boolean
  onSave: () => void
  onDiscard: () => void
  onStay?: () => void
  onLeave?: () => void
  // "Kapat" while there is nothing to discard.
  onClose?: () => void
}) {
  const [confirming, setConfirming] = useState(false)
  const dock = useDockHost()
  useEffect(() => { if (!count) setConfirming(false) }, [count])
  const foot = variant === 'foot'
  if (!count && !foot) return null
  // A strip on a phone is not drawn in the page: Vazgeç / Kaydet go to the dock's task bar and the state to the strip above it.
  const wrap = (text: ReactNode, actions: ReactNode, tone = '') => !foot && dock?.phone
    ? <DockActions label="Kaydedilmemiş değişiklikler" info={{ start: text }}>{actions}</DockActions>
    : foot
    ? <><span className="rr-note rv-draftbar-text" role="status">{text}</span>{actions}</>
    : <div className={`rv-draftbar${tone}`} role="region" aria-label="Kaydedilmemiş değişiklikler"><span className="rv-draftbar-text">{text}</span><span className="rv-draftbar-actions">{actions}</span></div>
  if (leaving && count) {
    return wrap(<><b>Kaydedilmemiş değişiklikler var.</b> Kapatırsanız {count} değişiklik silinir.</>, <><button type="button" className="rr-btn" onClick={onStay}>Düzenlemeye Dön</button><button type="button" className="rr-btn rr-danger" onClick={onLeave}>Değişiklikleri Sil ve Kapat</button></>, ' rv-draftbar-leave')
  }
  if (confirming && count) {
    return wrap(<>{summary}{notice ? <small>{notice}</small> : null}</>, <><button type="button" className="rr-btn" onClick={() => setConfirming(false)} disabled={busy}>Geri Dön</button><button type="button" className="rr-btn rr-primary rv-draftbar-save" disabled={busy} onClick={() => { setConfirming(false); onSave() }}>Onayla ve Kaydet</button></>, ' rv-draftbar-confirm')
  }
  return wrap(
    <>{lead ? <span className="rv-draftbar-lead">{lead} · </span> : null}{count ? <><b>{count} Değişiklik</b> · {invalid || 'Kaydedilmedi'}</> : <>Değişiklik Yok</>}</>,
    <><button type="button" className="rr-btn" onClick={count ? onDiscard : onClose} disabled={busy}>{count ? 'Vazgeç' : 'Kapat'}</button><button type="button" className="rr-btn rr-primary rv-draftbar-save" disabled={busy || !count || Boolean(invalid)} title={invalid || undefined} onClick={() => setConfirming(true)}>{busy ? 'Kaydediliyor…' : 'Kaydet'}</button></>
  )
}
