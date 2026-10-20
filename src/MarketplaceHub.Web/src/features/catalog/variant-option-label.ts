// "BEDEN=L|RENK=AÇıK KAHVERENGI" → [{ name: 'Beden', value: 'L' }, { name: 'Renk', value: 'Açık Kahverengi' }]
const sizeToken = /^(\d*X*[SML]|\d+([.,]\d+)?|[A-Z]{1,3}\d+)$/i

function titleWord(word: string) {
  if (sizeToken.test(word)) return word.toLocaleUpperCase('tr-TR')
  // Signatures are upper-cased invariantly: a plain I stands for i, while a dotless ı survives as it is.
  const lower = word.replace(/[Iİ]/g, 'i').toLocaleLowerCase('tr-TR')
  return lower.charAt(0).toLocaleUpperCase('tr-TR') + lower.slice(1)
}

export function titleCaseTr(value: string) {
  return value.trim().split(/\s+/).filter(Boolean).map(word => word.split('/').map(titleWord).join('/')).join(' ')
}

// Two signature shapes exist: the legacy upper-cased "BEDEN=L|RENK=MAVİ" and the unified display form
// "Renk: Mavi | Beden: L". The legacy one is title-cased; the display form already carries its labels.
export function variantOptionParts(signature: string | null | undefined) {
  return (signature ?? '').split('|').flatMap(part => {
    const equals = part.indexOf('=')
    const colon = part.indexOf(':')
    const legacy = equals >= 0 && (colon < 0 || equals < colon)
    const index = legacy ? equals : colon
    if (index < 0) return []
    const name = part.slice(0, index).trim()
    const value = part.slice(index + 1).trim()
    if (!name || !value) return []
    return [legacy ? { name: titleCaseTr(name), value: titleCaseTr(value) } : { name, value }]
  })
}
