import type { FieldState } from '../../shared'

// A mapped field's place in the category window's list: its glyph, the line under its name and the ring's share.
// `savedLocalIds` is null while the field's value mappings are not known (the progress read failed or has not
// answered): such a field is "unknown", never "done" (scan 25 Nov 2026, D-4: it showed a green tick and counted as
// ready in "Zorunlu Alanlar").
export function attributeFieldProgress({ mapped, needsValues, activeValueIds, savedLocalIds }: { mapped: boolean; needsValues: boolean; activeValueIds: readonly string[]; savedLocalIds: readonly string[] | null | undefined }): { state: FieldState; meta: string; ratio?: number } {
  if (!mapped) return { state: 'none', meta: 'Eşlenmedi' }
  if (!needsValues || !activeValueIds.length) return { state: 'done', meta: 'Eşlendi' }
  if (savedLocalIds == null) return { state: 'unknown', meta: 'Değerler bilinmiyor' }
  const active = new Set(activeValueIds)
  const done = new Set(savedLocalIds.filter(localId => active.has(localId))).size
  const total = activeValueIds.length
  return done < total ? { state: 'partial', meta: `${done}/${total} değer`, ratio: done / total } : { state: 'done', meta: `${total} değer`, ratio: 1 }
}
