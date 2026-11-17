// The "Platformlarda Güncelle" summary (POST /products/update-preview, read only): per store, which fields the update
// will change on the marketplace, compared with the marketplace's last known value. A field whose marketplace value the
// panel does not know is "Pazaryerindeki Değer Bilinmiyor", never "no change" (owner, 17 Nov 2026).
import type { ProductUpdatePreviewConnection, ProductUpdatePreviewField } from '../../../shared/api/generated/api-types'
import { platformUpdateFieldLabel, type PlatformUpdateField } from './platform-update-fields'

// Above it the server refuses the summary (ProductUpdatePreviewPolicy.MaxProducts); the send is not affected.
export const platformUpdatePreviewMaxProducts = 50

export const unknownMarketplaceValue = 'Pazaryerindeki Değer Bilinmiyor'
export const sameAsMarketplace = 'Pazaryerindeki Değerlerle Aynı'

export type PreviewChangeLine = { model: string; label: string; before: string; after: string; lengths?: string }
export type PreviewFieldRow = { field: string; label: string; state: 'CHANGES' | 'SAME' | 'UNKNOWN'; text: string; changes: PreviewChangeLine[] }
export type PreviewStore = { connectionId: string; name: string; rows: PreviewFieldRow[]; unknownFields: string; allSame: boolean; nothing: boolean; problems: { label: string; message: string }[] }

const format = (value: number) => value.toLocaleString('tr-TR')
const unitWord = (unit: string) => unit === 'VARIANT' ? 'Beden' : 'Renk'
export const fieldName = (field: string) => platformUpdateFieldLabel(field as PlatformUpdateField)

// One row per field across the store's models: how many colours / sizes change, then how many are not known.
function fieldRow(field: string, entries: { model: string; value: ProductUpdatePreviewField }[]): PreviewFieldRow {
  const unit = unitWord(entries[0]?.value.unit ?? 'COLOR')
  const changed = entries.reduce((sum, entry) => sum + entry.value.changed, 0)
  const unknown = entries.reduce((sum, entry) => sum + entry.value.unknown, 0)
  const same = entries.reduce((sum, entry) => sum + entry.value.same, 0)
  const state = changed > 0 ? 'CHANGES' : unknown > 0 ? 'UNKNOWN' : 'SAME'
  const text = state === 'CHANGES'
    ? `${format(changed)} ${unit} Değişecek${unknown ? ` · ${format(unknown)} ${unit} Bilinmiyor` : ''}`
    : state === 'UNKNOWN'
      ? `${unknownMarketplaceValue} · ${format(unknown + same)} ${unit}`
      : sameAsMarketplace
  const changes = entries.flatMap(entry => entry.value.changes.map(change => ({
    model: entry.model,
    label: change.label,
    before: change.before,
    after: change.after,
    lengths: change.beforeLength != null && change.afterLength != null ? `${format(change.beforeLength)} karakter → ${format(change.afterLength)} karakter` : undefined
  })))
  return { field, label: fieldName(field), state, text, changes }
}

export function previewStores(connections: ProductUpdatePreviewConnection[]): PreviewStore[] {
  return connections.map(connection => {
    const fields = [...new Set(connection.models.flatMap(model => model.fields.map(field => field.field)))]
    const rows = fields.map(field => fieldRow(field, connection.models.flatMap(model => model.fields.filter(item => item.field === field).map(value => ({ model: model.label, value })))))
    const unknownFields = connection.unknownFields.map(fieldName).join(', ')
    return {
      connectionId: connection.connectionId,
      name: connection.displayName,
      rows,
      unknownFields,
      allSame: rows.length > 0 && rows.every(row => row.state === 'SAME') && !unknownFields,
      nothing: rows.length === 0 && !unknownFields && connection.problems.length === 0,
      problems: connection.problems.map(problem => ({ label: problem.label, message: problem.message }))
    }
  })
}
