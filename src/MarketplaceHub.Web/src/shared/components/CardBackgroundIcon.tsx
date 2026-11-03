import type { ReactNode } from 'react'
import './card-background-icon.css'

// The icon of a metric card (a label and a figure) is not a box of its own: it is drawn into the card's background,
// oversized, tilted, cut by the right edge and fading in towards the text. Put <CardBackgroundIcon> as the FIRST child
// of the card; the card is made a positioned box and its other children are lifted above the icon by
// card-background-icon.css, so one component and one rule set serve every card of the panel.
// The clipping layer is its own element, so a tooltip or popover of the card is not clipped by it.
export type CardTone = 'warn' | 'bad' | 'ok' | 'info'

// Simple 16 px stroke icons (the dashboard's set, plus the ones the other pages' cards need).
const bodies = {
  box: <><path d="M8 1.8 13.8 4.8v6.4L8 14.2 2.2 11.2V4.8z" /><path d="M2.2 4.8 8 7.8l5.8-3M8 7.8v6.4M5.1 3.3l5.8 3" /></>,
  boxOff: <><path d="M8 1.8 13.8 4.8v6.4L8 14.2 2.2 11.2V4.8z" /><path d="M2.2 4.8 8 7.8l5.8-3M8 7.8v6.4" /><path d="M2 2l12 12" /></>,
  clock: <><circle cx="8" cy="8.8" r="5" /><path d="M8 6.3v2.6l1.7 1M2.6 3.4 4.4 1.9M13.4 3.4l-1.8-1.5" /></>,
  return: <><path d="M3 6.5h7.5a3 3 0 0 1 0 6H6" /><path d="M5.8 3.7 3 6.5l2.8 2.8" /></>,
  invoice: <><path d="M3.5 1.8h9v12.4l-1.5-1-1.5 1-1.5-1-1.5 1-1.5-1-1.5 1z" /><path d="M6 5.5h4M6 8h4M6 10.5h2.5" /></>,
  calendar: <><rect x="2.5" y="3.5" width="11" height="10" rx="1.5" /><path d="M2.5 6.5h11M5.5 2v3M10.5 2v3M8 9v2" /></>,
  cancel: <><circle cx="8" cy="8" r="5.8" /><path d="M3.9 3.9l8.2 8.2" /></>,
  bars: <><path d="M2.5 13.5h11" /><rect x="3" y="4" width="2.4" height="7.5" rx=".5" /><rect x="6.8" y="7" width="2.4" height="4.5" rx=".5" /><rect x="10.6" y="9.5" width="2.4" height="2" rx=".5" /></>,
  truck: <><path d="M1.8 4h7.4v6.6H1.8z" /><path d="M9.2 6.4h2.6l2.2 2.3v1.9H9.2" /><circle cx="4.6" cy="11.4" r="1.4" /><circle cx="11.4" cy="11.4" r="1.4" /></>,
  cycle: <><path d="M13.2 8a5.2 5.2 0 0 1-9.3 3.2M2.8 8a5.2 5.2 0 0 1 9.3-3.2" /><path d="M12.4 2.2V5H9.6M3.6 13.8V11h2.8" /></>,
  globe: <><circle cx="8" cy="8" r="6" /><path d="M2 8h12M8 2c1.8 1.7 2.7 3.7 2.7 6S9.8 12.3 8 14c-1.8-1.7-2.7-3.7-2.7-6S6.2 3.7 8 2z" /></>,
  repeat: <><path d="M2.5 7V6A2.5 2.5 0 0 1 5 3.5h8M10.5 1 13 3.5 10.5 6M13.5 9v1a2.5 2.5 0 0 1-2.5 2.5H3M5.5 15 3 12.5 5.5 10" /></>,
  send: <><path d="M14 2 7 9" /><path d="M14 2l-4.5 12-2.5-5-5-2.5z" /></>,
  check: <><circle cx="8" cy="8" r="5.8" /><path d="M5.3 8.2l1.9 1.9 3.6-3.9" /></>,
  layers: <><path d="M8 2 14 5 8 8 2 5z" /><path d="M2 8l6 3 6-3M2 11l6 3 6-3" /></>,
  grid: <><rect x="2.5" y="2.5" width="4.5" height="4.5" rx="1" /><rect x="9" y="2.5" width="4.5" height="4.5" rx="1" /><rect x="2.5" y="9" width="4.5" height="4.5" rx="1" /><rect x="9" y="9" width="4.5" height="4.5" rx="1" /></>,
  alert: <><path d="M8 2.2 14.2 13H1.8z" /><path d="M8 6.5v3M8 11.2v.1" /></>,
  coin: <><rect x="1.8" y="4" width="12.4" height="8" rx="1.5" /><circle cx="8" cy="8" r="2" /><path d="M4 8h.1M12 8h.1" /></>,
  cart: <><path d="M1.8 2.5h2l1.5 7h6.4l1.5-5H4.3" /><circle cx="6.2" cy="12.6" r="1" /><circle cx="11" cy="12.6" r="1" /></>,
  users: <><circle cx="6" cy="5.5" r="2.3" /><path d="M1.8 13c.3-2.4 2.1-3.8 4.2-3.8s3.9 1.4 4.2 3.8M10.8 3.4a2.3 2.3 0 0 1 0 4.2M12 9.4c1.4.4 2.3 1.6 2.4 3.6" /></>,
  percent: <><path d="M12.5 3.5l-9 9" /><circle cx="4.7" cy="4.7" r="1.6" /><circle cx="11.3" cy="11.3" r="1.6" /></>,
  key: <><circle cx="5" cy="10.5" r="2.7" /><path d="M7 8.6l6.2-6.2M10.6 5l1.8 1.8M12.2 3.4l1.3 1.3" /></>,
  pulse: <path d="M1.5 8h3l1.8-5 3.4 10 1.8-5h3" />,
  trend: <><path d="M1.8 11.5l4-4 2.8 2.8 5.6-5.8" /><path d="M10 4.5h4.2v4.2" /></>,
  tag: <><path d="M2 2h6.2l6 6-6.2 6.2-6-6z" /><circle cx="5.2" cy="5.2" r="1" /></>,
} satisfies Record<string, ReactNode>

export type CardIconName = keyof typeof bodies

export function CardBackgroundIcon({ name, tone }: { name: CardIconName; tone?: CardTone }) {
  return <i className="rv-card-bg" data-tone={tone} aria-hidden="true">
    <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.1} strokeLinecap="round" strokeLinejoin="round" focusable="false">{bodies[name]}</svg>
  </i>
}
