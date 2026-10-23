// One area of the sales map: a Turkish province (plate code; "00" is Northern Cyprus, drawn but not a province) or a
// country (ISO 3166-1 alpha-2). d: its outline in the map's viewBox units; x, y: where its label goes.
export type MapRegion = { code: string; name: string; d: string; x: number; y: number }
