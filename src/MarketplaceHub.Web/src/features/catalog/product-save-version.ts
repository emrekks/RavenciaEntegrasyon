function withoutVersionMetadata(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(withoutVersionMetadata)
  if (!value || typeof value !== 'object') return value

  return Object.fromEntries(
    Object.entries(value)
      .filter(([key]) => key !== 'version' && key !== 'updatedAt')
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, child]) => [key, withoutVersionMetadata(child)])
  )
}

export function sameProductSnapshotIgnoringVersion(left: unknown, right: unknown): boolean {
  return JSON.stringify(withoutVersionMetadata(left)) === JSON.stringify(withoutVersionMetadata(right))
}
