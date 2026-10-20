// Money in the panel: Turkish number format with the currency of the record. A currency code the browser does not know
// (empty, misspelled, a marketplace's own code) must not take the screen down: Intl would throw a RangeError, so the
// amount is shown as a plain number followed by the code instead.
type MoneyOptions = Omit<Intl.NumberFormatOptions, 'style' | 'currency'>

const formatters = new Map<string, Intl.NumberFormat>()
function formatter(currency: string, options: MoneyOptions) {
  const key = `${currency}|${JSON.stringify(options)}`
  let cached = formatters.get(key)
  if (!cached) {
    cached = new Intl.NumberFormat('tr-TR', { ...options, style: 'currency', currency })
    formatters.set(key, cached)
  }
  return cached
}

export function formatMoney(value: number | null | undefined, currency: string | null | undefined = 'TRY', options: MoneyOptions = {}, fallback = '—') {
  if (value === null || value === undefined || Number.isNaN(value)) return fallback
  const code = (currency ?? '').trim().toUpperCase() || 'TRY'
  try {
    return formatter(code, options).format(value)
  } catch {
    const digits = { minimumFractionDigits: 2, maximumFractionDigits: 2, ...options }
    return `${new Intl.NumberFormat('tr-TR', digits).format(value)} ${code}`
  }
}
