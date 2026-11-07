// The delivery address as the label prints it: one part a line, as on Trendyol Express' own sticker — the street lines,
// then the neighbourhood, then "district / city". Built from the address's own fields when it has them; a marketplace
// that sends one text only (or a Trendyol fullAddress, which is address1, a run of spaces, district and city) is split
// at its separators. Words a buyer typed together ("BLOKDAİRE", "12DAİRE") get their space back; nothing is ever joined.

export type ShippingLabelAddressSource = {
  fullAddress?: string | null
  address1?: string | null
  address2?: string | null
  neighborhood?: string | null
  district?: string | null
  city?: string | null
  isDeliveryPoint?: boolean
  deliveryPointType?: string | null
}

const meaningful = (value: unknown): value is string => typeof value === 'string' && /[\p{L}\p{N}]/u.test(value)
const squeeze = (value: string) => value.replace(/\s+/g, ' ').trim()
const lower = (value: string) => value.toLocaleLowerCase('tr-TR')

// Address words that start a new part. Two of them typed together, or one typed straight after a number, are split.
const addressWords = ['BLOK', 'BLK', 'DAİRE', 'DAIRE', 'KAT', 'APARTMANI', 'APT', 'SİTESİ', 'SITESI', 'SOKAK', 'SOKAĞI', 'SOK', 'CADDESİ', 'CADDESI', 'CAD', 'MAHALLESİ', 'MAHALLESI', 'MAH', 'BULVARI', 'BLV', 'NO', 'KAPI', 'GİRİŞ']
// Turkish dotted and dotless i match their own lower case; the other letters match either case through the i flag.
const wordPattern = addressWords.map(word => Array.from(word, letter => letter === 'İ' ? '[İi]' : letter === 'I' ? '[Iı]' : letter).join('')).join('|')
const gluedWords = new RegExp(`(?<=(?:${wordPattern}))(?=(?:${wordPattern})(?![\\p{L}]))`, 'giu')
const gluedAfterNumber = new RegExp(`(?<=\\p{N})(?=(?:${wordPattern})(?![\\p{L}]))`, 'giu')

export function spacedAddressText(value: string) {
  return squeeze(value.replace(gluedWords, ' ').replace(gluedAfterNumber, ' '))
}

export function shippingLabelAddressLines(source: ShippingLabelAddressSource | null | undefined, fallbackText = ''): string[] {
  const data = source ?? {}
  const prefix = data.isDeliveryPoint ? `Teslimat Noktası${meaningful(data.deliveryPointType) ? ` (${squeeze(data.deliveryPointType)})` : ''}` : null
  const district = meaningful(data.district) ? squeeze(data.district) : ''
  const city = meaningful(data.city) ? squeeze(data.city) : ''
  const place = [district, city].filter(Boolean).filter((item, index, all) => all.findIndex(other => lower(other) === lower(item)) === index).join(' / ')
  let street = [data.address1, data.address2].filter(meaningful).map(item => spacedAddressText(item))
  if (!street.length && meaningful(data.fullAddress)) {
    // Trendyol's fullAddress is address1, a run of spaces, then district and city: keep the part before the run.
    const [head] = data.fullAddress.split(/\s{3,}| · /)
    street = [spacedAddressText(head)]
  }
  if (!street.length) {
    const parts = fallbackText.split(/\n| · |\s{3,}/).map(item => spacedAddressText(item)).filter(meaningful)
    return prefix ? [prefix, ...parts] : parts
  }
  // As on Trendyol's sticker, the neighbourhood and "district / city" get lines of their own even when the buyer also
  // typed them into the street line; a line is only left out when it would repeat the one before it word for word.
  const lines = [...street]
  for (const line of [meaningful(data.neighborhood) ? squeeze(data.neighborhood) : '', place]) {
    if (line && lower(line) !== lower(lines[lines.length - 1])) lines.push(line)
  }
  return prefix ? [prefix, ...lines] : lines
}
