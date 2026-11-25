// A model is BRAND + MODEL CODE: the products list groups cards, and its bulk actions reach a family, by the key the
// server names each card's family with (ProductView.FamilyKey = ProductFamilyKeyPolicy.FamilyKey). A record without it
// (an older answer, a test) is keyed the same way here: brand id, then the model code's shared catalog key.
import { variantCodeMatchKey } from '../platform-code-rules'

export type FamilyKeyRecord = {
  id: string
  brandId?: string | null
  modelCode?: string | null
  familyKey?: string | null
  variants: ReadonlyArray<{ modelCode?: string | null }>
}

export function productFamilyKey(record: FamilyKeyRecord | undefined, id = record?.id ?? '') {
  if (record?.familyKey) return record.familyKey
  const modelCode = record?.modelCode?.trim() || record?.variants.map(variant => variant.modelCode?.trim()).find(Boolean)
  const key = modelCode ? variantCodeMatchKey(modelCode) : ''
  return key ? `model:${record?.brandId ?? '-'}:${key}` : `product:${id}`
}

// The records by family, in the order each family first appears; a family's records in their own order.
export function groupByFamily<T extends FamilyKeyRecord>(records: readonly T[]): Array<{ id: string; members: T[] }> {
  const groups = new Map<string, T[]>()
  for (const record of records) {
    const id = productFamilyKey(record)
    const members = groups.get(id)
    if (members) members.push(record)
    else groups.set(id, [record])
  }
  return [...groups].map(([id, members]) => ({ id, members }))
}
