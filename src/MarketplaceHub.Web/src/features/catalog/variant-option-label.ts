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

export function variantOptionParts(signature: string | null | undefined) {
  return (signature ?? '').split('|').map(part => part.split('=')).filter(pair => pair.length === 2 && pair[1].trim())
    .map(([name, value]) => ({ name: titleCaseTr(name), value: titleCaseTr(value) }))
}
