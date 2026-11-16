// The product editor's form data in one state object with its baseline (WEB-SAYFALAR-10). The baseline is the form as
// it stood right after it was filled (a seed, the editor's own save, a new product's open); the form is dirty when a
// field differs from it. Typed actions set a field (the setters the hooks use), ask for a new baseline, or move the
// baseline for something the editor saved on its own (a seeded Web Color, a picture write).
import type { SetStateAction } from 'react'
import { EMPTY_EDITOR_FORM, type EditorForm } from './editor-form'
import type { ChannelPricingDraft } from './marketplace-channel-pricing'
import type { EditorWebColor } from '../product-editor-state'
import type { PendingProductMediaFile, ProductFamilyMediaItem, VariantDraft } from './editor-types'

// What the user edits on the card: every field here counts for the unsaved-changes check.
export type EditorFields = {
  form: EditorForm
  variantRows: VariantDraft[]
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  familyMediaOrder: string[]
  // New files waiting for Kaydet: their number counts.
  mediaFiles: PendingProductMediaFile[]
  removedVariantIds: string[]
  // Other colours' pictures "Tümünü Temizle" took off, removed on Kaydet.
  pendingFamilyMediaRemovals: ProductFamilyMediaItem[]
  // "Pazaryerlerine Özel Fiyat" (per marketplace) and a new product's variant price table (per variant and marketplace).
  channelPricing: Record<string, ChannelPricingDraft>
  variantChannelPricing: Record<string, ChannelPricingDraft>
  // Web Color: automatic (each row's own Renk) or one manual value.
  webColorAutoEnabled: boolean
  manualWebColorValueId: string
  // The picture marked as the size chart (Beden Tablosu).
  sizeChartUrl: string | null
}

export const initialEditorFields = (): EditorFields => ({
  form: EMPTY_EDITOR_FORM,
  variantRows: [],
  attributeSelections: {},
  attributeTextValues: {},
  familyMediaOrder: [],
  mediaFiles: [],
  removedVariantIds: [],
  pendingFamilyMediaRemovals: [],
  channelPricing: {},
  variantChannelPricing: {},
  webColorAutoEnabled: true,
  manualWebColorValueId: '',
  sizeChartUrl: null
})

// Each field as the unsaved-changes check compares it. Server-side state shown in the rows (platform prices, versions)
// is left out: it is saved on its own and does not make the form "unsaved". The order the marketplaces were switched on
// in does not matter.
export type DirtyFields = {
  form: Record<string, string>
  rows: unknown[][]
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  familyMediaOrder: string[]
  pendingMediaFileCount: number
  removedVariantIds: string[]
  pendingFamilyMediaRemovalUrls: string[]
  channelPricing: string[][]
  variantChannelPricing: string[][]
  webColor: [boolean, string]
  sizeChartUrl: string | null
}

const sortedPricing = (pricing: Record<string, ChannelPricingDraft>) => Object.entries(pricing).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0).map(([key, value]) => [key, String(value.listPrice), String(value.salePrice)])

// The index of a row's pictures in its compared tuple (rowMediaRefs below).
const ROW_MEDIA_INDEX = 10

export function editorDirtyFields(fields: EditorFields): DirtyFields {
  return {
    form: fields.form,
    rows: fields.variantRows.map(row => [row.key, row.optionSignature, row.options, row.attributeValueIds, row.sku, row.barcode, row.stock, row.salePrice, row.listPrice, row.costPrice, row.mediaRefs]),
    attributeSelections: fields.attributeSelections,
    attributeTextValues: fields.attributeTextValues,
    familyMediaOrder: fields.familyMediaOrder,
    pendingMediaFileCount: fields.mediaFiles.length,
    removedVariantIds: fields.removedVariantIds,
    pendingFamilyMediaRemovalUrls: fields.pendingFamilyMediaRemovals.map(item => item.url),
    channelPricing: sortedPricing(fields.channelPricing),
    variantChannelPricing: sortedPricing(fields.variantChannelPricing),
    webColor: [fields.webColorAutoEnabled, fields.manualWebColorValueId],
    sizeChartUrl: fields.sizeChartUrl
  }
}

const sameValue = (left: unknown, right: unknown) => left === right || JSON.stringify(left) === JSON.stringify(right)

// The fields that differ from the baseline, by name.
export function changedEditorFields(current: DirtyFields, baseline: DirtyFields): Array<keyof DirtyFields> {
  return (Object.keys(current) as Array<keyof DirtyFields>).filter(field => !sameValue(current[field], baseline[field]))
}

// The Web Color an edit opens with is filled once the category's requirements are in, which can be after the baseline
// was taken: the baseline then takes that Web Color as its own, so the seeded value is not an unsaved change.
export function baselineWithWebColor(baseline: DirtyFields, webColor: EditorWebColor): DirtyFields {
  return { ...baseline, webColor: [webColor.autoEnabled, webColor.manualValueId] }
}

// The editor's own picture write (a saved picture removed, picture assignments saved on their own) over a form with
// other unsaved edits: the baseline moves for the pictures that write saved and nothing else, so those edits stay
// unsaved and taking them back leaves the form clean (W1, scan 2026-11-15).
export function baselineWithSavedMedia(baseline: DirtyFields, saved: {
  mediaUrls?: (value: string) => string
  rowMediaRefs?: (rowKey: string, references: string[]) => string[]
  familyMediaOrder?: (order: string[]) => string[]
  sizeChartUrl?: (url: string | null) => string | null
}): DirtyFields {
  const next = { ...baseline }
  if (saved.mediaUrls) next.form = { ...next.form, mediaUrls: saved.mediaUrls(next.form.mediaUrls ?? '') }
  const rowMediaRefs = saved.rowMediaRefs
  if (rowMediaRefs) next.rows = next.rows.map(row => row.map((value, index) => index === ROW_MEDIA_INDEX ? rowMediaRefs(String(row[0]), value as string[]) : value))
  if (saved.familyMediaOrder) next.familyMediaOrder = saved.familyMediaOrder(next.familyMediaOrder)
  if (saved.sizeChartUrl) next.sizeChartUrl = saved.sizeChartUrl(next.sizeChartUrl)
  return next
}

export type EditorState = {
  fields: EditorFields
  // The form right after it was filled; null until the first one (nothing counts as unsaved before it).
  baseline: DirtyFields | null
  // Raised to take a new baseline from the form as the next render shows it (after the updates queued with it).
  baselineRequest: number
  // Raised when a seeded Web Color should become the baseline's own, after the render that holds it.
  webColorBaselineRequest: number
}

export type SetFieldAction = { [K in keyof EditorFields]: { type: 'setField'; field: K; update: SetStateAction<EditorFields[K]> } }[keyof EditorFields]

export type EditorAction =
  | SetFieldAction
  | { type: 'requestBaseline' }
  | { type: 'takeBaseline'; baseline: DirtyFields }
  | { type: 'adjustBaseline'; adjust: (baseline: DirtyFields) => DirtyFields }
  | { type: 'requestWebColorBaseline' }

export const initialEditorState = (): EditorState => ({ fields: initialEditorFields(), baseline: null, baselineRequest: 0, webColorBaselineRequest: 0 })

export function editorReducer(state: EditorState, action: EditorAction): EditorState {
  switch (action.type) {
    case 'setField': {
      const current = state.fields[action.field]
      const next = typeof action.update === 'function' ? (action.update as (value: typeof current) => typeof current)(current) : action.update
      // A field set to the value it holds changes nothing (as a useState setter bails out).
      return Object.is(next, current) ? state : { ...state, fields: { ...state.fields, [action.field]: next } }
    }
    case 'requestBaseline': return { ...state, baselineRequest: state.baselineRequest + 1 }
    case 'takeBaseline': return { ...state, baseline: action.baseline }
    // No baseline yet: nothing to move.
    case 'adjustBaseline': return state.baseline == null ? state : { ...state, baseline: action.adjust(state.baseline) }
    case 'requestWebColorBaseline': return { ...state, webColorBaselineRequest: state.webColorBaselineRequest + 1 }
  }
}

// Whether the form holds edits a reseed would destroy: a field differs from the baseline taken after it was filled.
export function editorStateDirty(state: Pick<EditorState, 'fields' | 'baseline'>) {
  return state.baseline !== null && changedEditorFields(editorDirtyFields(state.fields), state.baseline).length > 0
}
