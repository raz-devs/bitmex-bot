import { useEffect, useState } from 'react'
import { api, fmt, TIMEFRAMES, type BacktestResult, type Strategy } from './api'

export function Backtest({ strategy, notify }: { strategy: Strategy | null; notify: (m: string, error?: boolean) => void }) {
  const [datasets, setDatasets] = useState<string[]>([])
  const [dataset, setDataset] = useState('')
  const [datasetTf, setDatasetTf] = useState('5m')
  const [bars, setBars] = useState(2000)
  const [result, setResult] = useState<BacktestResult | null>(null)
  const [running, setRunning] = useState(false)

  useEffect(() => {
    api<string[]>('/api/datasets').then(setDatasets).catch(() => {})
  }, [])

  if (!strategy) return <div className="panel-body hint">Pick a strategy in the Strategies tab, then press Backtest.</div>

  const run = async () => {
    setRunning(true)
    try {
      setResult(await api<BacktestResult>('/api/backtest', { body: { strategy, dataset: dataset || null, datasetTimeframe: dataset ? datasetTf : null, bars } }))
    } catch (e) {
      notify((e as Error).message, true)
    } finally {
      setRunning(false)
    }
  }

  const r = result
  return (
    <div className="panel-body">
      <p>
        <strong>{strategy.name}</strong> on {strategy.timeframe}
      </p>
      <label className="field">
        <span>Data</span>
        <select value={dataset} onChange={(e) => setDataset(e.target.value)}>
          <option value="">Exchange history</option>
          {datasets.map((d) => (
            <option key={d}>{d}</option>
          ))}
        </select>
      </label>
      {dataset ? (
        <label className="field">
          <span>Bar size in the file</span>
          <select value={datasetTf} onChange={(e) => setDatasetTf(e.target.value)}>
            {TIMEFRAMES.map((t) => (
              <option key={t}>{t}</option>
            ))}
          </select>
        </label>
      ) : (
        <label className="field">
          <span>Bars</span>
          <input type="number" min={100} max={20000} value={bars} onChange={(e) => setBars(Number(e.target.value))} />
        </label>
      )}
      <p className="hint">Put CSV files (time,open,high,low,close,volume with time = bar close in UTC) in the server's data folder to test on real history. The paper exchange's history is random and only good for checking that a strategy runs.</p>
      <button className="primary" onClick={run} disabled={running}>
        {running ? 'Running…' : 'Run backtest'}
      </button>

      {r && (
        <>
          <dl className="stats">
            <dt>Bars</dt>
            <dd>
              {r.bars} ({new Date(r.from).toLocaleDateString()} – {new Date(r.to).toLocaleDateString()})
            </dd>
            <dt>Signals / missed</dt>
            <dd>
              {r.signals} / {r.missedEntries}
            </dd>
            <dt>Trades</dt>
            <dd>{r.trades}</dd>
            <dt>Win rate</dt>
            <dd>{fmt(r.winRate * 100)}%</dd>
            <dt>Avg win / loss</dt>
            <dd>
              {r.avgWinXbt.toFixed(6)} / {r.avgLossXbt.toFixed(6)}
            </dd>
            <dt>Expectancy / trade</dt>
            <dd className={r.expectancyXbt > 0 ? 'pos' : 'neg'}>{r.expectancyXbt.toFixed(8)}</dd>
            <dt>Profit factor</dt>
            <dd>{r.profitFactor == null ? '∞' : r.profitFactor.toFixed(2)}</dd>
            <dt>Fees</dt>
            <dd>{r.feesXbt.toFixed(6)}</dd>
            <dt>Net PnL (BTC)</dt>
            <dd className={r.netXbt > 0 ? 'pos' : 'neg'}>{r.netXbt.toFixed(6)}</dd>
            <dt>Max drawdown</dt>
            <dd>{r.maxDrawdownXbt.toFixed(6)}</dd>
          </dl>
          <p className={r.expectancyXbt > 0 ? 'hint pos' : 'hint neg'}>
            {r.expectancyXbt > 0 ? 'Positive on this data. Check other periods before trusting it.' : 'Negative expectancy: this setup loses money on this data.'}
          </p>
          <table className="table">
            <thead>
              <tr>
                <th>Side</th>
                <th>Entry</th>
                <th>Exit</th>
                <th>Reason</th>
                <th>PnL</th>
              </tr>
            </thead>
            <tbody>
              {r.tradeList
                .slice(-100)
                .reverse()
                .map((t, i) => (
                  <tr key={i}>
                    <td>{t.side}</td>
                    <td>{fmt(t.entryPrice)}</td>
                    <td>{fmt(t.exitPrice)}</td>
                    <td>{t.exitReason}</td>
                    <td className={t.pnlXbt > 0 ? 'pos' : 'neg'}>{t.pnlXbt.toFixed(6)}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}
