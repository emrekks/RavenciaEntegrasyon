import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
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

// "N Değişiklik · Vazgeç · Kaydet", shown only while a draft exists and sticking to the bottom of its window. Kaydet
// first shows one confirmation line (what will change, where it goes); leaving with a draft asks in the same bar.
export function DraftSaveBar({ count, busy = false, invalid = '', summary, notice, leaving = false, onSave, onDiscard, onStay, onLeave }: {
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
}) {
  const [confirming, setConfirming] = useState(false)
  useEffect(() => { if (!count) setConfirming(false) }, [count])
  if (!count) return null
  if (leaving) {
    return <div className="rv-draftbar rv-draftbar-leave" role="alertdialog" aria-label="Kaydedilmemiş değişiklikler var" aria-describedby="rv-draftbar-leave-text">
      <span id="rv-draftbar-leave-text" className="rv-draftbar-text"><b>Kaydedilmemiş değişiklikler var.</b> Kapatırsanız {count} değişiklik silinir.</span>
      <span className="rv-draftbar-actions"><button type="button" className="rr-btn" onClick={onStay}>Düzenlemeye Dön</button><button type="button" className="rr-btn rr-danger" onClick={onLeave}>Değişiklikleri Sil ve Kapat</button></span>
    </div>
  }
  if (confirming) {
    return <div className="rv-draftbar rv-draftbar-confirm" role="alertdialog" aria-label="Değişiklikleri kaydet" aria-describedby="rv-draftbar-confirm-text">
      <span id="rv-draftbar-confirm-text" className="rv-draftbar-text">{summary}{notice ? <small>{notice}</small> : null}</span>
      <span className="rv-draftbar-actions"><button type="button" className="rr-btn" onClick={() => setConfirming(false)} disabled={busy}>Geri Dön</button><button type="button" className="rr-btn rr-primary" disabled={busy} onClick={() => { setConfirming(false); onSave() }}>Onayla ve Kaydet</button></span>
    </div>
  }
  return <div className="rv-draftbar" role="region" aria-label="Kaydedilmemiş değişiklikler">
    <span className="rv-draftbar-text"><b>{count} Değişiklik</b><small>{invalid || 'Kaydedilmedi'}</small></span>
    <span className="rv-draftbar-actions"><button type="button" className="rr-btn" onClick={onDiscard} disabled={busy}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={busy || Boolean(invalid)} title={invalid || undefined} onClick={() => setConfirming(true)}>{busy ? 'Kaydediliyor…' : 'Kaydet'}</button></span>
  </div>
}
