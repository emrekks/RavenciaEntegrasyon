import { platformLogoClass, platformLogoSource } from './platform-logos'

const platformMarks: Record<string, string> = {
  N11: 'N11',
  PTTAVM: 'PTT',
  PAZARAMA: 'p',
  TRENDYOL: 'ty',
  TRENDYOL_EFATURAM: 'eF',
  HEPSIBURADA: 'hb',
  SHOPIFY: 'S',
}

// size="sm" is the 20 px mark for rows, chips and menus. "labelled" makes the mark itself the accessible name when no
// text sits next to it.
export function PlatformSquareMark({ code, name, size, labelled = false }: { code: string | null | undefined; name?: string | null; size?: 'sm'; labelled?: boolean }) {
  const normalized = code?.trim().toUpperCase() ?? ''
  const label = name || code || 'Platform'
  const logoSource = platformLogoSource(code)

  return (
    <span className={`platform-square-mark ${platformLogoClass(code)}${size === 'sm' ? ' is-sm' : ''}`} title={label} {...(labelled ? { role: 'img', 'aria-label': label } : { 'aria-hidden': true })}>
      {logoSource ? <img src={logoSource} alt="" /> : <strong>{platformMarks[normalized] ?? (normalized.slice(0, 2) || '?')}</strong>}
    </span>
  )
}
