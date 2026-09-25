export type Crossing = {
  id: string
  name: string
  type: string
  startKm: number
  endKm: number
  depth: number
}

export const crossingTemplate = [
  'özel geçiş adı;tip;başlangıç km;bitiş km;derinlik (m)',
  'Örnek yol;karayolu;1.200;1.280;1.2',
  'Örnek demiryolu;demiryolu;3.400;3.460;1.5',
  'Örnek dere;akarsu;5.100;5.180;2.5',
].join('\r\n')

let crossingSeq = 1

export function emptyCrossing(): Crossing {
  crossingSeq += 1
  return { id: `gecis-${crossingSeq}`, name: '', type: '', startKm: 0, endKm: 0, depth: 1.2 }
}

export function downloadCrossingTemplate() {
  const blob = new Blob([`\uFEFF${crossingTemplate}`], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = 'ozel-gecis-sablonu.csv'
  link.click()
  URL.revokeObjectURL(url)
}

export async function readCrossingFile(file: File): Promise<Crossing[]> {
  const text = await file.text()
  return parseCrossingTable(text)
}

export function parseCrossingTable(text: string): Crossing[] {
  const lines = text.replace(/^\uFEFF/, '').split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  if (lines.length === 0) {
    return []
  }
  const delimiter = lines[0].includes(';') ? ';' : ','
  const header = lines[0].split(delimiter).map(normalizeHeader)
  const nameAt = columnIndex(header, ['ozel gecis adi', 'ad', 'adi', 'name'])
  const typeAt = columnIndex(header, ['tip', 'type'])
  const startAt = columnIndex(header, ['baslangic km', 'baslangic', 'start'])
  const endAt = columnIndex(header, ['bitis km', 'bitis', 'end'])
  const depthAt = columnIndex(header, ['derinlik (m)', 'derinlik', 'depth'])
  if (startAt < 0 || endAt < 0 || depthAt < 0) {
    throw new Error('Şablonda başlangıç km, bitiş km ve derinlik (m) sütunları olmalı.')
  }
  const rows: Crossing[] = []
  for (const line of lines.slice(1)) {
    const cells = line.split(delimiter).map((cell) => cell.trim())
    const startKm = parseKm(cells[startAt] ?? '')
    const endKm = parseKm(cells[endAt] ?? '')
    const depth = parseNumber(cells[depthAt] ?? '')
    if (startKm === null || endKm === null || depth === null) {
      continue
    }
    crossingSeq += 1
    rows.push({
      id: `gecis-${crossingSeq}`,
      name: nameAt >= 0 ? cells[nameAt] ?? '' : '',
      type: typeAt >= 0 ? cells[typeAt] ?? '' : '',
      startKm,
      endKm,
      depth,
    })
  }
  return rows
}

function normalizeHeader(value: string) {
  return value
    .toLocaleLowerCase('tr')
    .replace(/ı/g, 'i')
    .replace(/ş/g, 's')
    .replace(/ğ/g, 'g')
    .replace(/ü/g, 'u')
    .replace(/ö/g, 'o')
    .replace(/ç/g, 'c')
    .trim()
}

function columnIndex(header: string[], names: string[]) {
  return header.findIndex((cell) => names.includes(cell))
}

function parseKm(value: string): number | null {
  const text = value.trim()
  if (!text) {
    return null
  }
  const plus = text.match(/^(\d+)\s*\+\s*(\d+(?:[.,]\d+)?)$/)
  if (plus) {
    const km = Number(plus[1])
    const meters = parseNumber(plus[2])
    if (meters === null) {
      return null
    }
    return km + meters / 1000
  }
  return parseNumber(text)
}

function parseNumber(value: string): number | null {
  const number = Number(value.replace(',', '.'))
  return Number.isFinite(number) ? number : null
}
