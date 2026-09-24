export function parseBarcodeClipboardValues(text: string): string[] {
  const lines = text.replace(/\r\n?/gu, '\n').split('\n')
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop()
  return lines.map(line => line.split('\t', 1)[0].trim())
}

export function barcodeClipboardIssue(values: readonly string[], expectedCount: number, reservedValues: readonly string[] = []): string | null {
  if (expectedCount < 1) return 'Yapıştırılacak barkod satırı bulunamadı.'
  if (values.length !== expectedCount) return `Panoda ${values.length} barkod satırı var; hedef için tam ${expectedCount} satır gerekli.`
  if (values.some(value => !value.trim())) return 'Panoda boş barkod hücresi var. Boş hücreleri kaldırıp tekrar kopyalayın.'

  const seen = new Set(reservedValues.map(value => value.trim().toLocaleUpperCase('tr-TR')).filter(Boolean))
  for (const value of values) {
    const normalized = value.trim().toLocaleUpperCase('tr-TR')
    if (seen.has(normalized)) return 'Tekrar eden barkod bulundu; hiçbir satır değiştirilmedi.'
    seen.add(normalized)
  }
  return null
}
