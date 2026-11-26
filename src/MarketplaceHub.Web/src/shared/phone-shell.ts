// The phone shell (the dock, the task bar, the full-screen menu, the top bar without its hamburger, the shell's own way back)
// is drawn up to this width. Wider is a tablet: the sidebar or the drawer and the page's own top bar. Page layouts already
// use the same 760 px for their phone rules (phone-spacing.css, products-responsive.css...). The CSS of the shell (app/bottom-nav.css)
// opens its @media with the same number; a test keeps the two equal.
export const phoneShellMaxWidth = 760
export const phoneShellQuery = `(max-width: ${phoneShellMaxWidth}px)`

// A Home Screen app on iOS (standalone, black-translucent, viewport-fit=cover). There WebKit ends position: fixed layers
// above the screen's bottom edge by about the safe area while body is held still by overflow: hidden or position: fixed
// (WebKit bugs 236445, 237961, 254868): a band showed under every open window and the dock sat lifted. The page locks
// (shared/hooks/useScrollLock.ts, shared/overlay-scroll-lock.ts) therefore hold the page another way in this mode.
export const isHomeScreenApp = () => typeof window !== 'undefined'
  && (window.matchMedia?.('(display-mode: standalone)').matches === true || (window.navigator as { standalone?: boolean }).standalone === true)
