// One matching rule for suggestions across the mapping screens: Turkish letters folded
// (ç→c, ğ→g, ı/İ→i, ö→o, ş→s, ü→u), case ignored and punctuation treated as spaces, so
// "Çok Renkli", "cok-renkli" and "ÇOK RENKLİ" are the same key. Stored normalized values are not
// changed by this; it is only used to rank and suggest matches.
const turkishFold: Record<string, string> = { ç: 'c', ğ: 'g', ı: 'i', i: 'i', ö: 'o', ş: 's', ü: 'u', â: 'a', î: 'i', û: 'u' }

export function matchingKey(value: string) {
  return value
    .toLocaleLowerCase('tr-TR')
    .replace(/[çğıiöşüâîû]/g, letter => turkishFold[letter] ?? letter)
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[/\\\-_,.;:&+()[\]'"]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

// Clothing-first synonym groups: every name in a group means the same value or attribute. The
// first entry is the canonical one. Kept short and unambiguous on purpose.
const synonymGroups: string[][] = [
  ['siyah', 'black'], ['beyaz', 'white'], ['lacivert', 'navy', 'navy blue'], ['kirmizi', 'red'], ['mavi', 'blue'],
  ['yesil', 'green'], ['gri', 'grey', 'gray'], ['kahverengi', 'kahve', 'brown'], ['bej', 'beige'], ['pembe', 'pink'],
  ['mor', 'purple'], ['sari', 'yellow'], ['turuncu', 'orange'], ['bordo', 'burgundy', 'maroon'], ['haki', 'khaki'],
  ['antrasit', 'anthracite'], ['altin', 'gold', 'gold rengi'], ['gumus', 'silver', 'gumus rengi'], ['cok renkli', 'multicolor', 'renkli'],
  ['kadin', 'kadin kiz', 'female', 'women'], ['erkek', 'male', 'men'], ['unisex', 'kadin erkek'],
  ['yetiskin', 'adult'], ['cocuk', 'child', 'kids'], ['bebek', 'baby'],
  ['pamuk', 'pamuklu', 'cotton', '%100 pamuk'], ['polyester', 'polyester kumas'], ['viskon', 'viscose'], ['keten', 'linen'], ['yun', 'wool'],
  ['kumas tipi', 'materyal', 'kumas', 'material'], ['kalip', 'fit'], ['yaka tipi', 'yaka', 'yaka sekli'], ['kol boyu', 'kol uzunlugu'],
  ['desen', 'desen tipi', 'pattern'], ['renk', 'color', 'colour'], ['beden', 'size'], ['cinsiyet', 'gender'], ['yas grubu', 'age group'],
  ['mensei', 'origin', 'uretim yeri']
]
const canonicalByKey = new Map<string, string>()
for (const group of synonymGroups) for (const name of group) canonicalByKey.set(matchingKey(name), matchingKey(group[0]))

export function canonicalKey(value: string) {
  const key = matchingKey(value)
  return canonicalByKey.get(key) ?? key
}

const strict = (value: string) => value.trim().toLocaleLowerCase('tr-TR').replace(/\s*\/\s*/g, '/')

// 100 same name, 95 same once letters/punctuation are folded, 90 synonyms, 70 one starts with the
// other, 50 one contains the other, 30 they share a word, 0 unrelated.
export function valueMatchScore(panelLabel: string, remoteName: string) {
  if (!panelLabel.trim() || !remoteName.trim()) return 0
  if (strict(panelLabel) === strict(remoteName)) return 100
  const left = matchingKey(panelLabel)
  const right = matchingKey(remoteName)
  if (!left || !right) return 0
  if (left === right) return 95
  if (canonicalKey(panelLabel) === canonicalKey(remoteName)) return 90
  if (left.startsWith(right) || right.startsWith(left)) return 70
  if (left.includes(right) || right.includes(left)) return 50
  const leftWords = new Set(left.split(' '))
  return right.split(' ').some(word => word.length > 1 && leftWords.has(word)) ? 30 : 0
}

export const autoMatchScore = 90
