export function reorderMediaUrls(urls: string[], source: string, target: string): string[] {
  const sourceIndex = urls.indexOf(source)
  const targetIndex = urls.indexOf(target)
  if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) return urls

  const next = [...urls]
  const [moved] = next.splice(sourceIndex, 1)
  next.splice(targetIndex, 0, moved)
  return next
}

export function mediaRefsEqual(current: string[], original: string[]): boolean {
  return current.length === original.length && current.every((reference, index) => reference === original[index])
}
