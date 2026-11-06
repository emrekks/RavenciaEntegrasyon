// The RAVENCIA wordmark: black letters on the light theme, white on the dark theme. Both images render; the CSS in
// design-system.css (img.brand-symbol-*) shows the one for html[data-theme], the same way as BrandSymbol.
export function BrandWordmark({ className, alt = 'Ravencia' }: { className?: string, alt?: string }) {
  const cls = (theme: 'light' | 'dark') => `brand-symbol brand-symbol-${theme}${className ? ` ${className}` : ''}`
  return <>
    <img className={cls('light')} src="/pack/brand/ravencia-wordmark-light.webp" alt={alt} />
    <img className={cls('dark')} src="/pack/brand/ravencia-wordmark-dark.webp" alt={alt} aria-hidden="true" />
  </>
}
