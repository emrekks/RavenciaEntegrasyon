import type { ReactNode } from 'react'

export type UiIconName =
  | 'alert'
  | 'alignCenter'
  | 'alignLeft'
  | 'alignRight'
  | 'arrowLeft'
  | 'arrowRight'
  | 'barcode'
  | 'bold'
  | 'calendar'
  | 'check'
  | 'chevronDown'
  | 'chevronLeft'
  | 'chevronRight'
  | 'clearFormatting'
  | 'code'
  | 'close'
  | 'command'
  | 'download'
  | 'edit'
  | 'externalLink'
  | 'filter'
  | 'grid'
  | 'image'
  | 'italic'
  | 'layout'
  | 'link'
  | 'list'
  | 'loader'
  | 'moreVertical'
  | 'paragraph'
  | 'plus'
  | 'redo'
  | 'search'
  | 'sparkle'
  | 'sync'
  | 'textColor'
  | 'undo'
  | 'underline'
  | 'upload'

type UiIconProps = {
  name: UiIconName
  className?: string
  size?: number
  title?: string
}

const paths: Record<UiIconName, ReactNode> = {
  alert: <><circle cx="12" cy="12" r="9" /><path d="M12 7.5v5m0 3.25h.01" /></>,
  alignCenter: <><path d="M5 6h14M8 10h8M5 14h14M8 18h8" /></>,
  alignLeft: <><path d="M5 6h14M5 10h10M5 14h14M5 18h10" /></>,
  alignRight: <><path d="M5 6h14M9 10h10M5 14h14M9 18h10" /></>,
  arrowLeft: <path d="M19 12H5m6-6-6 6 6 6" />,
  arrowRight: <path d="M5 12h14m-6-6 6 6-6 6" />,
  barcode: <><path d="M4 5v14m3-14v14m3-11v8m4-11v14m3-14v14m3-11v8" /><path d="M3 5h18M3 19h18" opacity=".35" /></>,
  bold: <path d="M8 5h5.2a3.3 3.3 0 0 1 0 6.6H8m0 0h5.8a3.7 3.7 0 0 1 0 7.4H8V5m0 0v14" />,
  calendar: <><rect x="4" y="5" width="16" height="15" rx="2" /><path d="M8 3v4m8-4v4M4 9h16" /></>,
  check: <path d="m5 12.5 4.2 4.2L19 7" />,
  chevronDown: <path d="m6 9 6 6 6-6" />,
  chevronLeft: <path d="m15 5-7 7 7 7" />,
  chevronRight: <path d="m9 5 7 7-7 7" />,
  clearFormatting: <><path d="M5 5h14M7 9h10M9 13h6M11 17h2" /><path d="m4 4 16 16" /></>,
  code: <><path d="m9 7-5 5 5 5M15 7l5 5-5 5" /><path d="m14 4-4 16" /></>,
  close: <path d="m6 6 12 12M18 6 6 18" />,
  command: <path d="M8 8a3 3 0 1 1 3-3v14a3 3 0 1 1-3-3h8a3 3 0 1 1-3 3V5a3 3 0 1 1 3 3H8Z" />,
  download: <><path d="M12 4v11m-4-4 4 4 4-4" /><path d="M5 20h14" /></>,
  edit: <><path d="m5 16-.8 3.8L8 19l10.5-10.5a2.1 2.1 0 0 0-3-3L5 16Z" /><path d="m14.5 6.5 3 3" /></>,
  externalLink: <><path d="M14 5h5v5m0-5-8 8" /><path d="M19 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" /></>,
  filter: <path d="M4 6h16M7 12h10m-7 6h4" />,
  grid: <><rect x="4" y="4" width="6" height="6" rx="1" /><rect x="14" y="4" width="6" height="6" rx="1" /><rect x="4" y="14" width="6" height="6" rx="1" /><rect x="14" y="14" width="6" height="6" rx="1" /></>,
  image: <><rect x="4" y="5" width="16" height="14" rx="2" /><circle cx="9" cy="10" r="1.2" /><path d="m5 17 4.5-4 3 2.5 2.5-2 4 3.5" /></>,
  italic: <path d="M10 5h8M6 19h8M14 5 10 19" />,
  layout: <><rect x="4" y="4" width="16" height="16" rx="2" /><path d="M4 10h16M10 10v10" /></>,
  link: <><path d="m9.5 14.5 5-5" /><path d="m7 17-1.2 1.2a3.3 3.3 0 0 1-4.7-4.7l3.4-3.4a3.3 3.3 0 0 1 4.7 0M17 7l1.2-1.2a3.3 3.3 0 0 1 4.7 4.7l-3.4 3.4a3.3 3.3 0 0 1-4.7 0" /></>,
  list: <><path d="M9 6h11M9 12h11M9 18h11" /><path d="M4 6h.01M4 12h.01M4 18h.01" /></>,
  loader: <path d="M12 4a8 8 0 1 0 8 8" />,
  moreVertical: <><circle cx="12" cy="5" r="1" fill="currentColor" stroke="none" /><circle cx="12" cy="12" r="1" fill="currentColor" stroke="none" /><circle cx="12" cy="19" r="1" fill="currentColor" stroke="none" /></>,
  paragraph: <><path d="M5 5h10a4 4 0 0 1 0 8H9" /><path d="M9 5v14M13 5v14" /></>,
  plus: <path d="M12 5v14M5 12h14" />,
  redo: <><path d="M19 8v5h-5" /><path d="M19 13a7 7 0 1 0-2 4" /></>,
  search: <><circle cx="10.8" cy="10.8" r="6.3" /><path d="m16 16 4 4" /></>,
  sparkle: <path d="m12 3 1.5 6.5L20 12l-6.5 1.5L12 20l-1.5-6.5L4 12l6.5-2.5L12 3Z" />,
  sync: <><path d="M20 7v5h-5" /><path d="M4 17v-5h5" /><path d="M6.2 9A7 7 0 0 1 18.5 7M17.8 15A7 7 0 0 1 5.5 17" /></>,
  textColor: <><path d="M7 18 12 5l5 13M9 14h6" /><path d="M4 20h16" /></>,
  undo: <><path d="M5 8v5h5" /><path d="M5 13a7 7 0 1 1 2 4" /></>,
  underline: <><path d="M7 5v6a5 5 0 0 0 10 0V5M5 20h14" /></>,
  upload: <><path d="M12 20V9m-4 4 4-4 4 4" /><path d="M5 4h14" /></>,
}

export function UiIcon({ name, className, size = 16, title }: UiIconProps) {
  return <svg className={`ui-icon${className ? ` ${className}` : ''}`} viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" focusable="false" aria-hidden={title ? undefined : true} role={title ? 'img' : undefined}>{title && <title>{title}</title>}{paths[name]}</svg>
}
