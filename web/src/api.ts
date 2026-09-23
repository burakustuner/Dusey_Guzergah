import type { DesignParameters, DesignResult, Guide, Point, SessionResponse, Vertex, WarningThresholds } from './types'

async function errorMessage(response: Response): Promise<string> {
  const text = await response.text()
  if (!text) {
    return response.statusText || 'İstek başarısız.'
  }
  try {
    const body = JSON.parse(text) as { message?: string; title?: string }
    return body.message || body.title || text
  } catch {
    return text
  }
}

export async function createSession(kse: File, ktb: File | null): Promise<SessionResponse> {
  const form = new FormData()
  form.append('kse', kse)
  if (ktb) {
    form.append('ktb', ktb)
  }
  const response = await fetch('/api/sessions', { method: 'POST', body: form })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  return response.json() as Promise<SessionResponse>
}

export async function calculateDesign(sessionId: string, parameters: DesignParameters): Promise<DesignResult> {
  const response = await fetch(`/api/sessions/${sessionId}/calculate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(parameters),
  })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  return response.json() as Promise<DesignResult>
}

export async function previewCurve(vertices: Vertex[]): Promise<{ curve: Point[]; guides: Guide[] }> {
  const response = await fetch('/api/alignment/curve', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(vertices),
  })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  return response.json() as Promise<{ curve: Point[]; guides: Guide[] }>
}

export async function fetchWarnings(
  sessionId: string,
  thresholds: WarningThresholds,
  parameters: DesignParameters,
  vertices: Vertex[],
): Promise<string[]> {
  const response = await fetch(`/api/sessions/${sessionId}/warnings`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ thresholds, parameters, vertices }),
  })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  return response.json() as Promise<string[]>
}

async function downloadKtb(response: Response, fileName: string) {
  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

export async function exportKtb(sessionId: string): Promise<void> {
  const response = await fetch(`/api/sessions/${sessionId}/export`, { method: 'POST' })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  await downloadKtb(response, 'GÜZERGAH_dusey.KTB')
}

export async function exportEditorKtb(sessionId: string, vertices: Vertex[]): Promise<void> {
  const response = await fetch(`/api/sessions/${sessionId}/export-editor`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(vertices),
  })
  if (!response.ok) {
    throw new Error(await errorMessage(response))
  }
  await downloadKtb(response, 'GÜZERGAH_dusey_duzen.KTB')
}
