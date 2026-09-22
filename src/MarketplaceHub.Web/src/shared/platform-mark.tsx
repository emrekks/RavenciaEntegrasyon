import { orderPlatformLogoSource, platformLogoClass } from './platform-logos'

export function PlatformMark({ code, name }: { code: string | null | undefined; name: string | null | undefined }) {
  const source = orderPlatformLogoSource(code)
  const label = name || code || 'Platform'
  if (!source) return <span className="order-platform-fallback" title={label}>{label}</span>
  return <img className={`order-platform-logo ${platformLogoClass(code)}`} src={source} alt={label} loading="lazy" decoding="async" />
}
