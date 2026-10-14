export function formatPanelColorValue(value: string): string {
  return value.trim().toLocaleLowerCase('tr-TR').replace(/(^|[\s-])([\p{L}])/gu, (_, separator: string, letter: string) => `${separator}${letter.toLocaleUpperCase('tr-TR')}`)
}

export function usesCustomPanelColorValue(attributeName: string): boolean {
  const normalized = attributeName
    .replace(/\[(?:A-)?TDG\]/gi, '')
    .replace(/[\s_-]+/g, '')
    .toLocaleUpperCase('tr-TR')
  return ['RENK', 'COLOR', 'COLOUR'].includes(normalized)
}
