// A category cell in "Kayıtlı Kategori Eşleştirmeleri": the saved mapping holds only ids, the names come from the
// category lists. While a list loads the cell waits (it never flashes the raw id first); a name that is still missing
// once the list is in is shown as a marked fallback with its id, so it never reads as a real category name.
export type SavedCategoryLabel = { state: 'loading' } | { state: 'resolved'; text: string } | { state: 'fallback'; text: string }

export function savedCategoryLabel(path: string | null | undefined, id: string, loading: boolean): SavedCategoryLabel {
  const name = path?.trim()
  if (name) return { state: 'resolved', text: name }
  if (loading) return { state: 'loading' }
  return { state: 'fallback', text: id ? `Kategori #${id}` : 'Kategori bilinmiyor' }
}
