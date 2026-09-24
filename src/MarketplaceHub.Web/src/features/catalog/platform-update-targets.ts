export function quickPlatformUpdateTargets(selectedIds: readonly string[], updateableIds: readonly string[]) {
  const updateable = new Set(updateableIds)
  return [...new Set(selectedIds)].filter(id => updateable.has(id))
}
