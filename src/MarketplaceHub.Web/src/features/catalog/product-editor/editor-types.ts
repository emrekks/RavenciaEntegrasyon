// The product editor's shared types (ProductEditorPages and its pieces).
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { type VariantCodeChange } from '../variant-generation'
import { type PlatformUpdateField } from './platform-update-fields'
import { type VariantMediaAssignmentDrafts } from '../variant-media-assignments'
import { Product, ProductAttributeAssignment, VariantPlatformStatus } from '../shared'
import type { QueryClient } from '@tanstack/react-query'
import type { Location, NavigateFunction } from 'react-router'

// What the editor page hands its hooks first: its props and the router and query client it reads.
export type EditorProps = {
  editProductId: string | undefined
  publishOnly: boolean
  client: QueryClient
  location: Location
  navigate: NavigateFunction
  routeState: { duplicateProductId?: unknown; wizardStep?: unknown } | null
  duplicateProductId: string | undefined
}

// quick: sent by Hızlı Güncelle (else by the settings window's Gönder); the store prices name it as their reason.
export type ProductUpdateRequest = { connectionIds: string[]; fields: PlatformUpdateField[]; quick?: boolean }
export type PublicationStatus = ApiAnswer<'getProductsByIdPublicationStatusByConnectionId'>
export type PublicationJobDetail = ApiAnswer<'getJobsById'>
export type ProductFamilyMediaItem = NonNullable<Product['familyMediaItems']>[number]
export type MediaRemovalConfirmation = { kind: 'product'; url: string } | { kind: 'all'; ownCount: number; familyItems: ProductFamilyMediaItem[]; keptCount: number }
export type PendingProductMediaFile = { id: string; file: File }
export type VariantMediaGroup = { id: string; name: string; values: Array<{ id: string; value: string }>; attributeId?: string }
export type VariantMediaModalState = { mode: 'variant'; rowKey: string; draftRefs: string[] } | { mode: 'bulk'; draftsBySelection: VariantMediaAssignmentDrafts; groupId: string; valueId: string }
export type VariantFilterSelections = Record<string, string[]>
export type VariantDraft = {
  key: string
  optionSignature: string
  options: Record<string, string>
  attributeValueIds: Record<string, string>
  sku: string
  barcode: string
  stock: number
  salePrice: number
  listPrice: number
  // Empty (null) until a cost is entered: an empty cost is "not known", never 0.
  costPrice: number | null
  // The cost this row was loaded with (edit page only): the cost is saved only when the row differs from it, so an
  // untouched row never writes over a cost set elsewhere meanwhile (ürün içe aktarma).
  loadedCostPrice?: number | null
  mediaRefs: string[]
  // The on-hand stock this row was loaded with (edit page only): stock is saved only when the row differs from it,
  // as an absolute value against it.
  loadedStock?: number
  // The variant's version this row was loaded with (edit page only). The server refuses a save that would change the
  // row over a variant changed meanwhile elsewhere (another colour card of the family, a quick price edit, an import).
  loadedVersion?: number
  persistedAttributes?: ProductAttributeAssignment[]
  platformStatuses?: VariantPlatformStatus[]
}
export type SavedListingProfile = ApiAnswer<'getProductsByIdListingProfilesByConnectionId'>
export type ProductAttributePayload = ProductAttributeAssignment
// A column-menu code change waiting for the user's confirmation: model-code codes for every row or only the empty
// ones, or emptying the column. Nothing is written to the rows before the user confirms, nor saved before Kaydet.
export type PendingVariantCodeChange = VariantCodeChange & { rowCount: number; skipped?: number }
