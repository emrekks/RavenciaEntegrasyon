// The model code's copy button on a product row, like the order number's: a check for a moment once it is copied.
import { useEffect, useRef, useState } from 'react'
import { copyText } from '../../../shared/copy-text'
import { RrIcon } from '../../marketplace/returns-ui'

export function ModelCodeCopy({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  async function copy() {
    if (!await copyText(value)) return
    setCopied(true)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setCopied(false), 1400)
  }
  return <button type="button" className="rr-icon-btn rr-code-copy" title={copied ? 'Kopyalandı' : 'Model kodunu kopyala'}
    aria-label={copied ? `Model kodu ${value} kopyalandı` : `Model kodunu kopyala: ${value}`}
    onClick={event => { event.stopPropagation(); void copy() }}>
    <RrIcon name={copied ? 'check' : 'copy'} size={13} />
  </button>
}
