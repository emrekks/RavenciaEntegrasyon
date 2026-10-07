export type OrderLineVariantOption = { label: string; value: string }

function optionRows(value: string | null): OrderLineVariantOption[] {
  const parts = (value ?? '').split(/[|,·;]+/).map(item => item.trim()).filter(Boolean)
  const fields = parts
    .map(part => {
      const match = part.match(/^([^:=-]+)\s*[:=-]\s*(.+)$/)
      return match ? { label: match[1].trim(), value: match[2].trim() } : null
    })
    .filter((item): item is OrderLineVariantOption => item !== null)

  return fields.length ? fields : value ? [{ label: 'Seçenek', value }] : []
}

function optionDimension(label: string): 'color' | 'size' | null {
  const normalized = label.normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[\s_-]+/g, '').toLocaleLowerCase('en-US')
  if (['renk', 'color', 'colour'].includes(normalized)) return 'color'
  if (['beden', 'size'].includes(normalized)) return 'size'
  return null
}

export function visibleOrderLineVariantOptions(value: string | null): OrderLineVariantOption[] {
  const seen = new Set<string>()
  return optionRows(value).filter(option => {
    const dimension = optionDimension(option.label)
    if (!dimension || seen.has(dimension)) return false
    seen.add(dimension)
    return true
  })
}
