import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { addToast, dismissToasts, isPersistentToast, type ToastItem } from '../toast-stack'
import { NoticeToast } from './NoticeToast'
import { ToastStackContext, ToastViewportContext, type ToastStackApi } from './toast-stack-context'

function StackedToast({ item, onDismiss }: { item: ToastItem; onDismiss: (id: string) => void }) {
  const persistent = isPersistentToast(item)
  const { id, durationMs } = item
  useEffect(() => {
    if (persistent) return
    const timeout = window.setTimeout(() => onDismiss(id), durationMs)
    return () => window.clearTimeout(timeout)
  }, [id, durationMs, persistent, onDismiss])
  return <NoticeToast
    kind={item.kind}
    title={item.title}
    message={item.message}
    lines={item.lines}
    onClose={() => onDismiss(id)}
    durationMs={persistent ? undefined : durationMs}
  />
}

// The top-right column of the shell: a new toast lands below the ones already shown, each keeps its own timer and close
// button. Toasts a page still renders itself (NoticeToast, ErrorToast) are drawn into the same column.
export function ToastStackProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<readonly ToastItem[]>([])
  const [viewport, setViewport] = useState<HTMLDivElement | null>(null)
  const nextId = useRef(0)
  const api = useMemo<ToastStackApi>(() => ({
    show: input => setItems(current => addToast(current, input, () => `toast-${++nextId.current}`)),
    dismiss: id => setItems(current => dismissToasts(current, item => item.id === id)),
    dismissKey: key => setItems(current => dismissToasts(current, item => item.key === key)),
    dismissWhere: test => setItems(current => dismissToasts(current, test))
  }), [])
  return <ToastStackContext.Provider value={api}>
    <ToastViewportContext.Provider value={viewport}>{children}</ToastViewportContext.Provider>
    <div ref={setViewport} className="app-toast-stack">
      {items.map(item => <StackedToast key={`${item.id}:${item.version}`} item={item} onDismiss={api.dismiss} />)}
    </div>
  </ToastStackContext.Provider>
}
