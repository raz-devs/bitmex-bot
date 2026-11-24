import { useCallback, useEffect, useState } from 'react'
import type { HubConnection } from '@microsoft/signalr'
import {
  api,
  connectHub,
  describe,
  fmt,
  setToken,
  time,
  TIMEFRAMES,
  Unauthorized,
  type Margin,
  type Operand,
  type Order,
  type Position,
  type SignalEvent,
  type State,
  type Status,
  type Strategy,
  type TradeView,
} from './api'
import { Chart, type ChartIndicator } from './Chart'
import { OrderPanel } from './OrderPanel'
import { StrategyEditor, newStrategy } from './StrategyEditor'
import { Backtest } from './Backtest'

const PALETTE = ['#4f8cff', '#f5a524', '#c084fc', '#22d3ee', '#f472b6', '#a3e635']
const QUICK: { label: string; operand: Operand }[] = [
  { label: 'EMA 9', operand: { ind: 'ema', period: 9 } },
  { label: 'EMA 21', operand: { ind: 'ema', period: 21 } },
  { label: 'SMA 50', operand: { ind: 'sma', period: 50 } },
  { label: 'SMA 200', operand: { ind: 'sma', period: 200 } },
  { label: 'Bollinger 20', operand: { ind: 'bb', period: 20, mult: 2 } },
  { label: 'RSI 14', operand: { ind: 'rsi', period: 14 } },
  { label: 'MACD 12/26/9', operand: { ind: 'macd', fast: 12, slow: 26, signal: 9 } },
]

function load<T>(key: string, fallback: T): T {
  try {
    const v = localStorage.getItem(key)
    return v ? (JSON.parse(v) as T) : fallback
  } catch {
    return fallback
  }
}
function save(key: string, v: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(v))
  } catch {
    /* not persisted */
  }
}

type Tab = 'trade' | 'strategies' | 'backtest' | 'activity'
type Toast = { id: number; message: string; error: boolean }

export default function App() {
  const [needToken, setNeedToken] = useState(false)
  const [hub, setHub] = useState<HubConnection | null>(null)
  const [status, setStatus] = useState<Status | null>(null)
  const [position, setPosition] = useState<Position | null>(null)
  const [margin, setMargin] = useState<Margin | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [trades, setTrades] = useState<TradeView[]>([])
  const [signals, setSignals] = useState<SignalEvent[]>([])
  const [log, setLog] = useState<{ time: string; message: string }[]>([])
  const [strategies, setStrategies] = useState<Strategy[]>([])
  const [selected, setSelected] = useState<Strategy | null>(null)
  const [tf, setTf] = useState<string>(() => load('bb-tf', '15m'))
  const [indicators, setIndicators] = useState<ChartIndicator[]>(() => load('bb-indicators', [{ id: 'a', operand: { ind: 'ema', period: 21 }, color: PALETTE[0] }]))
  const [tab, setTab] = useState<Tab>('trade')
  const [toasts, setToasts] = useState<Toast[]>([])

  const notify = useCallback((message: string, error = false) => {
    const id = Date.now() + Math.random()
    setToasts((t) => [...t, { id, message, error }])
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), error ? 8000 : 3000)
  }, [])

  useEffect(() => save('bb-tf', tf), [tf])
  useEffect(() => save('bb-indicators', indicators), [indicators])

  // Initial state + live connection.
  useEffect(() => {
    if (needToken) return
    let stopped = false
    const h = connectHub()
    const apply = (s: State) => {
      setStatus(s.status)
      setPosition(s.position)
      setMargin(s.margin)
      setOrders(s.orders)
      setTrades(s.trades)
      setSignals(s.signals)
    }
    h.on('status', setStatus)
    h.on('position', setPosition)
    h.on('margin', setMargin)
    h.on('orders', setOrders)
    h.on('trades', setTrades)
    h.on('signal', (s: SignalEvent) => {
      setSignals((list) => [s, ...list].slice(0, 200))
      notify(`${s.side} signal: ${s.strategy} (${s.timeframe}) at ${fmt(s.price)}. ${s.action}`)
    })
    h.on('log', (l: { time: string; message: string }) => {
      setLog((list) => [l, ...list].slice(0, 100))
      notify(l.message, true)
    })
    h.onreconnected(() => api<State>('/api/state').then(apply).catch(() => {}))
    Promise.all([api<State>('/api/state'), api<Strategy[]>('/api/strategies')])
      .then(([s, st]) => {
        apply(s)
        setStrategies(st)
        return h.start()
      })
      .then(() => {
        if (!stopped) setHub(h)
      })
      .catch((e) => {
        if (e instanceof Unauthorized) setNeedToken(true)
        else notify(`Can't reach the server: ${e.message}`, true)
      })
    return () => {
      stopped = true
      h.stop()
    }
  }, [needToken, notify])

  useEffect(() => {
    hub?.invoke('Watch', tf, null).catch(() => {})
  }, [hub, tf])

  if (needToken)
    return (
      <form
        className="login"
        onSubmit={(e) => {
          e.preventDefault()
          setToken(new FormData(e.currentTarget).get('token') as string)
          setNeedToken(false)
        }}
      >
        <h1>Access token</h1>
        <p>This server requires the token set in App:AccessToken.</p>
        <input name="token" type="password" autoFocus aria-label="Access token" />
        <button className="primary">Continue</button>
      </form>
    )

  const saveStrategy = async (s: Strategy) => {
    try {
      const saved = await api<Strategy>('/api/strategies', { body: s })
      setStrategies((list) => [...list.filter((x) => x.id !== saved.id), saved])
      setSelected(saved)
      notify(`Saved ${saved.name}`)
    } catch (e) {
      notify((e as Error).message, true)
    }
  }
  const deleteStrategy = async (id: string) => {
    try {
      await api(`/api/strategies/${id}`, { method: 'DELETE' })
    } catch {
      /* not saved yet */
    }
    setStrategies((list) => list.filter((x) => x.id !== id))
    setSelected(null)
  }
  const addIndicator = (operand: Operand) =>
    setIndicators((list) => (list.some((i) => describe(i.operand) === describe(operand)) ? list : [...list, { id: Math.random().toString(36).slice(2), operand, color: PALETTE[list.length % PALETTE.length] }]))
  const toggleAuto = async () => {
    try {
      await api('/api/autotrade', { body: { enabled: !status?.autoTrading } })
      setStatus((s) => (s ? { ...s, autoTrading: !s.autoTrading } : s))
    } catch (e) {
      notify((e as Error).message, true)
    }
  }
  const kill = async () => {
    if (!window.confirm('Cancel every order, close the position and switch auto trading off?')) return
    try {
      await api('/api/kill', { method: 'POST' })
    } catch (e) {
      notify((e as Error).message, true)
    }
  }

  const mode = !status ? '' : status.exchange === 'paper' ? 'PAPER' : status.testnet ? 'TESTNET' : 'LIVE'

  return (
    <div className="app">
      <header>
        <div className="brand">
          <span className={`dot ${status?.connected ? 'ok' : 'bad'}`} title={status?.connected ? 'Connected' : 'Disconnected'} />
          <strong>{status?.symbol ?? '…'}</strong>
          {mode && <span className={`badge ${mode.toLowerCase()}`}>{mode}</span>}
        </div>
        <div className="price">{fmt(status?.lastPrice)}</div>
        <dl className="ticker">
          <dt>Bid / Ask</dt>
          <dd>
            {fmt(status?.bestBid)} / {fmt(status?.bestAsk)}
          </dd>
          <dt>Book</dt>
          <dd className={(status?.bookImbalance ?? 0) >= 0 ? 'pos' : 'neg'}>{status?.bookImbalance == null ? '–' : `${(status.bookImbalance * 100).toFixed(0)}%`}</dd>
          <dt>Feed lag</dt>
          <dd>{fmt(status?.feedLagMs)} ms</dd>
          <dt>Signal calc</dt>
          <dd>{fmt(status?.evalMicros)} µs</dd>
        </dl>
        <div className="spacer" />
        <button className={`auto ${status?.autoTrading ? 'on' : ''}`} onClick={toggleAuto} title={status?.autoTradingOffReason ?? 'Auto trading is on'} aria-pressed={status?.autoTrading ?? false}>
          Auto trading {status?.autoTrading ? 'ON' : 'OFF'}
        </button>
        <button className="kill" onClick={kill}>
          Kill switch
        </button>
      </header>

      <main>
        <section className="chart-area">
          <div className="toolbar">
            <div className="seg" role="radiogroup" aria-label="Timeframe">
              {TIMEFRAMES.map((t) => (
                <button key={t} role="radio" aria-checked={t === tf} className={t === tf ? 'on' : ''} onClick={() => setTf(t)}>
                  {t}
                </button>
              ))}
            </div>
            <select aria-label="Add indicator" value="" onChange={(e) => e.target.value && addIndicator(QUICK[Number(e.target.value)].operand)}>
              <option value="">+ Indicator</option>
              {QUICK.map((q, i) => (
                <option key={q.label} value={i}>
                  {q.label}
                </option>
              ))}
            </select>
            {indicators.map((i) => (
              <span className="chip" key={i.id} style={{ borderColor: i.color }}>
                {describe(i.operand).replace(/\.(middle|line)$/, '')}
                <button className="icon" aria-label={`Remove ${describe(i.operand)}`} onClick={() => setIndicators((list) => list.filter((x) => x.id !== i.id))}>
                  ×
                </button>
              </span>
            ))}
          </div>
          <Chart hub={hub} tf={tf} indicators={indicators} signals={signals} position={position} orders={orders} trades={trades} onError={(m) => notify(m, true)} />
        </section>

        <aside>
          <nav className="tabs" role="tablist">
            {(
              [
                ['trade', 'Trade'],
                ['strategies', 'Strategies'],
                ['backtest', 'Backtest'],
                ['activity', 'Activity'],
              ] as [Tab, string][]
            ).map(([k, label]) => (
              <button key={k} role="tab" aria-selected={tab === k} className={tab === k ? 'on' : ''} onClick={() => setTab(k)}>
                {label}
              </button>
            ))}
          </nav>

          {tab === 'trade' && <OrderPanel status={status} position={position} margin={margin} notify={notify} />}

          {tab === 'strategies' && (
            <div className="panel-body">
              {!selected && (
                <>
                  <ul className="list">
                    {strategies.map((s) => (
                      <li key={s.id}>
                        <button className="list-item" onClick={() => setSelected(s)}>
                          <strong>{s.name}</strong>
                          <span>
                            {s.timeframe} · {s.evaluate === 'tick' ? 'every trade' : 'on close'} · {!s.enabled ? 'disabled' : s.autoTrade ? 'auto orders' : 'alerts'}
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                  <button className="primary" onClick={() => setSelected(newStrategy())}>
                    + New strategy
                  </button>
                </>
              )}
              {selected && (
                <>
                  <button className="link" onClick={() => setSelected(null)}>
                    ← All strategies
                  </button>
                  <StrategyEditor
                    key={selected.id}
                    strategy={selected}
                    onSave={saveStrategy}
                    onDelete={deleteStrategy}
                    onBacktest={(s) => {
                      setSelected(s)
                      setTab('backtest')
                    }}
                    onShowOnChart={(ops) => {
                      ops.forEach(addIndicator)
                      setTf(selected.timeframe)
                    }}
                  />
                </>
              )}
            </div>
          )}

          {tab === 'backtest' && <Backtest strategy={selected} notify={notify} />}

          {tab === 'activity' && (
            <div className="panel-body">
              <h3>Working orders</h3>
              {orders.length === 0 ? (
                <p className="hint">None</p>
              ) : (
                <table className="table">
                  <tbody>
                    {orders.map((o) => (
                      <tr key={o.orderId}>
                        <td className={o.side === 'Buy' ? 'pos' : 'neg'}>{o.side}</td>
                        <td>{o.qty}</td>
                        <td>{o.type}</td>
                        <td>{fmt(o.price ?? o.stopPrice)}</td>
                        <td>{o.reduceOnly ? 'exit' : 'entry'}</td>
                        <td>
                          <button className="icon" aria-label="Cancel order" onClick={() => api(`/api/orders/${encodeURIComponent(o.orderId)}/cancel`, { method: 'POST' }).catch((e) => notify(e.message, true))}>
                            ×
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <h3>Trades</h3>
              {trades.length === 0 ? (
                <p className="hint">None yet</p>
              ) : (
                <table className="table">
                  <tbody>
                    {trades.slice(0, 30).map((t) => (
                      <tr key={t.id}>
                        <td className={t.side === 'Buy' ? 'pos' : 'neg'}>{t.side}</td>
                        <td>
                          {t.filledQty}/{t.qty}
                        </td>
                        <td>{fmt(t.avgEntry)}</td>
                        <td>{t.state}</td>
                        <td className="muted">{t.note ?? (t.source === 'manual' ? 'manual' : strategies.find((s) => s.id === t.source)?.name ?? t.source)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <h3>Signals</h3>
              {signals.length === 0 ? (
                <p className="hint">None yet</p>
              ) : (
                <table className="table">
                  <tbody>
                    {signals.slice(0, 50).map((s, i) => (
                      <tr key={i}>
                        <td>{time(s.time)}</td>
                        <td className={s.side === 'Long' ? 'pos' : 'neg'}>{s.side}</td>
                        <td>{s.strategy}</td>
                        <td>{fmt(s.price)}</td>
                        <td className="muted" title={`${fmt(s.latencyMs)} ms after the data`}>
                          {s.action}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {log.length > 0 && (
                <>
                  <h3>Log</h3>
                  <ul className="log">
                    {log.map((l, i) => (
                      <li key={i}>
                        {time(l.time)} {l.message}
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
          )}
        </aside>
      </main>

      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast ${t.error ? 'error' : ''}`}>
            {t.message}
          </div>
        ))}
      </div>
    </div>
  )
}
