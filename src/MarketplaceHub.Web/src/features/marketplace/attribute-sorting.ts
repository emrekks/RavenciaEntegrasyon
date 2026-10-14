export function sortByTurkishName<T extends { name: string }>(items: readonly T[]): T[] {
  return [...items].sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true }))
}
