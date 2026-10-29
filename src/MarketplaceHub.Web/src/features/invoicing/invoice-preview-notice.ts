// What "Fatura Kes" says about the selected orders it leaves out: those with no E-Faturam connection of their environment
// (an error), and those that already have a manual invoice document (skipped, not a connection problem). Null when none.
export function invoicePreviewNotice(withoutProvider: string[], withManualDocument: string[]): { text: string; kind: 'error' | 'info' } | null {
  const list = (values: string[]) => `${values.slice(0, 4).join(', ')}${values.length > 4 ? ` ve ${values.length - 4} kayıt daha` : ''}`
  const parts = [
    withoutProvider.length ? `Aynı ortamda etkin ve API bilgileri kayıtlı E-Faturam bağlantısı bulunamadı: ${list(withoutProvider)}.` : null,
    withManualDocument.length ? `Manuel fatura belgesi zaten kayıtlı olduğu için atlandı: ${list(withManualDocument)}.` : null,
  ].filter((part): part is string => part !== null)
  return parts.length ? { text: parts.join(' '), kind: withoutProvider.length ? 'error' : 'info' } : null
}
