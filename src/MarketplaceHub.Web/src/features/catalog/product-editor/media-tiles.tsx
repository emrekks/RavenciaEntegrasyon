// The product editor's picture tiles: sorted by dragging (mouse), by holding and sliding (phones) or with the arrow
// buttons (phones always; wider screens while the tile has keyboard focus), zoomed by clicking the picture or with the
// corner zoom button (keyboard).
import { useEffect, useState, type DragEvent } from 'react'
import { UiIcon } from '../../../shared/components'

// Two small buttons under a picture move it one place earlier or later in the order.
export function MediaMoveButtons({ index, count, disabled = false, onMove }: { index: number; count: number; disabled?: boolean; onMove: (target: number) => void }) {
  return <span className="media-move-buttons"><button type="button" aria-label="Görseli bir öne al" disabled={disabled || index === 0} onClick={event => { event.stopPropagation(); onMove(index - 1) }}><UiIcon name="chevronLeft" /></button><button type="button" aria-label="Görseli bir sonraya al" disabled={disabled || index >= count - 1} onClick={event => { event.stopPropagation(); onMove(index + 1) }}><UiIcon name="chevronRight" /></button></span>
}

// The keyboard way to the zoom that clicking the picture opens: a small corner button, shown on hover and keyboard
// focus. The picture itself stays a plain image so the whole tile remains a drag handle (Firefox starts no drag from a
// button, so a button over the whole picture would stop drag-sorting there).
export function MediaZoomButton({ onZoom }: { onZoom: () => void }) {
  return <button type="button" className="image-zoom-btn" aria-label="Görseli büyüt" title="Görseli büyüt" onClick={event => { event.stopPropagation(); onZoom() }}><UiIcon name="search" /></button>
}

export function LocalImagePreview({ file, alt, caption: _caption, onRemove, onZoom, draggable = false, isDragOver = false, onDragStart, onDragOver, onDrop, onDragEnd, mediaIndex, mediaCount, onMove }: { file: File, alt: string, caption: string, mediaIndex?: number, mediaCount?: number, onMove?: (target: number) => void, onRemove?: () => void, onZoom?: (url: string) => void, draggable?: boolean, isDragOver?: boolean, onDragStart?: (event: DragEvent<HTMLElement>) => void, onDragOver?: (event: DragEvent<HTMLElement>) => void, onDrop?: (event: DragEvent<HTMLElement>) => void, onDragEnd?: () => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions -- mouse shortcut on a draggable tile; keyboard users have MediaZoomButton (a button over the picture would stop drag-sorting in Firefox)
  return <figure data-media-kind={mediaIndex === undefined ? undefined : 'file'} data-media-index={mediaIndex} className={`image-preview-card${draggable ? ' media-sortable' : ''}${isDragOver ? ' is-media-drag-over' : ''}`} draggable={draggable} onDragStart={onDragStart} onDragOver={onDragOver} onDrop={onDrop} onDragEnd={onDragEnd}><img src={url} alt={alt} className="clickable-thumb" draggable={false} onClick={() => onZoom?.(url)} title={draggable ? 'Sıralamak için sürükleyin; büyütmek için tıklayın' : 'Büyütmek için tıklayın'} />{onZoom && url && <MediaZoomButton onZoom={() => onZoom(url)} />}{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}><UiIcon name="close" /></button>}{onMove && mediaIndex !== undefined && mediaCount !== undefined && mediaCount > 1 && <MediaMoveButtons index={mediaIndex} count={mediaCount} onMove={onMove} />}</figure>;
}
