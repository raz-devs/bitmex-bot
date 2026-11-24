import * as signalR from '@microsoft/signalr'

// ---------- types (mirror the server's JSON) ----------

export type Candle = { time: number; open: number; high: number; low: number; close: number; volume: number }

export type Operand = {
  ind: 'price' | 'ema' | 'sma' | 'rsi' | 'macd' | 'bb' | 'value'
  period?: number
  source?: 'open' | 'high' | 'low' | 'close' | 'volume'
  output?: string
  value?: number
  mult?: number
  fast?: number
  slow?: number
  signal?: number
}
export type Op = 'crossesAbove' | 'crossesBelow' | 'gt' | 'lt'
export type Comparison = { left: Operand; op: Op; right: Operand }
export type ConditionGroup = { mode: 'all' | 'any'; rules: Comparison[] }
export type OrderPlan = {
  type: 'Limit' | 'Market'
  contracts: number
  offsetTicks: number
  postOnly: boolean
  timeoutBars: number
  takeProfitPct: number
  stopLossPct: number
}
export type Strategy = {
  id: string
  name: string
  timeframe: string
  evaluate: 'close' | 'tick'
  long: ConditionGroup | null
  short: ConditionGroup | null
  order: OrderPlan
  enabled: boolean
  autoTrade: boolean
}

export type Status = {
  exchange: string
  symbol: string
  connected: boolean
  testnet: boolean
  canTrade: boolean
  autoTrading: boolean
  autoTradingOffReason: string | null
  lastPrice: number | null
  markPrice: number | null
  bestBid: number | null
  bestAsk: number | null
  bookImbalance: number | null
  fundingRate: number | null
  feedLagMs: number
  evalMicros: number
  trades: number
}
export type Position = { qty: number; avgEntry: number | null; liquidation: number | null; unrealisedBtc: number; realisedBtc: number }
export type Margin = { walletBtc: number; equityBtc: number; availableBtc: number }
export type Order = {
  orderId: string
  clientId: string | null
  side: string
  qty: number
  price: number | null
  stopPrice: number | null
  type: string
  status: string
  cumQty: number
  reduceOnly: boolean
  time: string
}
export type SignalEvent = {
  time: string
  strategyId: string
  strategy: string
  timeframe: string
  side: 'Long' | 'Short'
  price: number
  onClose: boolean
  latencyMs: number
  action: string
}
export type TradeView = {
  id: string
  source: string
  side: string
  qty: number
  filledQty: number
  avgEntry: number | null
  takeProfit: number | null
  stopLoss: number | null
  state: string
  note: string | null
  created: string
}
export type State = {
  status: Status
  position: Position
  margin: Margin | null
  orders: Order[]
  signals: SignalEvent[]
  trades: TradeView[]
}
export type BacktestResult = {
  bars: number
  from: string
  to: string
  signals: number
  missedEntries: number
  trades: number
  wins: number
  winRate: number
  netXbt: number
  feesXbt: number
  avgWinXbt: number
  avgLossXbt: number
  expectancyXbt: number
  profitFactor: number | null
  maxDrawdownXbt: number
  tradeList: { side: string; entryTime: string; entryPrice: number; exitTime: string; exitPrice: number; exitReason: string; pnlXbt: number }[]
}

export const TIMEFRAMES = ['1m', '5m', '15m', '30m', '1h', '4h', '1d']

// ---------- auth + fetch ----------

const TOKEN_KEY = 'bb-token'
export function getToken(): string {
  try {
    return localStorage.getItem(TOKEN_KEY) ?? ''
  } catch {
    return ''
  }
}
export function setToken(t: string) {
  try {
    localStorage.setItem(TOKEN_KEY, t)
  } catch {
    /* private mode: token lasts for this page only */
  }
}

export class Unauthorized extends Error {}

export async function api<T>(path: string, init?: { method?: string; body?: unknown }): Promise<T> {
  const token = getToken()
  const res = await fetch(path, {
    method: init?.method ?? (init?.body ? 'POST' : 'GET'),
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: init?.body ? JSON.stringify(init.body) : undefined,
  })
  if (res.status === 401) throw new Unauthorized('Access token required')
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const j = await res.json()
      message = j.error ?? message
    } catch {
      /* not JSON */
    }
    throw new Error(message)
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

export function connectHub() {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hub', { accessTokenFactory: () => getToken() })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build()
}

// ---------- formatting ----------

export const fmt = (n: number | null | undefined, digits = 1) =>
  n == null || Number.isNaN(n) ? '–' : n.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits })
export const btc = (n: number | null | undefined) => (n == null ? '–' : `${n.toFixed(6)} BTC`)
export const time = (iso: string) => new Date(iso).toLocaleTimeString()

export function describe(o: Operand): string {
  switch (o.ind) {
    case 'value':
      return String(o.value ?? 0)
    case 'price':
      return o.source ?? 'close'
    case 'macd':
      return `MACD(${o.fast ?? 12},${o.slow ?? 26},${o.signal ?? 9}).${o.output ?? 'line'}`
    case 'bb':
      return `BB(${o.period ?? 20},${o.mult ?? 2}).${o.output ?? 'middle'}`
    default:
      return `${o.ind.toUpperCase()}(${o.period ?? 14}${o.source && o.source !== 'close' ? ',' + o.source : ''})`
  }
}
