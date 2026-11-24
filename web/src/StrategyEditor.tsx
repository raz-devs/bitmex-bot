import { useState } from 'react'
import { describe, TIMEFRAMES, type Comparison, type ConditionGroup, type Op, type Operand, type Strategy } from './api'

const INDICATORS: { value: Operand['ind']; label: string }[] = [
  { value: 'price', label: 'Price' },
  { value: 'ema', label: 'EMA' },
  { value: 'sma', label: 'SMA' },
  { value: 'rsi', label: 'RSI' },
  { value: 'macd', label: 'MACD' },
  { value: 'bb', label: 'Bollinger' },
  { value: 'value', label: 'Number' },
]
const OPS: { value: Op; label: string }[] = [
  { value: 'crossesAbove', label: 'crosses above' },
  { value: 'crossesBelow', label: 'crosses below' },
  { value: 'gt', label: 'is above' },
  { value: 'lt', label: 'is below' },
]

export const newStrategy = (): Strategy => ({
  id: Math.random().toString(36).slice(2, 10),
  name: 'New strategy',
  timeframe: '15m',
  evaluate: 'close',
  long: { mode: 'all', rules: [{ left: { ind: 'ema', period: 9 }, op: 'crossesAbove', right: { ind: 'ema', period: 21 } }] },
  short: { mode: 'all', rules: [{ left: { ind: 'ema', period: 9 }, op: 'crossesBelow', right: { ind: 'ema', period: 21 } }] },
  order: { type: 'Limit', contracts: 100, offsetTicks: 0, postOnly: true, timeoutBars: 2, takeProfitPct: 1, stopLossPct: 0.5 },
  enabled: true,
  autoTrade: false,
})

function Num({ label, value, onChange, step = 1, min }: { label: string; value: number | undefined; onChange: (v: number) => void; step?: number; min?: number }) {
  return (
    <label className="num">
      <span>{label}</span>
      <input type="number" value={value ?? ''} step={step} min={min} onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  )
}

function OperandEditor({ value, onChange }: { value: Operand; onChange: (o: Operand) => void }) {
  const set = (patch: Partial<Operand>) => onChange({ ...value, ...patch })
  return (
    <span className="operand">
      <select aria-label="Indicator" value={value.ind} onChange={(e) => onChange({ ind: e.target.value as Operand['ind'], period: e.target.value === 'bb' ? 20 : 14 })}>
        {INDICATORS.map((i) => (
          <option key={i.value} value={i.value}>
            {i.label}
          </option>
        ))}
      </select>
      {(value.ind === 'ema' || value.ind === 'sma' || value.ind === 'rsi' || value.ind === 'bb') && <Num label="period" value={value.period} min={1} onChange={(period) => set({ period })} />}
      {value.ind === 'bb' && <Num label="×σ" value={value.mult ?? 2} step={0.1} onChange={(mult) => set({ mult })} />}
      {value.ind === 'macd' && (
        <>
          <Num label="fast" value={value.fast ?? 12} onChange={(fast) => set({ fast })} />
          <Num label="slow" value={value.slow ?? 26} onChange={(slow) => set({ slow })} />
          <Num label="signal" value={value.signal ?? 9} onChange={(signal) => set({ signal })} />
        </>
      )}
      {(value.ind === 'macd' || value.ind === 'bb') && (
        <select aria-label="Line" value={value.output ?? (value.ind === 'macd' ? 'line' : 'middle')} onChange={(e) => set({ output: e.target.value })}>
          {(value.ind === 'macd' ? ['line', 'signal', 'hist'] : ['upper', 'middle', 'lower']).map((o) => (
            <option key={o}>{o}</option>
          ))}
        </select>
      )}
      {value.ind !== 'value' && value.ind !== 'rsi' && (
        <select aria-label="Source" value={value.source ?? 'close'} onChange={(e) => set({ source: e.target.value as Operand['source'] })}>
          {['close', 'open', 'high', 'low', 'volume'].map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
      )}
      {value.ind === 'value' && <Num label="" value={value.value ?? 0} step={0.1} onChange={(v) => set({ value: v })} />}
    </span>
  )
}

function GroupEditor({ title, group, onChange }: { title: string; group: ConditionGroup | null; onChange: (g: ConditionGroup | null) => void }) {
  if (!group)
    return (
      <div className="group">
        <div className="group-head">
          <strong>{title}</strong>
          <button onClick={() => onChange({ mode: 'all', rules: [{ left: { ind: 'price' }, op: 'gt', right: { ind: 'ema', period: 50 } }] })}>+ Add conditions</button>
        </div>
      </div>
    )
  const setRule = (i: number, r: Comparison) => onChange({ ...group, rules: group.rules.map((x, j) => (j === i ? r : x)) })
  return (
    <div className="group">
      <div className="group-head">
        <strong>{title}</strong> when
        <select aria-label={`${title} match mode`} value={group.mode} onChange={(e) => onChange({ ...group, mode: e.target.value as 'all' | 'any' })}>
          <option value="all">all of these are true</option>
          <option value="any">any of these is true</option>
        </select>
        <button className="link" onClick={() => onChange(null)}>
          remove
        </button>
      </div>
      {group.rules.map((r, i) => (
        <div className="rule" key={i}>
          <OperandEditor value={r.left} onChange={(left) => setRule(i, { ...r, left })} />
          <select aria-label="Operator" value={r.op} onChange={(e) => setRule(i, { ...r, op: e.target.value as Op })}>
            {OPS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
          <OperandEditor value={r.right} onChange={(right) => setRule(i, { ...r, right })} />
          <button className="icon" aria-label="Remove condition" onClick={() => onChange(group.rules.length === 1 ? null : { ...group, rules: group.rules.filter((_, j) => j !== i) })}>
            ×
          </button>
        </div>
      ))}
      <button onClick={() => onChange({ ...group, rules: [...group.rules, { left: { ind: 'rsi', period: 14 }, op: 'lt', right: { ind: 'value', value: 70 } }] })}>+ Condition</button>
    </div>
  )
}

type Props = {
  strategy: Strategy
  onSave: (s: Strategy) => Promise<void>
  onDelete: (id: string) => Promise<void>
  onBacktest: (s: Strategy) => void
  onShowOnChart: (operands: Operand[]) => void
}

export function StrategyEditor({ strategy, onSave, onDelete, onBacktest, onShowOnChart }: Props) {
  const [s, setS] = useState(strategy)
  const [saving, setSaving] = useState(false)
  const order = (patch: Partial<Strategy['order']>) => setS({ ...s, order: { ...s.order, ...patch } })
  const operands = [...(s.long?.rules ?? []), ...(s.short?.rules ?? [])].flatMap((r) => [r.left, r.right]).filter((o) => o.ind !== 'value' && o.ind !== 'price')
  const unique = operands.filter((o, i) => operands.findIndex((x) => describe(x) === describe(o)) === i)

  return (
    <div className="editor">
      <input className="name" aria-label="Strategy name" value={s.name} onChange={(e) => setS({ ...s, name: e.target.value })} />
      <div className="row">
        <select aria-label="Timeframe" value={s.timeframe} onChange={(e) => setS({ ...s, timeframe: e.target.value })}>
          {TIMEFRAMES.map((t) => (
            <option key={t}>{t}</option>
          ))}
        </select>
        <select aria-label="Evaluate" value={s.evaluate} onChange={(e) => setS({ ...s, evaluate: e.target.value as Strategy['evaluate'] })}>
          <option value="close">check on candle close</option>
          <option value="tick">check on every trade (earliest)</option>
        </select>
      </div>
      <p className="hint">
        {s.evaluate === 'tick'
          ? 'Fires as soon as the forming candle meets the conditions. Earliest signal, but the condition can still undo itself before the candle closes.'
          : 'Fires when a candle closes and meets the conditions. Confirmed, but up to one candle later.'}
      </p>

      <GroupEditor title="Go long" group={s.long} onChange={(long) => setS({ ...s, long })} />
      <GroupEditor title="Go short" group={s.short} onChange={(short) => setS({ ...s, short })} />

      <div className="group">
        <div className="group-head">
          <strong>Order</strong>
        </div>
        <div className="row wrap">
          <select aria-label="Entry type" value={s.order.type} onChange={(e) => order({ type: e.target.value as 'Limit' | 'Market' })}>
            <option value="Limit">Limit entry</option>
            <option value="Market">Market entry</option>
          </select>
          <Num label="contracts" value={s.order.contracts} min={1} onChange={(contracts) => order({ contracts })} />
          <Num label="TP %" value={s.order.takeProfitPct} step={0.1} min={0.01} onChange={(takeProfitPct) => order({ takeProfitPct })} />
          <Num label="SL %" value={s.order.stopLossPct} step={0.1} min={0.01} onChange={(stopLossPct) => order({ stopLossPct })} />
          {s.order.type === 'Limit' && (
            <>
              <Num label="offset ticks" value={s.order.offsetTicks} min={0} onChange={(offsetTicks) => order({ offsetTicks })} />
              <Num label="cancel after bars" value={s.order.timeoutBars} min={1} onChange={(timeoutBars) => order({ timeoutBars })} />
              <label className="check">
                <input type="checkbox" checked={s.order.postOnly} onChange={(e) => order({ postOnly: e.target.checked })} /> post-only
              </label>
            </>
          )}
        </div>
      </div>

      <div className="row wrap">
        <label className="check">
          <input type="checkbox" checked={s.enabled} onChange={(e) => setS({ ...s, enabled: e.target.checked })} /> Enabled
        </label>
        <label className="check">
          <input type="checkbox" checked={s.autoTrade} onChange={(e) => setS({ ...s, autoTrade: e.target.checked })} /> Place orders automatically
        </label>
      </div>

      <div className="row wrap actions">
        <button
          className="primary"
          disabled={saving}
          onClick={async () => {
            setSaving(true)
            try {
              await onSave(s)
            } finally {
              setSaving(false)
            }
          }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button onClick={() => onBacktest(s)}>Backtest</button>
        <button onClick={() => onShowOnChart(unique)} disabled={unique.length === 0}>
          Show indicators on chart
        </button>
        <button className="danger" onClick={() => onDelete(s.id)}>
          Delete
        </button>
      </div>
    </div>
  )
}
