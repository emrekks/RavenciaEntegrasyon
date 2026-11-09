import { useState } from 'react'
import { UiIcon } from '../../shared/components'
import { thumbnailSources } from '../../shared/product-thumbnail'
import { RrImagePreview } from '../marketplace/returns-ui'

// The product photo of a log row, 2:3 like the product cards; pressing it opens the photo large. The frame asks for
// the small copy first and falls back to the original when that does not load.
export function ProductThumb({ url, title }: { url: string | null | undefined; title: string }) {
  const [stage, setStage] = useState(0)
  const [open, setOpen] = useState(false)
  const shown = thumbnailSources(url)[stage]
  if (!url || !shown) return <span className="psl-product-placeholder" aria-hidden="true"><UiIcon name="image" size={16} /></span>
  return <>
    <button type="button" className="psl-thumb" aria-label={`${title} görselini büyüt`} title="Büyüt" onClick={() => setOpen(true)}>
      <img key={shown} src={shown} alt="" loading="lazy" decoding="async" onError={() => setStage(value => value + 1)} />
    </button>
    {open && <RrImagePreview title={title} sources={[url]} onClose={() => setOpen(false)} />}
  </>
}
