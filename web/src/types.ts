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
}

export type DesignForm = {
  targetCover: number
  extraDepth: number
  strippingDepth: number
  pipeDiameter: number
  pipeLength: number
  minimumSegmentLength: number
  minimumRadius: number
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
  targetCover: 1.2,
  extraDepth: 3,
  strippingDepth: 0.15,
  pipeDiameter: 12,
  pipeLength: 12,
  minimumSegmentLength: 24,
  minimumRadius: 60,
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

export function toDesignParameters(form: DesignForm): DesignParameters {
  return {
    targetCover: form.targetCover,
    coverTolerance: 0.05,
    minimumCover: form.targetCover,
    maximumCover: form.targetCover + form.extraDepth,
    strippingDepth: form.strippingDepth,
    pipeDiameter: form.pipeDiameter,
    pipeLength: form.pipeLength,
    minimumSegmentLength: form.minimumSegmentLength,
    minimumRadius: form.minimumRadius,
  }
}
