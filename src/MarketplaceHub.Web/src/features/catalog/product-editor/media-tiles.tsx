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

export function LocalImagePreview({ file, alt, caption: _caption, onRemove, onZoom, draggable = false, isDragOver = false, onDragStart, onDragOver, onDrop, onDragEnd, mediaIndex }: { file: File, alt: string, caption: string, mediaIndex?: number, onRemove?: () => void, onZoom?: (url: string) => void, draggable?: boolean, isDragOver?: boolean, onDragStart?: (event: DragEvent<HTMLElement>) => void, onDragOver?: (event: DragEvent<HTMLElement>) => void, onDrop?: (event: DragEvent<HTMLElement>) => void, onDragEnd?: () => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  return <figure data-media-kind={mediaIndex === undefined ? undefined : 'file'} data-media-index={mediaIndex} className={`image-preview-card${draggable ? ' media-sortable' : ''}${isDragOver ? ' is-media-drag-over' : ''}`} draggable={draggable} onDragStart={onDragStart} onDragOver={onDragOver} onDrop={onDrop} onDragEnd={onDragEnd}><img src={url} alt={alt} className="media-tile-image" draggable={false} title={draggable ? 'Sıralamak için sürükleyin' : undefined} />{onZoom && url && <MediaZoomButton onZoom={() => onZoom(url)} />}{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}><UiIcon name="close" /></button>}</figure>;
}
