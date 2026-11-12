// Whether the text typed in a reset confirmation field is the expected word: spaces at either end and the case of the
// letters do not matter, in the English and the Turkish way (the server compares with ConfirmationText.Matches).
export function confirmationMatches(typed: string, expected: string) {
  const text = typed.trim()
  if (!text) return false
  return text.toUpperCase() === expected.toUpperCase() || text.toLocaleUpperCase('tr-TR') === expected.toLocaleUpperCase('tr-TR')
}
