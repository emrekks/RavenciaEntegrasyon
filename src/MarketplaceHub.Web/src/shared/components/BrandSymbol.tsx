import type { CSSProperties } from 'react'

// The brand symbol in inverse colouring per theme: black R and circle on the light theme, white on the dark theme,
// the gold arc kept in both. Both images render; the CSS in design-system.css shows the one for html[data-theme].
export function BrandSymbol({ size = 128, className, style }: { size?: 128 | 256, className?: string, style?: CSSProperties }) {
  const cls = (theme: 'light' | 'dark') => `brand-symbol brand-symbol-${theme}${className ? ` ${className}` : ''}`
  return <>
    <img className={cls('light')} src={`/pack/brand/ravencia-symbol-light-${size}.webp`} alt="" style={style} />
    <img className={cls('dark')} src={`/pack/brand/ravencia-symbol-dark-${size}.webp`} alt="" style={style} />
  </>
}
