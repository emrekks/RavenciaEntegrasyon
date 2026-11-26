// TEMPORARY DIAGNOSTIC (remove after the standalone bottom-gap fix)
import { useCallback, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { collectSnapshot, formatSideBySide, type DiagnosticSnapshot } from './dockDiagnosticFormat'

export default function DockDiagnostic({ onClose }: { onClose: () => void }) {
  const openRef = useRef<DiagnosticSnapshot | null>(null)
  const closedRef = useRef<DiagnosticSnapshot | null>(null)
  const [text, setText] = useState('')
  const [copied, setCopied] = useState(false)
  const refresh = useCallback(() => {
    const snap = collectSnapshot(document, window)
    if (snap.menu === 'open') openRef.current = snap; else closedRef.current = snap
    setText(formatSideBySide(openRef.current, closedRef.current, snap.menu))
  }, [])
  useEffect(() => {
    refresh()
    const vv = window.visualViewport
    window.addEventListener('resize', refresh)
    vv?.addEventListener('resize', refresh)
    vv?.addEventListener('scroll', refresh)
    const observer = new MutationObserver(() => window.requestAnimationFrame(refresh))
    observer.observe(document.body, { childList: true, subtree: true })
    const timer = window.setInterval(refresh, 500)
    return () => {
      window.removeEventListener('resize', refresh)
      vv?.removeEventListener('resize', refresh)
      vv?.removeEventListener('scroll', refresh)
      observer.disconnect()
      window.clearInterval(timer)
    }
  }, [refresh])
  const copy = () => { void navigator.clipboard?.writeText(text).then(() => setCopied(true), () => setCopied(false)) }
  const button = { font: '10px ui-monospace, Menlo, monospace', minHeight: 0, height: 24, padding: '0 8px', margin: '0 4px 4px 0', color: '#000', background: '#ddd', border: '1px solid #888', borderRadius: 4 } as const
  return createPortal(<div data-rv-diagnostic="dock" style={{ position: 'fixed', top: 'env(safe-area-inset-top, 0px)', left: 0, zIndex: 2147483000, boxSizing: 'border-box', maxWidth: '90%', maxHeight: '90%', overflow: 'auto', padding: 4, color: '#0f0', background: '#000', font: '10px/1.25 ui-monospace, Menlo, monospace', userSelect: 'text', WebkitUserSelect: 'text' }}>
    <div><button type="button" style={button} onClick={copy}>{copied ? 'Kopyalandı' : 'Kopyala'}</button><button type="button" style={button} onClick={onClose}>Kapat</button></div>
    <pre style={{ margin: 0, font: 'inherit', whiteSpace: 'pre-wrap', wordBreak: 'break-all', userSelect: 'text', WebkitUserSelect: 'text' }}>{text}</pre>
  </div>, document.body)
}
