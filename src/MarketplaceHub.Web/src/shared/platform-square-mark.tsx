import { platformLogoClass } from './platform-logos'

const platformMarks: Record<string, string> = {
  N11: 'n11',
  PTTAVM: 'PTT',
  PAZARAMA: 'p',
  TRENDYOL: 'ty',
  TRENDYOL_EFATURAM: 'eF',
  HEPSIBURADA: 'hb',
  SHOPIFY: 'S',
}

export function PlatformSquareMark({ code, name }: { code: string | null | undefined; name?: string | null }) {
  const normalized = code?.trim().toUpperCase() ?? ''
  const label = name || code || 'Platform'
  return <span className={`platform-square-mark ${platformLogoClass(code)}`} title={label} aria-hidden="true"><strong>{platformMarks[normalized] ?? (normalized.slice(0, 2) || '?')}</strong></span>
}
