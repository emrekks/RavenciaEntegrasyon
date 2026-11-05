import { useState } from 'react'
import { UiIcon } from '../../shared/components'
import { RrImagePreview } from '../marketplace/returns-ui'

// The product photo of a log row, 2:3 like the product cards; pressing it opens the photo large.
export function ProductThumb({ url, title }: { url: string | null | undefined; title: string }) {
  const [broken, setBroken] = useState(false)
  const [open, setOpen] = useState(false)
  if (!url || broken) return <span className="psl-product-placeholder" aria-hidden="true"><UiIcon name="image" size={16} /></span>
  return <>
    <button type="button" className="psl-thumb" aria-label={`${title} görselini büyüt`} title="Büyüt" onClick={() => setOpen(true)}>
      <img src={url} alt="" loading="lazy" onError={() => setBroken(true)} />
    </button>
    {open && <RrImagePreview title={title} sources={[url]} onClose={() => setOpen(false)} />}
  </>
}
