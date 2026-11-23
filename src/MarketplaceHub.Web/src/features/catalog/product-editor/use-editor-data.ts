// What the editor reads from the server (the product, its copy source, categories, brands, connections, category
// requirements) and what follows from it and the form at render time.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useEffect, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../../shared/api'
import { categoryRequirementsStaleTimeMs } from '../category-requirements-query'
import { publicationRequirementSets, readCategoryRequirements } from './publication-readiness'
import { usePttAvmManualWrites } from '../use-pttavm-manual-writes'
import { cardPrices } from './card-prices'
import { type RequiredEditorField } from './required-fields'
import { validateEditor } from './validate-editor'
import { pinSizeChartLast } from './size-chart-order'
import { editorAwaitingSeed } from '../product-editor-state'
import { isProductUpdateConnection } from '../platform-update-targets'
import { Brand, Category, MarketplaceConnection, Product } from '../shared'
import { connectionQueries, queryKeys } from '../../../shared/api/query-keys'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import { editorCount, parseEditorNumber } from './editor-number'
import { readMoneyField } from './seeded-fields'

export function useEditorData(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult) {
  const { editProductId, duplicateProductId, form, setVariantAttributeIds, variantRows, automaticBarcodeGeneration, seededProductId, sizeChartPicking, chartKey, requiredErrors } = c
  // Always fresh on open and on returning to the tab: the products list's quick edit (or another tab) may have changed
  // its prices, channel prices or stock. Unsaved edits are kept; the stale banner offers the newer data instead.
  const productToEdit = useQuery({ queryKey: queryKeys.product(editProductId), queryFn: ({ signal }) => hubApi<Product>(`/products/${editProductId}`, { signal }), enabled: !!editProductId, staleTime: 0, refetchOnWindowFocus: true })
  const productToCopy = useQuery({ queryKey: queryKeys.product(duplicateProductId), queryFn: ({ signal }) => hubApi<Product>(`/products/${duplicateProductId}`, { signal }), enabled: !editProductId && Boolean(duplicateProductId) })
  const seedProduct = editProductId ? productToEdit.data : productToCopy.data
  const categories = useQuery({ queryKey: queryKeys.categories('new-product'), queryFn: ({ signal }) => loadAllPages<Category>('/catalog/categories', { signal }) })
  const brands = useQuery({ queryKey: queryKeys.brands('new-product'), queryFn: ({ signal }) => loadAllPages<Brand>('/catalog/brands', { signal }) })
  const connections = useQuery(connectionQueries.list<MarketplaceConnection>())
  const pttAvmManualWrites = usePttAvmManualWrites((connections.data?.items ?? []).filter(isProductUpdateConnection))
  // An edit or a copy keeps its fields masked until the product and the category and brand lists that name its values
  // are in: the empty defaults and the single-product SKU, barcode and price fields must not show before the variant
  // rows turn them into the varyantlı state.
  const editorHydrating = editorAwaitingSeed({ seeding: Boolean(editProductId || duplicateProductId), productPending: editProductId ? productToEdit.isPending : productToCopy.isPending, listsPending: categories.isPending || brands.isPending })
  const requirements = useQuery({ queryKey: queryKeys.categoryRequirements(form.categoryId), queryFn: async ({ signal }) => readCategoryRequirements(await hubApi<ApiAnswer<'getCatalogCategoriesByIdAttributeRequirements'>>(`/catalog/categories/${form.categoryId}/attribute-requirements`, { signal })), enabled: !!form.categoryId, retry: false, staleTime: categoryRequirementsStaleTimeMs })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  // The card's Liste/Satış (decimal comma read): a new variant row's first prices (buildVariantGenerationDefaults).
  const cardPriceDefaults = cardPrices(form.listPrice, form.salePrice); const fallbackListPrice = cardPriceDefaults.listPrice; const fallbackSalePrice = cardPriceDefaults.salePrice; const fallbackCostPrice = form.costPrice.trim() === '' ? null : readMoneyField('costPrice', form.costPrice); const initialStock = editorCount(form.initialStock || 0)
  // Required fields left empty (required-fields.ts): a save stops on them; a saved card that opens with one is framed red.
  // KDV Oranı is checked beside them (vatRateIssue): required on every card but a draft (validate-editor.ts).
  const { missingRequired: missingRequiredFields, vatRateIssue } = validateEditor({ form, automaticBarcodes: automaticBarcodeGeneration, hasVariants: variantRows.length > 1 }, { kind: 'live' })
  // A stopped save's message under a required field stays only while the field is still empty.
  const requiredError = (field: RequiredEditorField) => missingRequiredFields.includes(field) ? requiredErrors[field] : undefined
  // An opened card frames its empty required fields red only once its data is in the form: before that every field is
  // empty and the whole form flashed red while the product loaded (owner, 11 Nov 2026).
  const editorSeeded = Boolean(editProductId) && seededProductId === editProductId
  const requiredFieldInvalid = (field: RequiredEditorField) => requiredError(field) || (editorSeeded && missingRequiredFields.includes(field)) ? true : undefined
  const variantStockTotal = variantRows.reduce((total, row) => total + parseEditorNumber(row.stock || 0), 0)
  const desi = useMemo(() => { const width = parseEditorNumber(form.width); const length = parseEditorNumber(form.length); const height = parseEditorNumber(form.height); return width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0 }, [form.width, form.length, form.height])
  // The size chart stays the card's last picture whatever order the draft holds (size-chart-order.ts).
  const mediaUrls = useMemo(() => pinSizeChartLast(form.mediaUrls.split(/\r?\n|[;|]/).map(item => item.trim()).filter(Boolean), chartKey), [form.mediaUrls, chartKey])
  // Picking is asked for on a saved card; it stays on only while the strip shows a saved picture to mark (pickingSizeChart).
  const sizeChartPickRequested = sizeChartPicking && Boolean(editProductId)

  const allRequirements = useMemo(() => (requirements.data ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder), [requirements.data])
  // A category requirement is usable in the product editor when its mapped
  // local attribute has active values or the marketplace accepts a custom
  // value. The latter keeps required text fields (for example a free-form
  // color field) available for entry from the product form.
  const requirementSets = useMemo(() => publicationRequirementSets(allRequirements), [allRequirements])
  const { mappedRequirements, colorOptionRequirement, webColorRequirement, optionRequirements } = requirementSets
  // Web Color is backed by the local Renk option. Prefer the explicit
  // category mapping marker, but keep the editor usable when a category has
  // Renk configured before its Web Color mapping is saved.
  const webColorValues = webColorRequirement?.attribute.values ?? colorOptionRequirement?.attribute.values ?? []
  useEffect(() => {
    const optionIds = optionRequirements.map(item => item.attributeId)
    setVariantAttributeIds(current => current.filter(id => optionIds.includes(id)))
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [optionRequirements])
  const visibleOptionRequirements = optionRequirements

  return { productToEdit, productToCopy, seedProduct, categories, brands, connections, pttAvmManualWrites, editorHydrating, requirements, leafCategories, activeBrands, cardPriceDefaults, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, missingRequiredFields, vatRateIssue, requiredError, editorSeeded, requiredFieldInvalid, variantStockTotal, desi, mediaUrls, sizeChartPickRequested, allRequirements, requirementSets, mappedRequirements, colorOptionRequirement, webColorRequirement, optionRequirements, webColorValues, visibleOptionRequirements }
}
export type EditorDataResult = ReturnType<typeof useEditorData>
