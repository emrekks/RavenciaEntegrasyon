// A filter that applies at once (a popup choice, the date range) changes the applied filters and the form alike, but
// starts from the applied ones: search text typed without Enter or Filtrele stays in the box, unapplied.
export function applyFilterPatch<T extends object>(applied: T, form: T, patch: Partial<T>): { applied: T; form: T } {
  return { applied: { ...applied, ...patch }, form: { ...form, ...patch } }
}
