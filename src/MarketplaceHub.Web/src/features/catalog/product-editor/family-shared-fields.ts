// A model's model-row fields (Sopyo's main row: name, description, category, brand, VAT, warranty, delivery days and the
// card's category attributes) are the model's, whichever colour row opened the editor (owner, 11.11.2026). The save
// names the fields the owner changed (familySharedFields); the server writes those to every colour card, each name
// keeping its own colour word, and leaves the fields the owner did not touch as each card has them.

export type SharedFieldValues = {
  title: string; description: string; brandId: string; categoryId: string
  vatRate: string; vatIncluded: string; warrantyMonths: string; deliveryDays: string
  attributeSelections: Record<string, string[]>; attributeTextValues: Record<string, string>
}

const sortedJson = (value: Record<string, unknown>) => JSON.stringify(Object.keys(value).sort().map(key => [key, value[key]]))

export function sharedFieldSnapshot(values: SharedFieldValues): Record<string, string> {
  return {
    title: values.title.trim(),
    description: values.description.trim(),
    brand: values.brandId,
    category: values.categoryId,
    vat: `${Number(values.vatRate || 0)}|${values.vatIncluded || 'INCLUDED'}`,
    warranty: String(Number(values.warrantyMonths || 0)),
    deliveryDays: String(Number(values.deliveryDays || 0)),
    attributes: sortedJson(Object.fromEntries(Object.entries(values.attributeSelections).map(([key, ids]) => [key, [...ids].sort()]))) + sortedJson(values.attributeTextValues)
  }
}

// The model-row fields the owner changed since the editor was loaded, in the server's names.
export function editedSharedFields(initial: Record<string, string>, current: Record<string, string>): string[] {
  return Object.keys(current).filter(field => field in initial && initial[field] !== current[field])
}

const FIELD_LABELS: Record<string, string> = {
  title: 'Ürün Adı', description: 'Açıklama', brand: 'Marka', category: 'Kategori', vat: 'KDV', warranty: 'Garanti Süresi', deliveryDays: 'Teslimat Süresi', desi: 'Desi'
}

// "Renklerde farklı: Açıklama (Siyah, Kahverengi) · KDV (Mürdüm)": the model-row fields other colour cards hold
// differently; the save leaves them unless the owner changes that field, then every colour takes the new value.
export function familyDifferenceText(differences: ReadonlyArray<{ field: string; colors: string[] }> | null | undefined): string {
  const parts = (differences ?? []).filter(item => FIELD_LABELS[item.field] && item.colors.length).map(item => `${FIELD_LABELS[item.field]} (${item.colors.join(', ')})`)
  return parts.length ? `Renklerde farklı: ${parts.join(' · ')}` : ''
}
