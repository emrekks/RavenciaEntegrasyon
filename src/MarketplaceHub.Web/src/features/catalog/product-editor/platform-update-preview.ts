// The "Platformlarda Güncelle" summary (POST /products/update-preview, read only): per store, which fields the update
// will change on the marketplace, compared with the marketplace's last known value. A field whose marketplace value the
// panel does not know is "Pazaryerindeki Değer Bilinmiyor", never "no change" (owner, 17 Nov 2026).
import type { ProductUpdatePreviewConnection, ProductUpdatePreviewField } from '../../../shared/api/generated/api-types'
import { formatDate } from '../../../shared/format-date'
import { platformUpdateFieldLabel, type PlatformUpdateField } from './platform-update-fields'

// Above it the server refuses the summary (ProductUpdatePreviewPolicy.MaxProducts); the send is not affected.
export const platformUpdatePreviewMaxProducts = 50

export const unknownMarketplaceValue = 'Pazaryerindeki Değer Bilinmiyor'
export const sameAsMarketplace = 'Pazaryerindeki Değerlerle Aynı'
// Where the compared marketplace values come from (catalog.marketplace_known_listing_values): a product pull read them,
// or the marketplace accepted an update carrying them.
export const readSourceLabel = 'Pazaryerinde Okunan'
export const sentSourceLabel = 'Son Gönderilen'
// Shown above the summary while the editor holds edits not saved yet: Gönder saves them first, the summary compares the
// saved card.
export const unsavedEditsNote = 'Kaydedilmemiş değişiklikler bu özette yok; Gönder önce kaydeder, sonra gönderir.'

/** The cards Gönder reaches on one store: the summary asks for exactly these. */
export type PreviewTarget = { connectionId: string; productIds: string[] }

export type PreviewChangeLine = { model: string; label: string; before: string; after: string; lengths?: string }
export type PreviewFieldRow = { field: string; label: string; state: 'CHANGES' | 'SAME' | 'UNKNOWN' | 'SENT_SAME'; text: string; changes: PreviewChangeLine[] }

// A value equal to what the panel last sent: whether the marketplace applied it is not known, so it is never told as
// "no change" (independent review E1b, 18 Nov 2026).
export const sameAsLastSent = 'Son Gönderilenle Aynı'
export const appliedUnknown = 'Pazaryerinde Uygulandığı Bilinmiyor'
export type PreviewStore = { connectionId: string; name: string; known: string; rows: PreviewFieldRow[]; unknownFields: string; allSame: boolean; nothing: boolean; problems: { label: string; message: string }[] }

const format = (value: number) => value.toLocaleString('tr-TR')
const unitWord = (unit: string) => unit === 'VARIANT' ? 'Beden' : 'Renk'
export const fieldName = (field: string) => platformUpdateFieldLabel(field as PlatformUpdateField)

// One row per field across the store's models: how many colours / sizes change, then how many are not known.
function fieldRow(field: string, entries: { model: string; value: ProductUpdatePreviewField }[], sentAt?: string | null): PreviewFieldRow {
  const unit = unitWord(entries[0]?.value.unit ?? 'COLOR')
  const changed = entries.reduce((sum, entry) => sum + entry.value.changed, 0)
  const unknown = entries.reduce((sum, entry) => sum + entry.value.unknown, 0)
  const same = entries.reduce((sum, entry) => sum + entry.value.same, 0)
  const sentSame = entries.reduce((sum, entry) => sum + (entry.value.sentSame ?? 0), 0)
  const state = changed > 0 ? 'CHANGES' : unknown > 0 ? 'UNKNOWN' : sentSame > 0 ? 'SENT_SAME' : 'SAME'
  const sentPart = sentSame ? ` · ${format(sentSame)} ${unit} ${sameAsLastSent}` : ''
  const sentDate = sentAt ? ` · ${formatDate(sentAt)}` : ''
  const text = state === 'CHANGES'
    ? `${format(changed)} ${unit} Değişecek${unknown ? ` · ${format(unknown)} ${unit} Bilinmiyor` : ''}${sentPart}`
    : state === 'UNKNOWN'
      ? `${unknownMarketplaceValue} · ${format(unknown + same + sentSame)} ${unit}`
      : state === 'SENT_SAME'
        ? `${sameAsLastSent}${sentDate} · ${appliedUnknown}`
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

// "Pazaryerinde Okunan · 17.11.2026 · Son Gönderilen · 18.11.2026": the dates behind the compared values; empty when no
// value was known (every field is then "Bilinmiyor").
export function knownValuesLine(connection: Pick<ProductUpdatePreviewConnection, 'knownValuesReadAt' | 'knownValuesSentAt'>) {
  return [
    connection.knownValuesReadAt ? `${readSourceLabel} · ${formatDate(connection.knownValuesReadAt)}` : '',
    connection.knownValuesSentAt ? `${sentSourceLabel} · ${formatDate(connection.knownValuesSentAt)}` : ''
  ].filter(Boolean).join(' · ')
}

/** The distinct cards across the stores (the server's cap counts these). */
export function previewProductCount(targets: readonly PreviewTarget[]) {
  return new Set(targets.flatMap(target => target.productIds)).size
}

export function previewStores(connections: ProductUpdatePreviewConnection[]): PreviewStore[] {
  return connections.map(connection => {
    const fields = [...new Set(connection.models.flatMap(model => model.fields.map(field => field.field)))]
    const rows = fields.map(field => fieldRow(field, connection.models.flatMap(model => model.fields.filter(item => item.field === field).map(value => ({ model: model.label, value }))), connection.knownValuesSentAt))
    const unknownFields = connection.unknownFields.map(fieldName).join(', ')
    return {
      connectionId: connection.connectionId,
      name: connection.displayName,
      known: knownValuesLine(connection),
      rows,
      unknownFields,
      allSame: rows.length > 0 && rows.every(row => row.state === 'SAME') && !unknownFields,
      nothing: rows.length === 0 && !unknownFields && connection.problems.length === 0,
      problems: connection.problems.map(problem => ({ label: problem.label, message: problem.message }))
    }
  })
}
