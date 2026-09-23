export const MAX_PRODUCT_MEDIA_URL_LENGTH = 512

export function productMediaUrlIssue(rawUrl: string): string | null {
  const value = rawUrl.trim()
  if (!value) return 'Adres boş.'

  let url: URL
  try {
    url = new URL(value)
  } catch {
    return 'Geçerli ve herkese açık bir HTTPS adresi girin.'
  }

  if (url.protocol !== 'https:') return 'Adres HTTPS olmalıdır.'
  if (url.username || url.password) return 'Adres kullanıcı adı veya parola içeremez.'
  if (url.href.length > MAX_PRODUCT_MEDIA_URL_LENGTH) return `Adres ${MAX_PRODUCT_MEDIA_URL_LENGTH} karakteri aşamaz.`

  const host = url.hostname.toLocaleLowerCase('en-US').replace(/\.$/u, '')
  if (host === 'localhost' || host.endsWith('.local') || host.endsWith('.internal') || host.endsWith('.lan')) {
    return 'Yerel ağ adresleri kullanılamaz; görsel internete açık olmalıdır.'
  }
  if (isIpv4(host) && !isPublicIpv4(host)) return 'Özel veya yerel ağ IP adresleri kullanılamaz.'
  if (host.startsWith('[') && !isPublicIpv6(host.slice(1, -1))) return 'Özel veya yerel ağ IP adresleri kullanılamaz.'
  return null
}

function isIpv4(host: string): boolean {
  return /^\d{1,3}(?:\.\d{1,3}){3}$/u.test(host)
}

function isPublicIpv4(host: string): boolean {
  const octets = host.split('.').map(Number)
  if (octets.some(value => value > 255)) return false
  const [first, second] = octets
  return first !== 0 && first !== 10 && first !== 127
    && !(first === 169 && second === 254)
    && !(first === 172 && second >= 16 && second <= 31)
    && !(first === 192 && second === 168)
    && first < 224
}

function isPublicIpv6(host: string): boolean {
  const sections = host.toLowerCase().split('::')
  if (sections.length > 2) return false
  const left = sections[0] ? sections[0].split(':') : []
  const right = sections[1] ? sections[1].split(':') : []
  const missing = 8 - left.length - right.length
  if ((sections.length === 1 && missing !== 0) || (sections.length === 2 && missing < 1)) return false
  const groups = [...left, ...Array(missing).fill('0'), ...right]
  if (groups.length !== 8 || groups.some(group => !/^[\da-f]{1,4}$/u.test(group))) return false
  const bytes = groups.flatMap(group => {
    const value = Number.parseInt(group, 16)
    return [value >> 8, value & 0xff]
  })
  const isUnspecified = bytes.every(value => value === 0)
  const isLoopback = bytes.slice(0, 15).every(value => value === 0) && bytes[15] === 1
  const isLinkLocal = bytes[0] === 0xfe && (bytes[1] & 0xc0) === 0x80
  const isSiteLocal = bytes[0] === 0xfe && (bytes[1] & 0xc0) === 0xc0
  const isMulticast = bytes[0] === 0xff
  return !(isUnspecified || isLoopback || isLinkLocal || isSiteLocal || isMulticast)
}
