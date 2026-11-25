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

// What a refused step says. A removal refused after the new source was saved leaves both in place: the person is told
// so plainly, and the field's lists are read again (independent review, 25 Nov 2026).
export function fieldChoiceFailureMessage(failedStep: FieldChoiceStep | null, newSourceSaved: boolean, reason: string) {
  if (newSourceSaved && (failedStep === 'delete-mapping' || failedStep === 'delete-default'))
    return `Yeni eşleme kaydedildi, eskisi silinemedi; şimdilik ikisi birlikte duruyor. Eskisini yeniden kaldırın. (${reason})`
  return reason
}
