// A marketplace field filled from the product itself instead of a panel attribute: Hepsiburada's Kombin ID takes each
// variant's own model code. Saved per connection as the field's default (sourceField), so it holds in every category.
export type ProductFieldSource = 'MODEL_CODE' | 'SKU' | 'BARCODE' | 'TITLE'

export const productFieldSources: ReadonlyArray<{ value: ProductFieldSource; label: string }> = [
  { value: 'MODEL_CODE', label: 'Model Kodu' },
  { value: 'SKU', label: 'Stok Kodu' },
  { value: 'BARCODE', label: 'Barkod' },
  { value: 'TITLE', label: 'Ürün Adı' }
]

// Only these send free text for a field; Pazarama sends listed value ids only.
const productFieldPlatforms = new Set(['HEPSIBURADA', 'TRENDYOL', 'N11'])

// Whether the field list shows the Ürün Bilgileri group at all: on every field of a marketplace that sends free text.
export function showsProductFields(platformCode: string) {
  return productFieldPlatforms.has(platformCode)
}

// Whether a product field can be chosen for this field: only where the marketplace takes free text; a list-only field
// (Renk, Materyal) would refuse a model code it does not list.
export function offersProductFields(platformCode: string, allowsCustomValue: boolean | null | undefined) {
  return productFieldPlatforms.has(platformCode) && allowsCustomValue === true
}

export function productFieldLabel(source: string) {
  return productFieldSources.find(item => item.value === source)?.label ?? source
}

// The select's option value for a product field, kept apart from panel attribute ids (GUIDs).
const optionPrefix = 'field:'
export function productFieldOptionValue(source: string) {
  return `${optionPrefix}${source}`
}
export function parseProductFieldOption(value: string): ProductFieldSource | null {
  if (!value.startsWith(optionPrefix)) return null
  const source = value.slice(optionPrefix.length)
  return productFieldSources.some(item => item.value === source) ? source as ProductFieldSource : null
}
