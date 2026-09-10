import type { ReactNode } from 'react'

export type UiIconName =
  | 'alert'
  | 'addCategory'
  | 'alignCenter'
  | 'alignLeft'
  | 'alignRight'
  | 'arrowLeft'
  | 'arrowRight'
  | 'barcode'
  | 'bell'
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
  | 'copy'
  | 'download'
  | 'import'
  | 'edit'
  | 'eye'
  | 'externalLink'
  | 'filter'
  | 'grid'
  | 'help'
  | 'image'
  | 'italic'
  | 'layout'
  | 'layers'
  | 'link'
  | 'list'
  | 'listSortAscending'
  | 'lock'
  | 'mail'
  | 'loader'
  | 'moreVertical'
  | 'paragraph'
  | 'plus'
  | 'redo'
  | 'refresh'
  | 'save'
  | 'search'
  | 'shield'
  | 'slidersHorizontal'
  | 'sparkle'
  | 'sync'
  | 'trash'
  | 'textColor'
  | 'undo'
  | 'underline'
  | 'upload'
  | 'dashboard'
  | 'products'
  | 'orders'
  | 'returns'
  | 'jobs'
  | 'settings'
  | 'platforms'
  | 'mappings'
  | 'logout'
  | 'pin'
  | 'pendingOrders'
  | 'lateOrders'
  | 'todayOrders'
  | 'monthOrders'
  | 'pendingReturns'
  | 'invoiceDue'
  | 'invoicePending'
  | 'stock'
  | 'bag'
  | 'box'
  | 'chart'
  | 'truck'
  | 'invoice'
  | 'connect'
  | 'arrowUp'
  | 'trend'
  | 'circleCheck'
  | 'clock'
  | 'users'
  | 'spark'
  | 'bolt'
  | 'globe'
  | 'chevrons'
  | 'menu'
  | 'moon'
  | 'sun'
  | 'monitor'
  | 'card'
  | 'cart'
  | 'shirt'
  | 'shoe'
  | 'watch'
  | 'headphones'
  | 'coffee'
  | 'play'
  | 'pause'
  | 'target'
  | 'warehouse'
  | 'equal'

type UiIconProps = {
  name: UiIconName
  className?: string
  size?: number
  title?: string
}

const paths: Record<UiIconName, ReactNode> = {
  alert: <><circle cx="12" cy="12" r="9" /><path d="M12 7.5v5m0 3.25h.01" /></>,
  addCategory: <path fill="currentColor" stroke="none" fillRule="evenodd" clipRule="evenodd" d="M3.75 4.5 4.5 3.75h6l.75.75v6l-.75.75h-6l-.75-.75v-6Zm1.5.75v4.5h4.5v-4.5h-4.5Zm8.25-1.5-.75.75v6l.75.75h6l.75-.75v-6l-.75-.75h-6Zm.75 1.5h4.5v4.5h-4.5v-4.5Zm3 15h-1.5v-3h-3v-1.5h3v-3h1.5v3h3v1.5h-3v3Zm-12.75-7.5-.75.75v6l.75.75h6l.75-.75v-6l-.75-.75h-6Zm.75 1.5h4.5v4.5h-4.5v-4.5Z" />,
  alignCenter: <><path d="M5 6h14M8 10h8M5 14h14M8 18h8" /></>,
  alignLeft: <><path d="M5 6h14M5 10h10M5 14h14M5 18h10" /></>,
  alignRight: <><path d="M5 6h14M9 10h10M5 14h14M9 18h10" /></>,
  arrowLeft: <path d="M19 12H5m6-6-6 6 6 6" />,
  arrowRight: <path d="M5 12h14m-6-6 6 6-6 6" />,
  barcode: <><path d="M4 5v14m3-14v14m3-11v8m4-11v14m3-14v14m3-11v8" /><path d="M3 5h18M3 19h18" opacity=".35" /></>,
  bell: <><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9" /><path d="M10 21h4" /></>,
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
  copy: <><rect x="8" y="8" width="11" height="11" rx="2" /><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" /></>,
  download: <><path d="M12 15V3" /><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="m7 10 5 5 5-5" /></>,
  import: <><path d="M12 3v12" /><path d="m8 11 4 4 4-4" /><path d="M8 5H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4" /></>,
  edit: <><path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z" /><path d="m15 5 4 4" /></>,
  eye: <><path d="M2.5 12s3.3-6 9.5-6 9.5 6 9.5 6-3.3 6-9.5 6-9.5-6-9.5-6Z" /><circle cx="12" cy="12" r="2.8" /></>,
  externalLink: <><path d="M14 5h5v5m0-5-8 8" /><path d="M19 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" /></>,
  filter: <path d="M4 6h16M7 12h10m-7 6h4" />,
  grid: <><rect x="4" y="4" width="6" height="6" rx="1" /><rect x="14" y="4" width="6" height="6" rx="1" /><rect x="4" y="14" width="6" height="6" rx="1" /><rect x="14" y="14" width="6" height="6" rx="1" /></>,
  help: <><circle cx="12" cy="12" r="9" /><path d="M9.75 9a2.3 2.3 0 1 1 3.8 1.75c-.95.8-1.55 1.2-1.55 2.5M12 16.5h.01" /></>,
  image: <><rect x="4" y="5" width="16" height="14" rx="2" /><circle cx="9" cy="10" r="1.2" /><path d="m5 17 4.5-4 3 2.5 2.5-2 4 3.5" /></>,
  italic: <path d="M10 5h8M6 19h8M14 5 10 19" />,
  layout: <><rect x="4" y="4" width="16" height="16" rx="2" /><path d="M4 10h16M10 10v10" /></>,
  layers: <><path d="m12 4 8 4-8 4-8-4 8-4Z" /><path d="m4 12 8 4 8-4M4 16l8 4 8-4" /></>,
  link: <><path d="m9.5 14.5 5-5" /><path d="m7 17-1.2 1.2a3.3 3.3 0 0 1-4.7-4.7l3.4-3.4a3.3 3.3 0 0 1 4.7 0M17 7l1.2-1.2a3.3 3.3 0 0 1 4.7 4.7l-3.4 3.4a3.3 3.3 0 0 1-4.7 0" /></>,
  list: <><path d="M9 6h11M9 12h11M9 18h11" /><path d="M4 6h.01M4 12h.01M4 18h.01" /></>,
  listSortAscending: <><path d="M3 19h18" /><path d="M15 12H3" /><path d="M9 5H3" /></>,
  lock: <><rect x="5" y="10" width="14" height="11" rx="2" /><path d="M8 10V7a4 4 0 0 1 8 0v3" /></>,
  mail: <><rect x="3.5" y="5" width="17" height="14" rx="2" /><path d="m4 7 8 6 8-6" /></>,
  loader: <path d="M12 4a8 8 0 1 0 8 8" />,
  moreVertical: <><circle cx="12" cy="5" r="1.6" fill="currentColor" stroke="none" /><circle cx="12" cy="12" r="1.6" fill="currentColor" stroke="none" /><circle cx="12" cy="19" r="1.6" fill="currentColor" stroke="none" /></>,
  paragraph: <><path d="M5 5h10a4 4 0 0 1 0 8H9" /><path d="M9 5v14M13 5v14" /></>,
  plus: <path d="M12 5v14M5 12h14" />,
  redo: <><path d="M19 8v5h-5" /><path d="M19 13a7 7 0 1 0-2 4" /></>,
  refresh: <><path d="M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" /><path d="M3 3v5h5" /><path d="M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16" /><path d="M16 16h5v5" /></>,
  save: <><path d="M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z" /><path d="M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7" /><path d="M7 3v4a1 1 0 0 0 1 1h7" /></>,
  search: <><circle cx="10.8" cy="10.8" r="6.3" /><path d="m16 16 4 4" /></>,
  shield: <><path d="M12 3 5 6v5c0 5 3 8 7 10 4-2 7-5 7-10V6l-7-3Z" /><path d="m9 12 2 2 4-5" /></>,
  slidersHorizontal: <><path d="M10 5H3" /><path d="M12 19H3" /><path d="M14 3v4" /><path d="M16 17v4" /><path d="M21 12h-9" /><path d="M21 19h-5" /><path d="M21 5h-7" /><path d="M8 10v4" /><path d="M8 12H3" /></>,
  sparkle: <path d="m12 3 1.5 6.5L20 12l-6.5 1.5L12 20l-1.5-6.5L4 12l6.5-2.5L12 3Z" />,
  sync: <><path d="M20 7v5h-5" /><path d="M4 17v-5h5" /><path d="M6.2 9A7 7 0 0 1 18.5 7M17.8 15A7 7 0 0 1 5.5 17" /></>,
  trash: <><path d="M10 11v6" /><path d="M14 11v6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" /><path d="M3 6h18" /><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>,
  textColor: <><path d="M7 18 12 5l5 13M9 14h6" /><path d="M4 20h16" /></>,
  undo: <><path d="M5 8v5h5" /><path d="M5 13a7 7 0 1 1 2 4" /></>,
  underline: <><path d="M7 5v6a5 5 0 0 0 10 0V5M5 20h14" /></>,
  upload: <><path d="M12 20V9m-4 4 4-4 4 4" /><path d="M5 4h14" /></>,
  dashboard: <><rect x="3.5" y="3.5" width="7" height="7" rx="1.8" /><rect x="13.5" y="3.5" width="7" height="7" rx="1.8" /><rect x="3.5" y="13.5" width="7" height="7" rx="1.8" /><rect x="13.5" y="13.5" width="7" height="7" rx="1.8" /></>,
  products: <><path d="M6 8h12l1 12H5L6 8Z" /><path d="M9 8V6a3 3 0 0 1 6 0v2" /><path d="M9 12h6" /></>,
  orders: <><path d="M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z" /><path d="M12 22V12" /><path d="m3.29 7 8.71 5 8.71-5" /><path d="m7.5 4.27 9 5.15" /></>,
  returns: <g transform="scale(0.0234375)" fill="currentColor" stroke="none"><path d="M488.006 999.898C218.982 999.898 0.106 781.022 0.106 512c0-269.022 218.876-487.898 487.9-487.898 156.624 0 304.81 76.03 396.386 203.364a8.016 8.016 0 0 1-1.828 11.17c-3.61 2.562-8.592 1.766-11.17-1.828C782.818 113.63 639.49 40.1 488.006 40.1 227.792 40.098 16.104 251.788 16.104 512c0 260.21 211.69 471.902 471.902 471.902 260.21 0 471.902-211.692 471.902-471.902a7.992 7.992 0 0 1 7.998-7.998 7.994 7.994 0 0 1 7.998 7.998c0 269.022-218.876 487.898-487.898 487.898z" /><path d="M1015.896 567.988a7.976 7.976 0 0 1-5.656-2.344l-47.99-47.99a7.996 7.996 0 1 1 11.31-11.31l47.99 47.992a7.994 7.994 0 0 1 0 11.308 7.96 7.96 0 0 1-5.654 2.344z" /><path d="M919.916 567.988a7.994 7.994 0 0 1-5.656-13.652l47.99-47.992a7.996 7.996 0 1 1 11.31 11.31l-47.99 47.99a7.96 7.96 0 0 1-5.654 2.344zM727.956 735.954H280.048c-4.422 0-8-3.578-8-7.998V376.028a7.994 7.994 0 0 1 8-7.998h447.906c4.422 0 8 3.578 8 7.998v351.928a7.994 7.994 0 0 1-7.998 7.998z m-439.91-15.996h431.91V384.026H288.046v335.932z" /><path d="M727.956 384.026H280.048a8.002 8.002 0 0 1-7.39-4.936 8.018 8.018 0 0 1 1.734-8.716l63.988-63.986a7.992 7.992 0 0 1 5.656-2.344h319.934c2.124 0 4.154.844 5.654 2.344l63.988 63.986a8.02 8.02 0 0 1 1.734 8.716 8.002 8.002 0 0 1-7.39 4.936z m-428.598-15.996h409.29l-47.99-47.99H347.346l-47.988 47.99z" /><path d="M456.012 384.026a8.008 8.008 0 0 1-7.156-11.576l31.994-63.988c2-3.936 6.78-5.546 10.732-3.578a8.006 8.006 0 0 1 3.578 10.732l-31.994 63.986a8.014 8.014 0 0 1-7.154 4.424zM551.992 384.026a8.016 8.016 0 0 1-7.154-4.42l-31.994-63.986a8.006 8.006 0 0 1 3.578-10.732 7.992 7.992 0 0 1 10.732 3.578l31.992 63.988a8.006 8.006 0 0 1-3.578 10.732 7.992 7.992 0 0 1-3.576.84z" /><path d="M456.012 448.014a7.994 7.994 0 0 1-7.998-8v-63.988c0-4.42 3.578-7.998 7.998-7.998s7.998 3.578 7.998 7.998v63.988c0 4.422-3.578 8-7.998 8zM551.992 448.014h-95.98c-4.42 0-7.998-3.578-7.998-8s3.578-7.998 7.998-7.998h95.98c4.42 0 7.998 3.578 7.998 7.998s-3.578 8-7.998 8zM551.992 456.012a7.994 7.994 0 0 1-7.998-7.998v-8a7.994 7.994 0 0 1 7.998-7.998 7.994 7.994 0 0 1 7.998 7.998v8a7.994 7.994 0 0 1-7.998 7.998zM456.012 456.012a7.994 7.994 0 0 1-7.998-7.998v-8c0-4.42 3.578-7.998 7.998-7.998s7.998 3.578 7.998 7.998v8a7.994 7.994 0 0 1-7.998 7.998zM488.006 456.012c-4.422 0-8-3.578-8-7.998v-8a7.994 7.994 0 0 1 8-7.998 7.994 7.994 0 0 1 7.998 7.998v8a7.994 7.994 0 0 1-7.998 7.998zM519.998 456.012a7.994 7.994 0 0 1-7.998-7.998v-8a7.994 7.994 0 0 1 7.998-7.998c4.422 0 8 3.578 8 7.998v8a7.994 7.994 0 0 1-8 7.998z" /><path d="M695.962 703.96h-31.994a7.994 7.994 0 0 1-7.998-8v-31.992c0-4.422 3.578-8 7.998-8h31.994a7.994 7.994 0 0 1 7.998 8v31.992a7.994 7.994 0 0 1-7.998 7.998z m-23.996-15.998h15.996v-15.996h-15.996v15.996zM631.974 703.96h-31.992a7.994 7.994 0 0 1-7.998-8v-31.992c0-4.422 3.576-8 7.998-8h31.992c4.422 0 8 3.578 8 8v31.992c0 4.422-3.578 8-8 8z m-23.994-15.998h15.996v-15.996h-15.996v15.996zM472.008 671.966H312.04a7.994 7.994 0 0 1-7.998-7.998c0-4.422 3.578-8 7.998-8h159.968a7.994 7.994 0 0 1 7.998 8 7.994 7.994 0 0 1-7.998 7.998zM392.026 703.96h-79.984a7.994 7.994 0 0 1-7.998-8 7.994 7.994 0 0 1 7.998-7.998h79.984a7.994 7.994 0 0 1 7.998 7.998c0 4.422-3.578 8-7.998 7.998z" /></g>,
  jobs: <><path d="M21 11.693V5" /><path d="m22 22-1.875-1.875" /><path d="M3 12a9 3 0 0 0 8.697 2.998" /><path d="M3 5v14a9 3 0 0 0 9.28 2.999" /><circle cx="18" cy="18" r="3" /><ellipse cx="12" cy="5" rx="9" ry="3" /></>,
  settings: <><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" /><circle cx="12" cy="12" r="3" /></>,
  platforms: <><rect x="3.5" y="4" width="7" height="7" rx="1.8" /><rect x="13.5" y="13" width="7" height="7" rx="1.8" /><path d="M10.5 7.5h2a4 4 0 0 1 4 4V13M14 10l2.5 2.5L19 10" /></>,
  mappings: <><circle cx="6" cy="6" r="3" /><circle cx="18" cy="18" r="3" /><path d="m8.5 8.5 7 7M18 9V6h-3" /></>,
  logout: <><path d="M9 3.5H5.5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2H9M15 16l4-4-4-4M19 12H9" /></>,
  pin: <><path d="M8 3.5h8M9 3.5v5l-3 3v2h12v-2l-3-3v-5M12 13.5v7" /></>,
  pendingOrders: <><rect x="4" y="5" width="16" height="15" rx="2" /><path d="M8 3v4M16 3v4M4 10h16M8 14h.01M12 14h.01M16 14h.01M8 17h.01M12 17h.01" /></>,
  lateOrders: <><path d="m12 3.5 9 17H3l9-17Z" /><path d="M12 9v5M12 17.5h.01" /></>,
  todayOrders: <><rect x="4" y="5" width="16" height="15" rx="2" /><path d="M8 3v4M16 3v4M4 10h16M8 14h3M8 17h5" /></>,
  monthOrders: <><rect x="4" y="5" width="16" height="15" rx="2" /><path d="M8 3v4M16 3v4M4 10h16M8 14h8M8 17h5" /></>,
  pendingReturns: <><path d="M9 8H4v5" /><path d="M4 13a8 8 0 1 0 2.1-5.3" /><path d="M13 16h6M16 13l3 3-3 3" /></>,
  invoiceDue: <><path d="M6 3.5h9l3 3v14H6zM15 3.5v4h4M9 12h6" /><circle cx="16.5" cy="17" r="3.5" /><path d="M16.5 15.5V17l1 1" /></>,
  invoicePending: <><path d="M6 3.5h12v18l-6-2-6 2V3.5Z" /><path d="M9 8h6M9 12h4" /><path d="M15.5 17h.01" /></>,
  stock: <><path d="m4 8 8-4 8 4-8 4-8-4Z" /><path d="M4 8v8l8 4 8-4V8M12 12v8" /></>,
  bag: <><path d="M5 7h14l1 14H4L5 7Z" /><path d="M8 8V6a4 4 0 0 1 8 0v2" /></>,
  box: <><path d="m12 3 9 5-9 5-9-5 9-5Zm-9 5v10l9 5 9-5V8M12 13v10M7.5 5.5l9 5" /></>,
  chart: <path d="M4 4v16h17M8 15l4-5 4 2 5-7" />,
  truck: <><path d="M3 5h11v12H3zM14 9h4l3 4v4h-7" /><circle cx="7" cy="18" r="2" /><circle cx="17" cy="18" r="2" /></>,
  invoice: <><path d="M5 3h10l4 4v14l-3-2-3 2-3-2-3 2-2-1V3Z" /><path d="M14 3v5h5M9 12h6M9 16h4" /></>,
  connect: <><path d="M17 19a1 1 0 0 1-1-1v-2a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2a1 1 0 0 1-1 1z" /><path d="M17 21v-2" /><path d="M19 14V6.5a1 1 0 0 0-7 0v11a1 1 0 0 1-7 0V10" /><path d="M21 21v-2" /><path d="M3 5V3" /><path d="M4 10a2 2 0 0 1-2-2V6a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2a2 2 0 0 1-2 2z" /><path d="M7 5V3" /></>,
  arrowUp: <path d="m6 16 12-12M6 4h12v12" />,
  trend: <path d="m3 17 6-6 4 3 8-10M15 4h6v6" />,
  circleCheck: <><circle cx="12" cy="12" r="9" /><path d="m8 12 3 3 5-6" /></>,
  clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
  users: <><circle cx="9" cy="8" r="3" /><path d="M2 21v-3a7 7 0 0 1 14 0v3M16 5a3 3 0 0 1 0 6m2 4a5 5 0 0 1 4 5" /></>,
  spark: <path d="m12 2 3 7 7 3-7 3-3 7-3-7-7-3 7-3 3-7Z" />,
  bolt: <path d="m13 2-9 12h7l-1 8 10-13h-8l1-7Z" />,
  globe: <><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3a20 20 0 0 1 0 18 20 20 0 0 1 0-18Z" /></>,
  chevrons: <path d="m8 8 4-4 4 4M8 16l4 4 4-4" />,
  menu: <path d="M4 6h16M4 12h16M4 18h16" />,
  moon: <path d="M20 15a9 9 0 0 1-11-12 9 9 0 1 0 11 12Z" />,
  sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1 1m12 12 1 1M5 19l1-1M18 6l1-1" /></>,
  monitor: <><rect x="2" y="3" width="20" height="14" rx="2" /><path d="M8 21h8m-4-4v4" /></>,
  card: <><rect x="2" y="4" width="20" height="16" rx="3" /><path d="M2 9h20M6 15h4" /></>,
  cart: <path d="M6.29977 5H21L19 12H7.37671M20 16H8L6 3H3M9 20C9 20.5523 8.55228 21 8 21C7.44772 21 7 20.5523 7 20C7 19.4477 7.44772 19 8 19C8.55228 19 9 19.4477 9 20ZM20 20C20 20.5523 19.5523 21 19 21C18.4477 21 18 20.5523 18 20C18 19.4477 18.4477 19 19 19C19.5523 19 20 19.4477 20 20Z" />,
  shirt: <path d="m8 3-6 4 3 6 3-2v10h8V11l3 2 3-6-6-4a4 4 0 0 1-8 0Z" />,
  shoe: <><path d="M3 9v9h18v-4l-8-3-3-6-3 4H3ZM3 18v3h18v-3M13 11l-1 3m4-2-1 3" /></>,
  watch: <><rect x="6" y="6" width="12" height="12" rx="4" /><path d="M9 6V2h6v4M9 18v4h6v-4M12 9v3l2 1" /></>,
  headphones: <path d="M3 14v-3a9 9 0 0 1 18 0v3M3 12h4v8H3v-8Zm14 0h4v8h-4v-8Z" />,
  coffee: <><path d="M4 8h12v10a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V8Zm12 1h2a3 3 0 0 1 0 6h-2M7 2v3m5-3v3" /></>,
  play: <path d="m8 4 12 8-12 8V4Z" />,
  pause: <path d="M8 4v16M16 4v16" />,
  target: <><circle cx="12" cy="12" r="9" /><circle cx="12" cy="12" r="5" /><circle cx="12" cy="12" r="1" /></>,
  warehouse: <><path d="M18 21V10a1 1 0 0 0-1-1H7a1 1 0 0 0-1 1v11" /><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 1.132-1.803l7.95-3.974a2 2 0 0 1 1.837 0l7.948 3.974A2 2 0 0 1 22 8z" /><path d="M6 13h12" /><path d="M6 17h12" /></>,
  equal: <><line x1="5" x2="19" y1="9" y2="9" /><line x1="5" x2="19" y1="15" y2="15" /></>,
}

export function UiIcon({ name, className, size = 16, title }: UiIconProps) {
  return <svg className={`ui-icon${className ? ` ${className}` : ''}`} viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" vectorEffect="non-scaling-stroke" shapeRendering="geometricPrecision" preserveAspectRatio="xMidYMid meet" focusable="false" aria-hidden={title ? undefined : true} role={title ? 'img' : undefined}>{title && <title>{title}</title>}{paths[name]}</svg>
}
