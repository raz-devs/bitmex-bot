import type { Candle, Operand } from './api'

// Display-only copies of the server's indicator formulas (SMA-seeded EMA, Wilder RSI, population-stddev Bollinger),
// so chart lines match what the strategies see. Signals are always computed on the server.

export type Series = (number | null)[]

const src = (c: Candle, s: Operand['source']) => (s === 'open' ? c.open : s === 'high' ? c.high : s === 'low' ? c.low : s === 'volume' ? c.volume : c.close)

export function sma(xs: number[], p: number): Series {
  const out: Series = []
  let sum = 0
  xs.forEach((x, i) => {
    sum += x
    if (i >= p) sum -= xs[i - p]
    out.push(i >= p - 1 ? sum / p : null)
  })
  return out
}

export function ema(xs: number[], p: number): Series {
  const out: Series = []
  const k = 2 / (p + 1)
  let prev: number | null = null
  let sum = 0
  xs.forEach((x, i) => {
    if (prev == null) {
      sum += x
      if (i === p - 1) prev = sum / p
    } else prev = (x - prev) * k + prev
    out.push(prev)
  })
  return out
}

export function rsi(xs: number[], p: number): Series {
  const out: Series = [null]
  let g = 0,
    l = 0
  for (let i = 1; i < xs.length; i++) {
    const ch = xs[i] - xs[i - 1]
    const up = Math.max(ch, 0),
      down = Math.max(-ch, 0)
    if (i < p) {
      g += up
      l += down
      out.push(null)
      continue
    }
    if (i === p) {
      g = (g + up) / p
      l = (l + down) / p
    } else {
      g = (g * (p - 1) + up) / p
      l = (l * (p - 1) + down) / p
    }
    out.push(l === 0 ? (g === 0 ? 50 : 100) : 100 - 100 / (1 + g / l))
  }
  return out
}

export function compute(bars: Candle[], o: Operand): Series {
  const xs = bars.map((b) => src(b, o.source))
  const p = o.period ?? 14
  switch (o.ind) {
    case 'price':
      return xs
    case 'value':
      return xs.map(() => o.value ?? 0)
    case 'ema':
      return ema(xs, p)
    case 'sma':
      return sma(xs, p)
    case 'rsi':
      return rsi(xs, p)
    case 'macd': {
      const f = ema(xs, o.fast ?? 12),
        s = ema(xs, o.slow ?? 26)
      const line = f.map((v, i) => (v == null || s[i] == null ? null : v - s[i]!))
      const firstValid = line.findIndex((v) => v != null)
      const sig: Series = firstValid < 0 ? line.map(() => null) : [...line.slice(0, firstValid).map(() => null), ...ema(line.slice(firstValid) as number[], o.signal ?? 9)]
      if (o.output === 'signal') return sig
      if (o.output === 'hist') return line.map((v, i) => (v == null || sig[i] == null ? null : v - sig[i]!))
      return line
    }
    case 'bb': {
      const period = o.period ?? 20
      const mid = sma(xs, period)
      return mid.map((m, i) => {
        if (m == null) return null
        if (o.output !== 'upper' && o.output !== 'lower') return m
        const w = xs.slice(i - period + 1, i + 1)
        const sd = Math.sqrt(w.reduce((a, v) => a + (v - m) ** 2, 0) / period)
        return o.output === 'upper' ? m + (o.mult ?? 2) * sd : m - (o.mult ?? 2) * sd
      })
    }
  }
}

/** Oscillators get their own pane; moving averages and bands sit on the price chart. */
export const ownPane = (o: Operand) => o.ind === 'rsi' || o.ind === 'macd' || (o.ind === 'price' && o.source === 'volume')
