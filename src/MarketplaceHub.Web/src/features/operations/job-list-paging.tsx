import { useState } from 'react'
import { Button, UiIcon } from '../../shared/components'
import { Pager } from '../../shared/components/Pager'
import { ApiRequestError, hubFile } from '../../shared/api'

// The paging and the Excel download the job detail's two lists share (Gidenler ve Değişenler, Çekilen Kayıtlar):
// 25 rows a page, numbered pages with "Toplam N adet", and the file the server writes for the current search and filter.
export const jobListPageSize = 25

export function JobListPager({ page, total, onPage, shown }: { page: number; total: number; onPage: (page: number) => void; shown?: number }) {
  const pages = Math.max(1, Math.ceil(total / jobListPageSize))
  return <div className="jc-footer">
    <small>Toplam {total.toLocaleString('tr-TR')} adet{shown ? ` · ${(((page - 1) * jobListPageSize) + 1).toLocaleString('tr-TR')}–${(((page - 1) * jobListPageSize) + shown).toLocaleString('tr-TR')} gösteriliyor` : ''}</small>
    {pages > 1 ? <Pager page={page} totalPages={pages} onPageChange={onPage} ariaLabel="Sayfalama" /> : null}
  </div>
}

function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url; anchor.download = fileName
  document.body.append(anchor); anchor.click(); anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

const tooLarge = 'Bu işte 100.000’den fazla satır var; arama ya da filtreyle daraltıp yeniden deneyin.'

// "Excel'e Aktar": downloads path (with the search and filters as its query) and says plainly when it cannot.
export function ExcelExportButton({ path, query, fallbackName, disabled }: { path: string; query: Record<string, string>; fallbackName: string; disabled?: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function run() {
    setBusy(true); setError('')
    try {
      const params = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== ''))
      const file = await hubFile(`${path}${params.size ? `?${params}` : ''}`)
      saveBlob(file.blob, file.fileName ?? fallbackName)
    } catch (reason) {
      setError(reason instanceof ApiRequestError && reason.code === 'JOB_EXPORT_TOO_LARGE' ? tooLarge : reason instanceof Error ? reason.message : 'Dosya indirilemedi.')
    } finally { setBusy(false) }
  }
  return <span className="jc-export">
    <Button variant="secondary" size="sm" loading={busy} disabled={disabled || busy} onClick={() => void run()} title="Geçerli arama ve süzgeçle tüm satırları Excel dosyası olarak indirir."><UiIcon name="export" size={14} />Excel’e Aktar</Button>
    {error && <small role="alert" className="jc-export-error">{error}</small>}
  </span>
}
