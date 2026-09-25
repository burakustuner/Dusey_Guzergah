/** BOTAŞ soğuk büküm: çap grubuna göre en küçük yarıçap, boru çapının katı. */
const coldBendMultiples: { maxInches: number; multiples: number }[] = [
  { maxInches: 6, multiples: 115 },
  { maxInches: 8, multiples: 90 },
  { maxInches: 10, multiples: 70 },
  { maxInches: 14, multiples: 60 },
  { maxInches: 18, multiples: 50 },
  { maxInches: 999, multiples: 40 },
]

export const diameterChoices = [6, 8, 10, 12, 14, 16, 18, 20, 24, 28, 30, 36, 42, 48, 56]

export function diameterMeters(inches: number) {
  return inches * 0.0254
}

export function coldBendMultiplesOf(inches: number) {
  return coldBendMultiples.find((row) => inches <= row.maxInches)?.multiples ?? 40
}

export function coldBendMinRadiusM(inches: number) {
  return coldBendMultiplesOf(inches) * diameterMeters(inches)
}

export function elasticRadiusM(inches: number, factor: number) {
  return factor * diameterMeters(inches)
}

export function hotBendRadiusM(inches: number, factor: number) {
  return factor * diameterMeters(inches)
}

export const botasStandard = {
  id: 'botas',
  label: 'BOTAŞ',
  targetCover: 1.2,
  minimumCover: 1.0,
  extraDepth: 3,
  strippingDepth: 0.15,
  pipeLength: 12,
  straightEndM: 1.8,
  minimumSegmentLength: 24,
  elasticRadiusFactor: 500,
  hotBendRadiusFactor: 5,
  hotBendMaxAngleDeg: 45,
  hotBendTangentM: 1,
}

export function applyBotas(inches: number) {
  const cold = coldBendMinRadiusM(inches)
  return {
    standard: botasStandard.id,
    diameterInches: inches,
    targetCover: botasStandard.targetCover,
    minimumCover: botasStandard.minimumCover,
    extraDepth: botasStandard.extraDepth,
    strippingDepth: botasStandard.strippingDepth,
    pipeLength: botasStandard.pipeLength,
    straightEndM: botasStandard.straightEndM,
    minimumSegmentLength: botasStandard.minimumSegmentLength,
    minimumRadius: round3(cold),
    coldBendMinRadius: round3(cold),
    elasticRadiusFactor: botasStandard.elasticRadiusFactor,
    hotBendRadiusFactor: botasStandard.hotBendRadiusFactor,
    hotBendMaxAngleDeg: botasStandard.hotBendMaxAngleDeg,
    hotBendTangentM: botasStandard.hotBendTangentM,
  }
}

export function bendFieldsForDiameter(inches: number) {
  const cold = round3(coldBendMinRadiusM(inches))
  return {
    diameterInches: inches,
    minimumRadius: cold,
    coldBendMinRadius: cold,
  }
}

function round3(value: number) {
  return Math.round(value * 1000) / 1000
}
