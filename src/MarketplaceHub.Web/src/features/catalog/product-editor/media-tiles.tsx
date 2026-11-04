// The product editor's picture tiles: sorted by dragging (mouse) or by holding and sliding (phones), zoomed only with the
// corner zoom button; clicking the picture does nothing, so it never gets in the way of a drag.
import { useEffect, useState, type DragEvent } from 'react'
import { UiIcon } from '../../../shared/components'

// The last tile of the strip once there is a picture: a dashed frame the size of a picture with a plus, which opens the
// same file picker; files dropped on it are added too. It is not part of the drag order and always stays last.
export function MediaAddTile({ onOpen, onFiles, disabled = false, hint }: { onOpen: () => void; onFiles: (files: File[]) => void; disabled?: boolean; hint: string }) {
  const [fileOver, setFileOver] = useState(false)
  const carriesFiles = (event: DragEvent<HTMLElement>) => Array.from(event.dataTransfer?.types ?? []).includes('Files')
  return <button type="button" className={`media-add-tile${fileOver ? ' is-file-over' : ''}`} disabled={disabled} title={hint} aria-label={`Görsel Ekle. ${hint}`}
    onClick={onOpen}
    onDragOver={event => { if (disabled || !carriesFiles(event)) return; event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; setFileOver(true) }}
    onDragLeave={() => setFileOver(false)}
    onDrop={event => { if (disabled || !carriesFiles(event)) return; event.preventDefault(); setFileOver(false); const files = Array.from(event.dataTransfer.files ?? []); if (files.length) onFiles(files) }}>
    <UiIcon name="plus" />
    <span>Görsel Ekle</span>
  </button>
}

// The only way to the zoom: a small corner button, quiet until the tile is hovered or focused, always shown on touch
// screens. The picture itself stays a plain image so the whole tile remains a drag handle (Firefox starts no drag from a
// button, so a button over the whole picture would stop drag-sorting there).
export function MediaZoomButton({ onZoom }: { onZoom: () => void }) {
  return <button type="button" className="image-zoom-btn" aria-label="Görseli büyüt" title="Görseli büyüt" onClick={event => { event.stopPropagation(); onZoom() }}><UiIcon name="search" /></button>
}

// Phones: dragging is unreliable there, so every tile gets a left and a right arrow under it that move the picture one
// place with the same reorder the drag uses; the arrows are hidden on wider screens (CSS) and off at the strip's ends.
export function MediaMoveButtons({ index, count, onMove, disabled = false }: { index: number; count: number; onMove: (targetIndex: number) => void; disabled?: boolean }) {
  return <div className="media-move-buttons" role="group" aria-label="Görsel sırası">
    <button type="button" className="media-move-btn" aria-label="Görseli sola taşı" title="Görseli sola taşı" disabled={disabled || index <= 0} onClick={event => { event.stopPropagation(); onMove(index - 1) }}><UiIcon name="chevronLeft" size={14} /></button>
    <button type="button" className="media-move-btn" aria-label="Görseli sağa taşı" title="Görseli sağa taşı" disabled={disabled || index >= count - 1} onClick={event => { event.stopPropagation(); onMove(index + 1) }}><UiIcon name="chevronRight" size={14} /></button>
  </div>
}

export function LocalImagePreview({ file, alt, caption: _caption, onRemove, onZoom, draggable = false, isDragOver = false, onDragStart, onDragOver, onDrop, onDragEnd, mediaIndex, mediaCount, onMove, moveDisabled }: { file: File, alt: string, caption: string, mediaIndex?: number, mediaCount?: number, onMove?: (targetIndex: number) => void, moveDisabled?: boolean, onRemove?: () => void, onZoom?: (url: string) => void, draggable?: boolean, isDragOver?: boolean, onDragStart?: (event: DragEvent<HTMLElement>) => void, onDragOver?: (event: DragEvent<HTMLElement>) => void, onDrop?: (event: DragEvent<HTMLElement>) => void, onDragEnd?: () => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  return <figure data-media-kind={mediaIndex === undefined ? undefined : 'file'} data-media-index={mediaIndex} className={`image-preview-card${draggable ? ' media-sortable' : ''}${isDragOver ? ' is-media-drag-over' : ''}`} draggable={draggable} onDragStart={onDragStart} onDragOver={onDragOver} onDrop={onDrop} onDragEnd={onDragEnd}><img src={url} alt={alt} className="media-tile-image" draggable={false} title={draggable ? 'Sıralamak için sürükleyin' : undefined} />{onZoom && url && <MediaZoomButton onZoom={() => onZoom(url)} />}{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}><UiIcon name="close" /></button>}{onMove && mediaIndex !== undefined && mediaCount !== undefined && <MediaMoveButtons index={mediaIndex} count={mediaCount} onMove={onMove} disabled={moveDisabled} />}</figure>;
}
