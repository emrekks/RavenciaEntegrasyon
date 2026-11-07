/**
 * A buyer name as the marketplace stored it often starts words in lower case ("neslihan yurttutaan",
 * "Melike çağırcı"). This shows each word with a capital first letter (Turkish rules: i → İ, ı → I) and leaves the
 * rest of the word, and any word that already starts in capitals or digits ("DSM IGA"), as it is.
 */
export function personName(value: string | null | undefined): string {
  if (!value) return ''
  return value.replace(/(^|[\s\-/.'’(])(\p{Ll})/gu, (_all, lead: string, letter: string) => lead + letter.toLocaleUpperCase('tr-TR'))
}
