export type Point = { station: number; elevation: number }

export type Vertex = Point & {
  curveLength: number
  curveType: number
  tangentIn: number
  tangentOut: number
  radius: number
}

export type Guide = {
  vertexIndex: number
  start: Point
  vertex: Point
  end: Point
}

export type CoverIssue = { station: number; cover: number; message: string }

export type ValidationIssue = { severity: string; source: string; message: string }

export type Bounds = {
  minStation: number
  maxStation: number
  minElevation: number
  maxElevation: number
}

export type SessionResponse = {
  sessionId: string
  sectionCount: number
  terrainPointCount: number
  terrain: Point[]
  ktbVertices: Vertex[]
  ktbCurve: Point[]
  ktbGuides: Guide[]
  issues: ValidationIssue[]
  bounds: Bounds
  hasKtbTemplate: boolean
}

export type DesignResult = {
  vertices: Vertex[]
  curve: Point[]
  guides: Guide[]
  preferredLowerProfile: Point[]
  preferredUpperProfile: Point[]
  minimumCoverUpperProfile: Point[]
  coverIssues: CoverIssue[]
}

export type DesignCrossing = {
  name: string
  type: string
  startStation: number
  endStation: number
  depth: number
}

export type DesignParameters = {
  targetCover: number
  coverTolerance: number
  minimumCover: number
  maximumCover: number
  strippingDepth: number
  pipeDiameter: number
  pipeLength: number
  minimumSegmentLength: number
  minimumRadius: number
  coldBendMinRadius: number
  elasticRadiusFactor: number
  hotBendRadiusFactor: number
  hotBendMaxAngleDeg: number
  hotBendTangentM: number
  straightEndM: number
  crossings: DesignCrossing[]
}

export type DesignForm = {
  standard: string
  diameterInches: number
  targetCover: number
  minimumCover: number
  extraDepth: number
  strippingDepth: number
  pipeLength: number
  straightEndM: number
  minimumSegmentLength: number
  minimumRadius: number
  coldBendMinRadius: number
  elasticRadiusFactor: number
  hotBendRadiusFactor: number
  hotBendMaxAngleDeg: number
  hotBendTangentM: number
}

export type Layers = {
  terrain: boolean
  pipe: boolean
  ktb: boolean
  someInfo: boolean
  ktbGuides: boolean
  calculatedCurve: boolean
  calculatedVertices: boolean
  calculatedGuides: boolean
  corridor: boolean
  issues: boolean
}

export type CanvasTool = 'select' | 'pan' | 'measure' | 'window' | 'pick'

export type WarningThresholds = {
  minInteriorCurveLengthM: number
  minStrippedTerrainToAxisGapM: number
  warnRadiusBelowDesignMinimum: boolean
  maxAbsGradePercent: number
}

export const defaultThresholds: WarningThresholds = {
  minInteriorCurveLengthM: 5,
  minStrippedTerrainToAxisGapM: 0.45,
  warnRadiusBelowDesignMinimum: true,
  maxAbsGradePercent: 25,
}

export const defaultForm: DesignForm = {
  standard: 'botas',
  diameterInches: 12,
  targetCover: 1.2,
  minimumCover: 1.0,
  extraDepth: 3,
  strippingDepth: 0.15,
  pipeLength: 12,
  straightEndM: 1.8,
  minimumSegmentLength: 24,
  minimumRadius: 18.288,
  coldBendMinRadius: 18.288,
  elasticRadiusFactor: 500,
  hotBendRadiusFactor: 5,
  hotBendMaxAngleDeg: 45,
  hotBendTangentM: 1,
}

export const defaultLayers: Layers = {
  terrain: true,
  pipe: true,
  ktb: true,
  someInfo: false,
  ktbGuides: false,
  calculatedCurve: true,
  calculatedVertices: true,
  calculatedGuides: false,
  corridor: false,
  issues: true,
}

export function toDesignParameters(
  form: DesignForm,
  crossings: { name: string; type: string; startKm: number; endKm: number; depth: number }[] = [],
): DesignParameters {
  return {
    targetCover: form.targetCover,
    coverTolerance: 0.05,
    minimumCover: form.minimumCover,
    maximumCover: form.targetCover + form.extraDepth,
    strippingDepth: form.strippingDepth,
    pipeDiameter: form.diameterInches * 0.0254,
    pipeLength: form.pipeLength,
    minimumSegmentLength: form.minimumSegmentLength,
    minimumRadius: form.minimumRadius,
    coldBendMinRadius: form.coldBendMinRadius,
    elasticRadiusFactor: form.elasticRadiusFactor,
    hotBendRadiusFactor: form.hotBendRadiusFactor,
    hotBendMaxAngleDeg: form.hotBendMaxAngleDeg,
    hotBendTangentM: form.hotBendTangentM,
    straightEndM: form.straightEndM,
    crossings: crossings
      .filter((row) => row.depth > 0 && row.endKm !== row.startKm)
      .map((row) => ({
        name: row.name,
        type: row.type,
        startStation: Math.min(row.startKm, row.endKm) * 1000,
        endStation: Math.max(row.startKm, row.endKm) * 1000,
        depth: row.depth,
      })),
  }
}
