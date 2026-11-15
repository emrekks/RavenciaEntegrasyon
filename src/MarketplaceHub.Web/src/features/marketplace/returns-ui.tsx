// WEB-ALTYAPI-12: the panel's rr- building blocks moved to src/shared/ui/rr-components.tsx. This re-export keeps the
// old path working for the files that still import it (the product editor among them); new code imports shared/ui.
export * from '../../shared/ui/rr-components'
