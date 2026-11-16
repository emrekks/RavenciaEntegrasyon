// A percentage the panel works out itself rounds half away from zero, as the server's PercentRounding and the money
// figures do (owner, 16 Nov 2026): −12,5 % is −13 %, not −12 % as Math.round (half towards +∞) would give.
export function roundPercent(value: number, digits = 0) {
  const scale = 10 ** digits
  const rounded = Math.round(Math.abs(value) * scale) / scale
  return value < 0 ? -rounded : rounded
}
