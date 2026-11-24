import { useState } from 'react'
import { api, btc, fmt, type Margin, type Position, type Status } from './api'

type Props = { status: Status | null; position: Position | null; margin: Margin | null; notify: (m: string, error?: boolean) => void }

export function OrderPanel({ status, position, margin, notify }: Props) {
  const [type, setType] = useState<'Limit' | 'Market'>('Limit')
  const [contracts, setContracts] = useState(100)
  const [price, setPrice] = useState('')
  const [postOnly, setPostOnly] = useState(true)
  const [useTp, setUseTp] = useState(true)
  const [tp, setTp] = useState(1)
  const [useSl, setUseSl] = useState(true)
  const [sl, setSl] = useState(0.5)
  const [busy, setBusy] = useState(false)

  const send = async (side: 'Buy' | 'Sell') => {
    setBusy(true)
    try {
      await api('/api/orders', {
        body: {
          side,
          type,
          contracts,
          limitPrice: type === 'Limit' && price ? Number(price) : null,
          postOnly,
          takeProfitPct: useTp ? tp : null,
          stopLossPct: useSl ? sl : null,
        },
      })
      notify(`${side} ${contracts} ${type.toLowerCase()} sent`)
    } catch (e) {
      notify((e as Error).message, true)
    } finally {
      setBusy(false)
    }
  }

  const action = async (path: string, done: string) => {
    try {
      await api(path, { method: 'POST' })
      notify(done)
    } catch (e) {
      notify((e as Error).message, true)
    }
  }

  return (
    <div className="panel-body">
      <div className="seg" role="radiogroup" aria-label="Order type">
        {(['Limit', 'Market'] as const).map((t) => (
          <button key={t} role="radio" aria-checked={type === t} className={type === t ? 'on' : ''} onClick={() => setType(t)}>
            {t}
          </button>
        ))}
      </div>
      <label className="field">
        <span>Contracts (USD)</span>
        <input type="number" min={1} value={contracts} onChange={(e) => setContracts(Number(e.target.value))} />
      </label>
      {type === 'Limit' && (
        <>
          <label className="field">
            <span>Limit price</span>
            <input type="number" step={0.5} placeholder={`best bid/ask (${fmt(status?.bestBid)} / ${fmt(status?.bestAsk)})`} value={price} onChange={(e) => setPrice(e.target.value)} />
          </label>
          <label className="check">
            <input type="checkbox" checked={postOnly} onChange={(e) => setPostOnly(e.target.checked)} /> Post-only (maker fee, cancelled if it would take)
          </label>
        </>
      )}
      <div className="row">
        <label className="check">
          <input type="checkbox" checked={useTp} onChange={(e) => setUseTp(e.target.checked)} /> Take profit %
        </label>
        <input type="number" aria-label="Take profit percent" step={0.1} min={0.01} value={tp} disabled={!useTp} onChange={(e) => setTp(Number(e.target.value))} />
      </div>
      <div className="row">
        <label className="check">
          <input type="checkbox" checked={useSl} onChange={(e) => setUseSl(e.target.checked)} /> Stop loss %
        </label>
        <input type="number" aria-label="Stop loss percent" step={0.1} min={0.01} value={sl} disabled={!useSl} onChange={(e) => setSl(Number(e.target.value))} />
      </div>
      <div className="row buttons">
        <button className="buy" disabled={busy || !status?.canTrade} onClick={() => send('Buy')}>
          Buy / Long
        </button>
        <button className="sell" disabled={busy || !status?.canTrade} onClick={() => send('Sell')}>
          Sell / Short
        </button>
      </div>
      {!status?.canTrade && <p className="hint">Trading is off: the exchange has no API key configured on the server.</p>}

      <h3>Position</h3>
      <dl className="stats">
        <dt>Size</dt>
        <dd className={position && position.qty > 0 ? 'pos' : position && position.qty < 0 ? 'neg' : ''}>{position?.qty ?? 0}</dd>
        <dt>Entry</dt>
        <dd>{fmt(position?.avgEntry)}</dd>
        <dt>Unrealised</dt>
        <dd className={(position?.unrealisedBtc ?? 0) >= 0 ? 'pos' : 'neg'}>{btc(position?.unrealisedBtc)}</dd>
        <dt>Realised</dt>
        <dd>{btc(position?.realisedBtc)}</dd>
        <dt>Equity</dt>
        <dd>{btc(margin?.equityBtc)}</dd>
      </dl>
      <div className="row buttons">
        <button onClick={() => action('/api/position/close', 'Position closed')} disabled={!position?.qty}>
          Close position
        </button>
        <button onClick={() => action('/api/orders/cancel-all', 'Orders cancelled')}>Cancel all orders</button>
      </div>
    </div>
  )
}
