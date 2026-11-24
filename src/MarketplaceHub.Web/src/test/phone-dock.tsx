import type { ReactNode } from 'react'
import { useDockHostState } from '../app/BottomDock'
import { DockContext } from '../shared/components/dock-actions'

// A phone's dock for a page test: the page's StickyActionBar buttons land in `.rv-taskbar-actions`, as in the live app.
export function PhoneDock({ children }: { children: ReactNode }) {
  const dock = useDockHostState(true)
  return <DockContext.Provider value={dock.host}>
    <div className="rv-taskbar-actions" ref={dock.setActionsEl} />
    {children}
  </DockContext.Provider>
}
export const taskBarButtons = (root: ParentNode) => Array.from(root.querySelectorAll<HTMLButtonElement>('.rv-taskbar-actions button')).map(button => button.textContent?.trim() ?? '')
