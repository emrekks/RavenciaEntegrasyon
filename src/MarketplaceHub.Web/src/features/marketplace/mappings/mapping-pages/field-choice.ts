// The writes behind one choice in a marketplace field's "Panel Özelliği" select, in the order they are sent. The new
// source is saved first and the old one removed only after it succeeded (scan 25 Nov 2026, D-4): a refused save used to
// leave the field with nothing, its old mapping already deleted.
export type FieldChoiceStep = 'put-default' | 'put-mapping' | 'delete-mapping' | 'delete-default'

export function fieldChoiceSteps({ hasMapping, hasFieldSource, chosenSource, chosenLocalId }: { hasMapping: boolean; hasFieldSource: boolean; chosenSource: string | null; chosenLocalId: string }): FieldChoiceStep[] {
  const steps: FieldChoiceStep[] = []
  if (chosenSource) steps.push('put-default')
  else if (chosenLocalId) steps.push('put-mapping')
  // A product field and a panel attribute exclude each other: choosing one drops the other, once it is saved.
  if (hasMapping) steps.push('delete-mapping')
  if (hasFieldSource && !chosenSource) steps.push('delete-default')
  return steps
}
