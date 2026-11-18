// Short visible form of the "1 gün 20 saat 44 dakika kaldı" countdown: the two biggest units, abbreviated. The full text
// stays in the tooltip and the screen-reader text.
const count = (full: string, pattern: RegExp) => Number(pattern.exec(full)?.[1] ?? 0)

export function shortRemaining(full: string): string {
  const days = count(full, /(\d+)\s*gün/u)
  const hours = count(full, /(\d+)\s*saat/u)
  const minutes = count(full, /(\d+)\s*dakika/u)
  if (!full.includes('kaldı') || days + hours + minutes === 0) return full
  if (days > 0) return `${days} g${hours ? ` ${hours} sa` : ''} kaldı`
  if (hours > 0) return `${hours} sa${minutes ? ` ${minutes} dk` : ''} kaldı`
  return `${minutes} dk kaldı`
}
