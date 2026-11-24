// "Sayfa Başına" of the Loglar lists: the server serves at most 100 rows a request (the İşler list also offers 200).
export const logPageSizes = [20, 50, 100] as const
export const logDefaultPageSize = 20
