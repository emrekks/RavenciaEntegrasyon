export type ProductAttributeSelections = Record<string, string[]>

export function toggleProductAttributeValue(
  current: ProductAttributeSelections,
  attributeId: string,
  valueId: string,
  singleSelect: boolean,
): ProductAttributeSelections {
  const values = current[attributeId] ?? []
  const nextValues = values.includes(valueId)
    ? values.filter(value => value !== valueId)
    : singleSelect
      ? [valueId]
      : [...values, valueId]

  return { ...current, [attributeId]: nextValues }
}
