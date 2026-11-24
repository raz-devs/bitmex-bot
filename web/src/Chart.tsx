import { useEffect, useRef } from 'react'
import {
  CandlestickSeries,
  ColorType,
  createChart,
  createSeriesMarkers,
  HistogramSeries,
  LineSeries,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type ISeriesMarkersPluginApi,
  type SeriesMarker,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts'
import type { HubConnection } from '@microsoft/signalr'
import { api, describe, type Candle, type Operand, type Order, type Position, type SignalEvent, type TradeView } from './api'
import { compute, ownPane } from './indicators'

export type ChartIndicator = { id: string; operand: Operand; color: string }

type Props = {
  hub: HubConnection | null
  tf: string
  indicators: ChartIndicator[]
  signals: SignalEvent[]
  position: Position | null
  orders: Order[]
  trades: TradeView[]
  onError: (message: string) => void
}

const TF_SECONDS: Record<string, number> = { '1m': 60, '5m': 300, '15m': 900, '30m': 1800, '1h': 3600, '4h': 14400, '1d': 86400 }

/** Lines drawn for one chart indicator: MACD and Bollinger expand into three. */
function outputs(o: Operand): { operand: Operand; label: string; histogram?: boolean }[] {
  if (o.ind === 'macd')
    return [
      { operand: { ...o, output: 'line' }, label: 'MACD' },
      { operand: { ...o, output: 'signal' }, label: 'signal' },
      { operand: { ...o, output: 'hist' }, label: 'hist', histogram: true },
    ]
  if (o.ind === 'bb') return ['upper', 'middle', 'lower'].map((output) => ({ operand: { ...o, output }, label: `BB ${output}` }))
  return [{ operand: o, label: describe(o) }]
}

const css = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim()

export function Chart({ hub, tf, indicators, signals, position, orders, trades, onError }: Props) {
  const box = useRef<HTMLDivElement>(null)
  const chart = useRef<IChartApi | null>(null)
  const candles = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const volume = useRef<ISeriesApi<'Histogram'> | null>(null)
  const markers = useRef<ISeriesMarkersPluginApi<Time> | null>(null)
  const bars = useRef<Candle[]>([])
  const lines = useRef<{ series: ISeriesApi<'Line' | 'Histogram'>; operand: Operand }[]>([])
  const priceLines = useRef<IPriceLine[]>([])

  // Chart, once.
  useEffect(() => {
    const c = createChart(box.current!, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: css('--panel') }, textColor: css('--muted'), panes: { separatorColor: css('--border') } },
      grid: { vertLines: { color: css('--grid') }, horzLines: { color: css('--grid') } },
      timeScale: { timeVisible: true, secondsVisible: false, borderColor: css('--border') },
      rightPriceScale: { borderColor: css('--border') },
      crosshair: { mode: 0 },
    })
    candles.current = c.addSeries(CandlestickSeries, {
      upColor: css('--up'),
      downColor: css('--down'),
      wickUpColor: css('--up'),
      wickDownColor: css('--down'),
      borderVisible: false,
    })
    volume.current = c.addSeries(HistogramSeries, { priceScaleId: 'vol', priceFormat: { type: 'volume' }, lastValueVisible: false, priceLineVisible: false })
    volume.current.priceScale().applyOptions({ scaleMargins: { top: 0.82, bottom: 0 } })
    markers.current = createSeriesMarkers(candles.current, [])
    chart.current = c
    return () => c.remove()
  }, [])

  const redrawIndicators = () => {
    const all = bars.current
    for (const l of lines.current) {
      const values = compute(all, l.operand)
      l.series.setData(all.flatMap((b, i) => (values[i] == null ? [] : [{ time: b.time as UTCTimestamp, value: values[i]! }])))
    }
  }

  const updateLast = () => {
    const all = bars.current
    const last = all[all.length - 1]
    if (!last) return
    // Recomputing from scratch is O(bars) but tiny (5000 bars), and keeps the chart exactly in sync with history edits.
    for (const l of lines.current) {
      const values = compute(all, l.operand)
      const v = values[values.length - 1]
      if (v != null) l.series.update({ time: last.time as UTCTimestamp, value: v })
    }
  }

  // History + live candles for the selected timeframe.
  useEffect(() => {
    let cancelled = false
    bars.current = []
    candles.current?.setData([])
    volume.current?.setData([])
    api<Candle[]>(`/api/candles?tf=${tf}`)
      .then((data) => {
        if (cancelled) return
        bars.current = data
        candles.current!.setData(data.map((b) => ({ ...b, time: b.time as UTCTimestamp })))
        volume.current!.setData(data.map(volumeBar))
        redrawIndicators()
        chart.current!.timeScale().scrollToRealTime()
      })
      .catch((e) => onError(`Candles: ${e.message}`))

    const onCandle = (barTf: string, b: Candle) => {
      if (barTf !== tf || cancelled) return
      const all = bars.current
      const last = all[all.length - 1]
      if (last && b.time < last.time) return
      if (last && b.time === last.time) all[all.length - 1] = b
      else all.push(b)
      candles.current!.update({ ...b, time: b.time as UTCTimestamp })
      volume.current!.update(volumeBar(b))
      updateLast()
    }
    hub?.on('candle', onCandle)
    if (hub?.state === 'Connected') hub.invoke('Watch', tf, null).catch(() => {})
    return () => {
      cancelled = true
      hub?.off('candle', onCandle)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tf, hub])

  // Indicator series: rebuilt whenever the list changes.
  useEffect(() => {
    const c = chart.current!
    for (const l of lines.current) c.removeSeries(l.series)
    lines.current = []
    let pane = 0
    for (const ind of indicators) {
      const paneIndex = ownPane(ind.operand) ? ++pane : 0
      for (const out of outputs(ind.operand)) {
        const opts = { color: out.label === 'signal' ? css('--accent-2') : ind.color, lineWidth: 2 as const, title: out.label, priceLineVisible: false, lastValueVisible: true }
        const series = out.histogram ? c.addSeries(HistogramSeries, { ...opts, color: css('--muted') }, paneIndex) : c.addSeries(LineSeries, opts, paneIndex)
        lines.current.push({ series, operand: out.operand })
      }
    }
    c.panes().forEach((p, i) => p.setStretchFactor(i === 0 ? 3 : 1))
    redrawIndicators()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [indicators])

  // Signal markers for this timeframe.
  useEffect(() => {
    const size = TF_SECONDS[tf]
    const list: SeriesMarker<Time>[] = signals
      .filter((s) => s.timeframe === tf)
      .map((s) => {
        const t = new Date(s.time).getTime() / 1000 - (s.onClose ? size : 0)
        const long = s.side === 'Long'
        return {
          time: (Math.floor(t / size) * size) as UTCTimestamp,
          position: long ? 'belowBar' : 'aboveBar',
          color: long ? css('--up') : css('--down'),
          shape: long ? 'arrowUp' : 'arrowDown',
          text: s.strategy,
        } satisfies SeriesMarker<Time>
      })
      .sort((a, b) => (a.time as number) - (b.time as number))
    markers.current?.setMarkers(list)
  }, [signals, tf])

  // Price lines: position entry, TP/SL of open trades, working orders.
  useEffect(() => {
    const s = candles.current!
    for (const l of priceLines.current) s.removePriceLine(l)
    priceLines.current = []
    const add = (price: number | null | undefined, color: string, title: string, dashed = true) => {
      if (price == null) return
      priceLines.current.push(s.createPriceLine({ price, color, title, lineWidth: 1, lineStyle: dashed ? 2 : 0, axisLabelVisible: true }))
    }
    if (position && position.qty !== 0) add(position.avgEntry, css('--accent'), `entry ${position.qty}`, false)
    for (const t of trades.filter((t) => t.state === 'Open')) {
      add(t.takeProfit, css('--up'), 'TP')
      add(t.stopLoss, css('--down'), 'SL')
    }
    for (const o of orders.filter((o) => !o.reduceOnly)) add(o.price ?? o.stopPrice, css('--muted'), `${o.side} ${o.qty}`)
  }, [position, orders, trades])

  return <div className="chart" ref={box} />
}

function volumeBar(b: Candle) {
  return { time: b.time as UTCTimestamp, value: b.volume, color: b.close >= b.open ? css('--up-faint') : css('--down-faint') }
}
