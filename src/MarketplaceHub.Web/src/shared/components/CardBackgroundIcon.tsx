import { useId } from 'react'
import { uiIconBody, type UiIconName } from './UiIcon'
import './card-background-icon.css'

// The icon of a metric card (a label and a figure) is not a box of its own: it is drawn into the card's background,
// oversized, tilted, cut by the right edge and fading in towards the text. Put <CardBackgroundIcon> as the FIRST child
// of the card; the card is made a positioned box and its other children are lifted above the icon by
// card-background-icon.css, so one component and one rule set serve every card of the panel.
// The clipping layer is its own element, so a tooltip or popover of the card is not clipped by it.
// The glyph is the panel's main icon set (UiIcon: the sidebar's and the buttons'), so a concept looks the same here as
// everywhere else: orders, returns, invoice, products, reports, stock, truck, clock, calendar, cancel, alert …
export type CardTone = 'warn' | 'bad' | 'ok' | 'info'
export type CardIconName = UiIconName

export function CardBackgroundIcon({ name, tone }: { name: CardIconName; tone?: CardTone }) {
  const maskId = `cbi-${useId().replace(/:/g, '')}`
  return <i className="rv-card-bg" data-tone={tone} aria-hidden="true">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" focusable="false">{uiIconBody(name, maskId)}</svg>
  </i>
}
