import { UiIcon } from './UiIcon';

interface PaginationProps {
  page: number;
  pageSize: number;
  total: number;
  onPageChange: (page: number) => void;
  onPageSizeChange?: (size: number) => void;
  pageSizeOptions?: number[];
  className?: string;
}

export function Pagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = [20, 50, 100],
  className = ''
}: PaginationProps) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const from = Math.min((page - 1) * pageSize + 1, total);
  const to = Math.min(page * pageSize, total);

  return (
    <div className={`rv-pagination ${className}`}>
      {onPageSizeChange && (
        <div className="rv-pagination__size">
          <select
            className="rv-select rv-select--sm"
            value={pageSize}
            onChange={(e) => onPageSizeChange(Number(e.target.value))}
          >
            {pageSizeOptions.map((s) => (
              <option key={s} value={s}>{s} / sayfa</option>
            ))}
          </select>
        </div>
      )}

      <span className="rv-pagination__info">
        {from}–{to} / {total}
      </span>

      <div className="rv-pagination__controls">
        <button
          className="rv-icon-btn rv-icon-btn--ghost rv-icon-btn--sm"
          disabled={page <= 1}
          onClick={() => onPageChange(page - 1)}
          aria-label="Önceki sayfa"
        >
          <UiIcon name="chevronLeft" />
        </button>

        {Array.from({ length: Math.min(totalPages, 5) }, (_, i) => {
          let pageNum: number;
          if (totalPages <= 5) {
            pageNum = i + 1;
          } else if (page <= 3) {
            pageNum = i + 1;
          } else if (page >= totalPages - 2) {
            pageNum = totalPages - 4 + i;
          } else {
            pageNum = page - 2 + i;
          }
          return (
            <button
              key={pageNum}
              className={`rv-pagination__page ${pageNum === page ? 'rv-pagination__page--active' : ''}`}
              onClick={() => onPageChange(pageNum)}
            >
              {pageNum}
            </button>
          );
        })}

        <button
          className="rv-icon-btn rv-icon-btn--ghost rv-icon-btn--sm"
          disabled={page >= totalPages}
          onClick={() => onPageChange(page + 1)}
          aria-label="Sonraki sayfa"
        >
          <UiIcon name="chevronRight" />
        </button>
      </div>
    </div>
  );
}
