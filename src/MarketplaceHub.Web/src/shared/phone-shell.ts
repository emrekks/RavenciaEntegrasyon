// The phone shell (the dock, the task bar, the full-screen menu, the top bar without its hamburger, the shell's own way back)
// is drawn up to this width. Wider is a tablet: the sidebar or the drawer and the page's own top bar. Page layouts already
// use the same 760 px for their phone rules (phone-spacing.css, products-responsive.css...). The CSS of the shell (app/bottom-nav.css)
// opens its @media with the same number; a test keeps the two equal.
export const phoneShellMaxWidth = 760
export const phoneShellQuery = `(max-width: ${phoneShellMaxWidth}px)`
