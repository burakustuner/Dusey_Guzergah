export function parseChainage(raw: string): number | null {
  const text = raw.trim().replace(/\s/g, '').replace(',', '.')
  if (!text) {
    return null
  }
  const parts = text.split('+').filter((part) => part.length > 0)
  if (parts.length === 1) {
    const value = Number(parts[0])
    return Number.isFinite(value) ? value : null
  }
  const kilometers = Number(parts[0])
  const meters = Number(parts[1])
  if (!Number.isFinite(kilometers) || !Number.isFinite(meters)) {
    return null
  }
  const sign = kilometers < 0 ? -1 : 1
  return kilometers * 1000 + sign * meters
}

export function formatChainage(meters: number): string {
  const sign = meters < 0 ? '-' : ''
  const abs = Math.abs(meters)
  const km = Math.floor(abs / 1000)
  const rest = abs - km * 1000
  return `${sign}${km}+${rest.toFixed(2)}`
}
