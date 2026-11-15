// 4 Görseller: the model's one picture strip (own, family and new pictures), its size chart and its actions.
import { type DragEvent } from 'react'
import { TitleHint, UiIcon } from '../../../shared/components'
import { mediaImageKey, mediaStripOwnPosition } from '../product-media-editor'
import { LocalImagePreview, MediaAddTile, MediaMoveButtons, MediaZoomButton } from './media-tiles'
import { isSizeChartRef } from './size-chart-order'
import { thumbnailSrcSet, thumbnailUrl } from '../../../shared/product-thumbnail'
import type { EditorContext } from './editor-context'

export function EditorMediaSection({ editor }: { editor: EditorContext }) {
  const { editProductId, submitting, form, variantRows, setLightboxImage, mediaFiles, deletingProductMediaKey, draggedMedia, setDraggedMedia, dragOverMedia, setDragOverMedia, sizeChartUrl, setSizeChartPicking, chartKey, mediaFileInputRef, touchPointer, mediaTouch, suppressMediaClick, mediaStripRef, mediaUrls, familyMediaItems, draftMediaKeys, mediaStrip, sizeChartPickable, pickingSizeChart, familyOnlyMediaItems, pictureColourCaption, hasMediaTiles, mediaUploadHint, handleMediaFiles, markSizeChart, reorderMedia, startMediaTouch, moveMediaTouch, endMediaTouch, cancelMediaTouch, removeUploadedMedia, clearAllMedia, removeProductMedia, removeFamilyMedia, mediaFileInput } = editor
  return <>
      <section className="panel product-step-card product-media-card">
        <div className="editor-section-title">
          <span>4</span>
            <div><h2>Görseller<TitleHint label="Görseller">JPEG veya PNG yükleyin, sürükleyerek sıralayın. Modelin tüm renklerinin görselleri tek galeride görünür. Kaydet her görseli Seçeneklere Görsel Ata ile atandığı rengin kartına yazar; beden tablosu ve atanmamış yeni görseller her renge gider. Kutucuktaki ad görselin gideceği renktir.</TitleHint></h2></div>
            <div className="product-media-header-actions">
              {sizeChartPickable && <button type="button" className="secondary product-media-size-chart-button" aria-pressed={pickingSizeChart} disabled={submitting} title="Kayıtlı görsellerden birini (pazaryerinden gelenler ve diğer renklerinkiler dahil) beden tablosu olarak işaretleyin; her renkte son sıraya alınır." aria-label="Beden Tablosu Seç" onClick={() => setSizeChartPicking(current => !current)}><span className="media-action-full">Beden Tablosu Seç</span><span className="media-action-short" aria-hidden="true">Beden Tablosu</span></button>}
              {editProductId && sizeChartUrl && <button type="button" className="secondary product-media-size-chart-button" disabled={submitting} title="Beden tablosu işaretini kaldırın; görsel kartta kalır." aria-label="İşareti Kaldır" onClick={() => markSizeChart(null)}>İşareti Kaldır</button>}
              {(mediaUrls.length > 0 || mediaFiles.length > 0 || familyOnlyMediaItems.length > 0 || variantRows.some(row => row.mediaRefs.length > 0)) && <button type="button" className="secondary product-media-clear-all-button" aria-label="Tümünü Temizle" title="Tümünü Temizle" onClick={() => clearAllMedia()}><span className="media-action-full">Tümünü Temizle</span><span className="media-action-short" aria-hidden="true">Temizle</span></button>}
            </div>
        </div>
        {/* The wide box while there is no picture; after the first one the plus tile at the end of the strip opens the
            same picker (one input, rendered where its opener is). */}
        {!hasMediaTiles && <label className="upload-ghost-box product-media-upload">
          {mediaFileInput}
          <strong>Ürün Görsellerini Dosya Olarak Seç</strong>
          <small>{mediaUploadHint}</small>
        </label>}
        {hasMediaTiles && <span className="product-media-file-slot">{mediaFileInput}</span>}
        {pickingSizeChart && hasMediaTiles && <div className="size-chart-pick-hint" role="status"><span>Beden tablosu olacak görsele dokunun</span><button type="button" className="secondary" onClick={() => setSizeChartPicking(false)}>Vazgeç</button></div>}
        {hasMediaTiles && <div ref={mediaStripRef} className={`media-preview-strip${pickingSizeChart ? ' is-size-chart-picking' : ''}`} onPointerDown={pickingSizeChart ? undefined : startMediaTouch} onPointerMove={moveMediaTouch} onPointerUp={endMediaTouch} onPointerCancel={cancelMediaTouch} onContextMenu={event => { if (mediaTouch.current) event.preventDefault() }} onClickCapture={event => { if (suppressMediaClick.current) { event.preventDefault(); event.stopPropagation() } }}>
          {/* One ordered strip: saved pictures, the family's and new files mix; the first own picture is the main one. */}
          {mediaStrip.map((ref, index) => {
            const ownPosition = mediaStripOwnPosition(mediaStrip, index, draftMediaKeys)
            const mediaDrag = {
              onDragStart: (event: DragEvent<HTMLElement>) => { setDraggedMedia(index); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', `media:${index}`) },
              onDragOver: (event: DragEvent<HTMLElement>) => { if (draggedMedia === null) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragOverMedia(index) },
              onDrop: (event: DragEvent<HTMLElement>) => { event.preventDefault(); const transferValue = event.dataTransfer.getData('text/plain'); const transferIndex = transferValue.startsWith('media:') ? Number(transferValue.slice(6)) : draggedMedia ?? -1; if (draggedMedia !== null && Number.isInteger(transferIndex)) reorderMedia(transferIndex, index) },
              onDragEnd: () => { setDraggedMedia(null); setDragOverMedia(null) }
            }
            if (ref.startsWith('file|')) {
              const item = mediaFiles.find(file => file.id === ref.slice(5))
              return item && <LocalImagePreview key={item.id} mediaIndex={index} file={item.file} alt={`${form.title || 'Ürün'} ${index + 1}`} caption={pictureColourCaption(ref) || (ownPosition === 0 ? 'Ana görsel' : item.file.name)} draggable={!touchPointer && !submitting && !pickingSizeChart} mediaCount={mediaStrip.length} onMove={target => reorderMedia(index, target)} moveDisabled={submitting} isDragOver={dragOverMedia === index} {...mediaDrag} onRemove={() => removeUploadedMedia(item.id)} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli', modelCode: form.modelCode })} />
            }
            const url = ref.slice(4)
            const ownedUrl = mediaUrls.find(current => mediaImageKey(current) === mediaImageKey(url))
            const familyItem = familyMediaItems.find(item => mediaImageKey(item.url) === mediaImageKey(url))
            const isProductMedia = ownedUrl !== undefined
            const deletingProductMedia = deletingProductMediaKey === mediaImageKey(url)
            const isSizeChart = isSizeChartRef(ref, chartKey)
            const pickable = pickingSizeChart
            return <figure key={`family-${mediaImageKey(url)}`} data-media-kind="url" data-media-index={index} data-size-chart={isSizeChart || undefined} className={`image-preview-card media-sortable${isProductMedia ? '' : ' family-media-preview'}${pickable ? ' is-size-chart-pickable' : ''} ${dragOverMedia === index ? 'is-media-drag-over' : ''}`} draggable={!touchPointer && !submitting && !deletingProductMediaKey && !isSizeChart && !pickingSizeChart} {...mediaDrag}>
              <img src={thumbnailUrl(url)} srcSet={thumbnailSrcSet(url)} sizes="88px" alt={`${form.title || 'Ürün'} ${index + 1}`} className="media-tile-image" draggable={false} />
              {isSizeChart && <span className="size-chart-badge">Beden Tablosu</span>}
              {pickable && <button type="button" className="size-chart-pick-target" aria-label={isSizeChart ? 'Bu görsel zaten beden tablosu' : 'Bu görseli beden tablosu olarak işaretle'} onClick={event => { event.stopPropagation(); if (isSizeChart) setSizeChartPicking(false); else markSizeChart(ownedUrl ?? url) }} />}
              <MediaZoomButton onZoom={() => setLightboxImage({ url, title: isProductMedia ? form.title || 'Ürün Görseli' : `${form.title || 'Ürün'} · Renk Ailesi`, modelCode: form.modelCode })} />
              {isProductMedia
                ? <button type="button" className="image-remove-btn" disabled={deletingProductMedia || submitting} title="Görseli modelin tüm renklerinden hemen kaldır" aria-label="Görseli modelin tüm renklerinden hemen kaldır" onClick={event => { event.stopPropagation(); if (ownedUrl) void removeProductMedia(ownedUrl) }}><UiIcon name={deletingProductMedia ? 'loader' : 'close'} /></button>
                : <button type="button" className="image-remove-btn" disabled={!familyItem || submitting} title="Renk ailesi görselini kaldır; Kaydet’te diğer renk ürünlerinden de kaldırılır" aria-label="Renk ailesi görselini kaldır" onClick={event => { event.stopPropagation(); if (familyItem) removeFamilyMedia(familyItem) }}><UiIcon name="close" /></button>}
              <MediaMoveButtons index={index} count={mediaStrip.length} onMove={target => reorderMedia(index, target)} disabled={submitting || Boolean(deletingProductMediaKey)} />
              <figcaption>{pictureColourCaption(ref) || (isProductMedia ? (ownPosition === 0 ? 'Ana görsel' : `${ownPosition + 1}. görsel`) : 'Renk varyantı görseli')}<span className="media-drag-hint"> · sürükle</span></figcaption>
            </figure>
          })}
          <MediaAddTile hint={mediaUploadHint} disabled={submitting} onOpen={() => mediaFileInputRef.current?.click()} onFiles={handleMediaFiles} />
        </div>}
      </section>
  </>
}
