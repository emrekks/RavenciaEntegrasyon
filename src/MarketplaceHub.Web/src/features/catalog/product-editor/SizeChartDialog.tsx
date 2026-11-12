// "Beden Tablosu Oluştur" (Fiyat, Stok ve Vergi): the measurements per Beden value, drawn into a 1200×1800 JPEG by the
// server through Claude (Settings › Yapay Zekâ). "Kullan" only puts the picture into the editor's images as the last
// one; it reaches the marketplaces with the normal Kaydet / Platformlarda Güncelle. A failed or cancelled run adds nothing.
import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { chartDrawnMeanwhile, isUnknownSizeChartOutcome, sizeChartFailureMessage } from './size-chart-outcome'
import { Button, FieldHelp, IconButton, Modal, UiIcon } from '../../../shared/components'
import { addSizeChartColumn, initialSizeChartDraft, maximumSizeChartColumns, newColumnIssue, presetSizeChartColumns, removeSizeChartColumn, setSizeChartCell, sizeChartCellKey, sizeChartColumnLabel, validateSizeChartDraft, type SizeChartDraft, type SizeChartMeasurements } from './size-chart-measurements'
import './size-chart.css'

export type SizeChartInfo = { sizes: string[]; sizeOptionLabel: string | null; measurements: SizeChartMeasurements | null; sizeChartUrl: string | null; aiReady: boolean; aiIssue: string | null; lastGeneratedUrl?: string | null }
export type SizeChartResult = { url: string; fileAssetId: string; inputTokens: number | null; outputTokens: number | null; costUsd: number | null }

export function SizeChartButton({ productId, sizes, disabledReason, onUse }: { productId: string | null; sizes: string[]; disabledReason: string | null; onUse: (url: string) => void }) {
  const [open, setOpen] = useState(false)
  // A field of the price and stock grid (beside Güvenlik Stoğu): an empty label line keeps the button level with the inputs.
  return <div className="size-chart-trigger">
    {/* Why it is off sits behind a "?" on the label line, so the button stays level with the fields beside it. */}
    <span className="size-chart-trigger-label">{disabledReason ? <FieldHelp id="size-chart-disabled-reason" label="Beden Tablosu">{disabledReason}</FieldHelp> : <span aria-hidden="true">&nbsp;</span>}</span>
    <button type="button" className="secondary size-chart-button" disabled={Boolean(disabledReason)} title={disabledReason ?? 'Ölçüleri girip Ravencia tasarımında beden tablosu görseli oluşturun.'} aria-describedby={disabledReason ? 'size-chart-disabled-reason' : undefined} onClick={() => setOpen(true)}>Beden Tablosu</button>
    {open && productId && createPortal(<SizeChartDialog productId={productId} sizes={sizes} onClose={() => setOpen(false)} onUse={url => { onUse(url); setOpen(false) }} />, document.body)}
  </div>
}

function formatCost(result: SizeChartResult) {
  const parts: string[] = []
  if (result.inputTokens != null || result.outputTokens != null) parts.push(`${(result.inputTokens ?? 0).toLocaleString('tr-TR')} / ${(result.outputTokens ?? 0).toLocaleString('tr-TR')} token`)
  if (result.costUsd != null) parts.push(`$${result.costUsd.toFixed(4)}`)
  return parts.join(' · ')
}

export function SizeChartDialog({ productId, sizes, onClose, onUse }: { productId: string; sizes: string[]; onClose: () => void; onUse: (url: string) => void }) {
  const info = useQuery({ queryKey: ['size-chart', productId], queryFn: () => hubApi<SizeChartInfo>(`/products/${productId}/size-chart`), staleTime: 0, retry: false })
  const [draft, setDraft] = useState<SizeChartDraft | null>(null)
  const [showErrors, setShowErrors] = useState(false)
  const [columnName, setColumnName] = useState('')
  const [columnMenuOpen, setColumnMenuOpen] = useState(false)
  const [running, setRunning] = useState(false)
  const [elapsed, setElapsed] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<SizeChartResult | null>(null)
  const abort = useRef<AbortController | null>(null)

  // The panel's Beden order wins; the server's list covers a size the editor does not show.
  const rowSizes = sizes.length ? sizes : info.data?.sizes ?? []
  if (info.data && draft === null) setDraft(initialSizeChartDraft(rowSizes, info.data.measurements))

  useEffect(() => {
    if (!running) return
    const started = Date.now()
    const timer = window.setInterval(() => setElapsed(Math.round((Date.now() - started) / 1000)), 1000)
    return () => window.clearInterval(timer)
  }, [running])
  useEffect(() => () => abort.current?.abort(), [])

  const validation = draft ? validateSizeChartDraft(draft) : null
  const cellErrors = showErrors && validation && !validation.ok ? validation.cellErrors : {}
  const columnErrors = showErrors && validation && !validation.ok ? validation.columnErrors : {}
  const aiReady = info.data?.aiReady ?? false

  async function generate() {
    if (!draft) return
    setShowErrors(true)
    const checked = validateSizeChartDraft(draft)
    if (!checked.ok) { setError(checked.message); return }
    const controller = new AbortController()
    abort.current = controller
    const lastGeneratedBefore = info.data?.lastGeneratedUrl ?? null
    setRunning(true); setElapsed(0); setError(null); setResult(null)
    try {
      // A fresh key on every press: a lost answer is never sent again under the same key (size-chart-outcome.ts).
      const generated = await hubApi<SizeChartResult>(`/products/${productId}/size-chart/generate`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('size-chart') }, body: JSON.stringify(checked.payload), signal: controller.signal })
      if (controller.signal.aborted) return
      setResult(generated)
    } catch (reason) {
      if (controller.signal.aborted) return
      if (isUnknownSizeChartOutcome(reason)) {
        // The answer was lost: the server's newest drawn chart tells whether this try made one.
        const fresh = await info.refetch().then(read => read.data).catch(() => undefined)
        if (controller.signal.aborted) return
        const drawn = chartDrawnMeanwhile(lastGeneratedBefore, fresh?.lastGeneratedUrl)
        if (drawn) { setResult({ url: drawn, fileAssetId: '', inputTokens: null, outputTokens: null, costUsd: null }); return }
      }
      setError(sizeChartFailureMessage(reason))
    } finally {
      if (abort.current === controller) { abort.current = null; setRunning(false) }
    }
  }

  function close() {
    abort.current?.abort()
    onClose()
  }

  function addColumn(name: string) {
    if (!draft || newColumnIssue(name, draft.columns)) return
    setDraft(addSizeChartColumn(draft, name)); setColumnName(''); setColumnMenuOpen(false)
  }

  const columnIssue = draft && columnName.trim() ? newColumnIssue(columnName, draft.columns) : null
  const footer = <>
    <Button variant="secondary" onClick={close}>{running ? 'Vazgeç' : 'Kapat'}</Button>
    {result
      ? <><Button variant="secondary" disabled={running} onClick={() => void generate()}>Yeniden Oluştur</Button><Button disabled={running} onClick={() => onUse(result.url)}>Kullan</Button></>
      : <Button disabled={!draft || !aiReady || running} onClick={() => void generate()}>{running ? 'Oluşturuluyor…' : 'Oluştur'}</Button>}
  </>

  return <Modal open title="Beden Tablosu Oluştur" className="size-chart-modal" onClose={close} description="Ölçüler cm cinsindendir. Tablo yalnız girdiğiniz sayılarla çizilir; Kullan görseli ürünün son görseli olarak ekler, pazaryerine Kaydet ile gider." footer={footer}>
    {info.isPending ? <p className="size-chart-note" role="status"><UiIcon name="loader" /> Ölçüler yükleniyor…</p>
      : info.isError ? <p className="error size-chart-error" role="alert">{info.error instanceof Error ? info.error.message : 'Beden tablosu bilgisi alınamadı.'}</p>
      : !draft || !draft.rows.length ? <p className="error size-chart-error" role="alert">Bu ürünün Beden değeri yok.</p>
      : <>
        {!aiReady && <p className="error size-chart-error" role="alert">{info.data?.aiIssue ?? 'Yapay zekâ ayarları hazır değil.'}</p>}
        <div className="size-chart-table-wrap">
          <table className="size-chart-table">
            <thead><tr><th scope="col">Beden</th>{draft.columns.map((column, columnIndex) => <th scope="col" key={`${column}-${columnIndex}`}>
              <span className="size-chart-column-head"><span title={columnErrors[columnIndex]} className={columnErrors[columnIndex] ? 'is-invalid' : undefined}>{sizeChartColumnLabel(column)}</span>
                {draft.columns.length > 1 && <IconButton size="sm" className="size-chart-icon-button" disabled={running} label={`${column} kolonunu kaldır`} title="Kolonu kaldır" onClick={() => setDraft(removeSizeChartColumn(draft, columnIndex))}><UiIcon name="close" size={14} /></IconButton>}</span>
            </th>)}</tr></thead>
            <tbody>{draft.rows.map((row, rowIndex) => <tr key={row.size}><th scope="row">{row.size}</th>{draft.columns.map((column, columnIndex) => {
              const message = cellErrors[sizeChartCellKey(rowIndex, columnIndex)]
              return <td key={`${column}-${columnIndex}`}><input inputMode="decimal" autoComplete="off" aria-label={`${row.size} ${sizeChartColumnLabel(column)}`} aria-invalid={message ? true : undefined} title={message} disabled={running} value={row.values[columnIndex] ?? ''} onChange={event => setDraft(setSizeChartCell(draft, rowIndex, columnIndex, event.target.value))} />{message && <small className="size-chart-cell-error">{message}</small>}</td>
            })}</tr>)}</tbody>
          </table>
        </div>
        <div className="size-chart-columns">
          <Button variant="secondary" className="size-chart-add" disabled={running || draft.columns.length >= maximumSizeChartColumns} aria-expanded={columnMenuOpen} onClick={() => setColumnMenuOpen(value => !value)}><UiIcon name="plus" size={14} />Kolon Ekle</Button>
          {columnMenuOpen && <div className="size-chart-column-menu" role="group" aria-label="Kolon ekle">
            {presetSizeChartColumns.filter(name => !newColumnIssue(name, draft.columns)).map(name => <Button key={name} variant="secondary" onClick={() => addColumn(name)}>{name}</Button>)}
            <input value={columnName} maxLength={24} placeholder="Başka bir ölçü" aria-label="Yeni kolon adı" onChange={event => setColumnName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); addColumn(columnName) } }} />
            <Button variant="secondary" disabled={!columnName.trim() || Boolean(columnIssue)} onClick={() => addColumn(columnName)}>Ekle</Button>
            {columnIssue && <small className="size-chart-cell-error">{columnIssue}</small>}
          </div>}
        </div>
        {running && <p className="size-chart-note" role="status"><UiIcon name="loader" /> Beden tablosu oluşturuluyor… {elapsed} sn. Bu işlem 1-3 dakika sürebilir; pencereyi kapatırsanız görsel eklenmez.</p>}
        {error && !running && <p className="error size-chart-error" role="alert">{error}</p>}
        {result && !running && <figure className="size-chart-preview">
          <img src={result.url} alt="Oluşturulan beden tablosu önizlemesi" />
          {formatCost(result) && <figcaption>{formatCost(result)}</figcaption>}
        </figure>}
      </>}
  </Modal>
}
