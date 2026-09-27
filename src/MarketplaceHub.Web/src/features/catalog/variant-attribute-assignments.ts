import { normalizeVariantOptionValue } from './variant-option-matching'

export type VariantAttributeAssignment = {
  attributeId: string
  valueId: string | null
  textValue: string | null
  numberValue: number | null
  booleanValue: boolean | null
  sortOrder: number
}

export type VariantAttributeOptionRequirement = {
  attributeId: string
  name: string
  values: Array<{ id: string; value: string }>
}

const colorOptionNames = new Set(['RENK', 'RENKLER', 'COLOR', 'COLORS', 'COLOUR', 'COLOURS', 'WEBRENK', 'WEBCOLOR', 'WEBCOLOUR'])
const sizeOptionNames = new Set(['BEDEN', 'BEDENLER', 'SIZE', 'SIZES', 'BOYUT', 'BOYUTLAR', 'NUMARA', 'NUMARALAR', 'SHOESIZE', 'AYAKKABINUMARASI'])

function normalizedOptionName(value: string) {
  return value.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}

function optionNamesMatch(left: string, right: string) {
  const normalizedLeft = normalizedOptionName(left)
  const normalizedRight = normalizedOptionName(right)
  if (normalizedLeft === normalizedRight) return true
  if (colorOptionNames.has(normalizedLeft) && colorOptionNames.has(normalizedRight)) return true
  return sizeOptionNames.has(normalizedLeft) && sizeOptionNames.has(normalizedRight)
}

function assignment(attributeId: string, valueId: string, sortOrder: number): VariantAttributeAssignment {
  return { attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder }
}

/**
 * Builds the exact variant-attribute list to persist when editing a product.
 * Persisted non-option fields survive the edit, while category OPTION fields
 * are rebuilt synchronously from the row's option labels instead of depending
 * on the editor's asynchronous post-load hydration effect.
 */
export function buildVariantAttributeAssignments(input: {
  options: Record<string, string>
  selectedValueIds: Record<string, string>
  existing: VariantAttributeAssignment[]
  optionRequirements: VariantAttributeOptionRequirement[]
}): VariantAttributeAssignment[] {
  const optionRequirementIds = new Set(input.optionRequirements.map(item => item.attributeId))
  const assignments = new Map<string, VariantAttributeAssignment>()

  for (const existing of input.existing) {
    if (!optionRequirementIds.has(existing.attributeId)) assignments.set(existing.attributeId, existing)
  }

  for (const [attributeId, valueId] of Object.entries(input.selectedValueIds)) {
    if (optionRequirementIds.has(attributeId)) continue
    assignments.set(attributeId, assignment(attributeId, valueId, assignments.size))
  }

  for (const [index, requirement] of input.optionRequirements.entries()) {
    const optionEntry = Object.entries(input.options).find(([name]) => optionNamesMatch(name, requirement.name))
    if (optionEntry) {
      const matchedValue = requirement.values.find(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(optionEntry[1]))
      if (matchedValue) assignments.set(requirement.attributeId, assignment(requirement.attributeId, matchedValue.id, index))
      continue
    }

    const selectedValueId = input.selectedValueIds[requirement.attributeId]
    if (selectedValueId && requirement.values.some(value => value.id === selectedValueId)) {
      assignments.set(requirement.attributeId, assignment(requirement.attributeId, selectedValueId, index))
    }
  }

  return [...assignments.values()].sort((left, right) => left.sortOrder - right.sortOrder)
}
