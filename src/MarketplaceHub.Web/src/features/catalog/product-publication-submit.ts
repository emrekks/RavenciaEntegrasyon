export function productPublicationTargets(isPanelOnlySave: boolean, connectionIds: readonly string[]) {
  return isPanelOnlySave ? [] : [...connectionIds]
}
