import type {
  ActivityPoint,
  DayBucket,
  DeviceSlice,
  EnergyHour,
  EnergyPoint,
  HeatmapCell,
  HouseholdBar,
  ModelBar,
  OutdoorPoint,
  RiskSlice,
} from '@/services/analytics';

/** Wh under 1000 stays Wh: a family reads "780Wh" faster than "0.78kWh". */
export function formatWh(wh: number): string {
  return wh >= 1000
    ? `${(wh / 1000).toFixed(2)}kWh`
    : `${wh >= 10 ? Math.round(wh) : wh.toFixed(1)}Wh`;
}

/** One decimal, because that is the resolution 気象庁 actually publishes. */
export function formatC(c: number): string {
  return `${c.toFixed(1)}℃`;
}

/**
 * Daily outdoor temperature, drawn as a min-max band with the mean marked.
 *
 * A band rather than a line: heatstroke risk is about the day's peak, and a mean
 * of 26℃ hides an afternoon that touched 35℃. Days with no observation are drawn
 * as a hatched gap for the same reason the energy chart does -- 0℃ is a real
 * winter reading, so a zeroed gap would look like a cold snap that never happened.
 */
export function OutdoorTrend({ points }: { points: OutdoorPoint[] }) {
  if (points.length === 0) {
    return <p className="text-sm text-gray-400">気温のデータがまだありません。</p>;
  }

  const measured = points.filter((point) => point.measured);
  if (measured.length === 0) {
    return <p className="text-sm text-gray-400">気温のデータがまだありません。</p>;
  }

  // Pad the scale so a flat week still has a visible band instead of a hairline.
  const lo = Math.min(...measured.map((point) => point.minC)) - 1;
  const hi = Math.max(...measured.map((point) => point.maxC)) + 1;
  const span = Math.max(1, hi - lo);
  const labelEvery = Math.max(1, Math.ceil(points.length / 10));

  return (
    <div>
      <div className="relative flex h-36 items-stretch gap-[3px]">
        {points.map((point) => {
          // Warmer days sit further along the amber-to-red ramp, matching the
          // colour language the 環境省 bands already use elsewhere on this page.
          const warmth = Math.min(1, Math.max(0, (point.maxC - lo) / span));
          const bottom = ((point.minC - lo) / span) * 100;
          const height = ((point.maxC - point.minC) / span) * 100;

          return (
            <div key={point.date.toISOString()} className="relative h-full flex-1">
              {point.measured ? (
                <>
                  <div
                    className="absolute inset-x-0 rounded-[3px] transition-[height] duration-700"
                    style={{
                      bottom: `${bottom}%`,
                      height: `${Math.max(height, 4)}%`,
                      background: `linear-gradient(180deg, hsl(${34 - warmth * 30} 92% ${64 - warmth * 12}%), hsl(${44 - warmth * 30} 88% ${76 - warmth * 14}%))`,
                    }}
                    title={`${point.label}: ${formatC(point.minC)}〜${formatC(point.maxC)}（平均 ${formatC(point.avgC)}）${
                      point.maxWbgt === null ? '' : ` / 暑さ指数 最高 ${point.maxWbgt.toFixed(1)}`
                    }`}
                  />
                  <div
                    className="absolute inset-x-0 h-[2px] bg-white/80"
                    style={{ bottom: `${((point.avgC - lo) / span) * 100}%` }}
                  />
                </>
              ) : (
                <div
                  className="absolute inset-0 rounded-[3px]"
                  style={{
                    background:
                      'repeating-linear-gradient(135deg, #f1f5f9 0 4px, #ffffff 4px 8px)',
                  }}
                  title={`${point.label}: 未計測`}
                />
              )}
            </div>
          );
        })}
      </div>
      <div className="mt-1.5 flex gap-[3px] text-[10px] text-gray-400">
        {points.map((point, index) => (
          <span key={point.date.toISOString()} className="flex-1 text-center">
            {index % labelEvery === 0 || index === points.length - 1
              ? point.label
              : ''}
          </span>
        ))}
      </div>
    </div>
  );
}

/**
 * Daily electricity bars.
 *
 * Bars rather than a line because each day is a discrete total, and because a
 * missing day has to be visibly absent -- a line would interpolate straight
 * through an outage and invent consumption that was never measured.
 */
export function EnergyTrend({ points }: { points: EnergyPoint[] }) {
  if (points.length === 0) {
    return <p className="text-sm text-gray-400">電力量のデータがまだありません。</p>;
  }

  const measured = points.filter((point) => point.measured);
  const max = Math.max(1, ...measured.map((point) => point.wh));
  const avg =
    measured.length === 0
      ? 0
      : measured.reduce((sum, point) => sum + point.wh, 0) / measured.length;
  const labelEvery = Math.max(1, Math.ceil(points.length / 10));

  return (
    <div>
      <div className="relative flex h-36 items-stretch gap-[3px]">
        {avg > 0 && (
          <div
            className="pointer-events-none absolute inset-x-0 border-t border-dashed border-amber-400/70"
            style={{ bottom: `${(avg / max) * 100}%` }}
          >
            <span className="absolute right-0 -top-4 text-[10px] font-medium text-amber-600">
              平均 {formatWh(avg)}
            </span>
          </div>
        )}
        {points.map((point) => {
          const ratio = point.measured ? point.wh / max : 0;
          return (
            <div
              key={point.date.toISOString()}
              className="flex h-full flex-1 flex-col justify-end"
            >
              <div
                className="w-full rounded-t-[3px] transition-[height] duration-700"
                style={{
                  height: point.measured
                    ? `${Math.max(ratio * 100, point.wh > 0 ? 6 : 2)}%`
                    : '100%',
                  background: point.measured
                    ? `linear-gradient(180deg, hsl(${38 - ratio * 22} 95% ${70 - ratio * 22}%), hsl(${38 - ratio * 22} 90% ${56 - ratio * 14}%))`
                    : 'repeating-linear-gradient(135deg, #f1f5f9 0 4px, #ffffff 4px 8px)',
                }}
                title={
                  point.measured
                    ? `${point.label}: ${formatWh(point.wh)}`
                    : `${point.label}: 計測なし`
                }
              />
            </div>
          );
        })}
      </div>
      <div className="mt-1.5 flex gap-[3px] text-[10px] text-gray-400">
        {points.map((point, index) => (
          <span key={point.date.toISOString()} className="flex-1 text-center">
            {index % labelEvery === 0 || index === points.length - 1
              ? point.label
              : ''}
          </span>
        ))}
      </div>
    </div>
  );
}

/**
 * 24-slot profile of average hourly consumption, in JST.
 *
 * Shown next to the activity rhythm on purpose: the two disagreeing -- power drawn
 * in an hour nobody switched anything -- is the signal an operator is looking for.
 */
export function EnergyProfile({ hours }: { hours: EnergyHour[] }) {
  const max = Math.max(...hours.map((hour) => hour.avgWh));

  if (max <= 0) {
    return <p className="text-sm text-gray-400">電力量のデータがまだありません。</p>;
  }

  const peak = hours.reduce((best, hour) => (hour.avgWh > best.avgWh ? hour : best), hours[0]);

  return (
    <div>
      <div className="flex h-28 items-stretch gap-[3px]">
        {hours.map((hour) => {
          const ratio = hour.avgWh / max;
          return (
            <div key={hour.hour} className="flex h-full flex-1 flex-col justify-end">
              <div
                className="w-full rounded-t-[3px] transition-[height] duration-700"
                style={{
                  height: `${Math.max(ratio * 100, hour.avgWh > 0 ? 8 : 3)}%`,
                  background:
                    hour.avgWh === 0
                      ? '#f1f5f9'
                      : `linear-gradient(180deg, hsl(${40 - ratio * 30} 95% ${72 - ratio * 24}%), hsl(${40 - ratio * 30} 90% ${58 - ratio * 16}%))`,
                }}
                title={`${hour.hour}時台: 平均 ${formatWh(hour.avgWh)}（${hour.days}日ぶん）`}
              />
            </div>
          );
        })}
      </div>
      <div className="mt-1.5 flex justify-between text-[10px] text-gray-400">
        <span>0時</span>
        <span>6時</span>
        <span>12時</span>
        <span>18時</span>
        <span>23時</span>
      </div>
      <p className="mt-2 text-xs text-gray-500">
        よく電気を使う時間帯は <span className="font-semibold text-amber-600">{peak.hour}時台</span>
        （平均 {formatWh(peak.avgWh)}）です。
      </p>
    </div>
  );
}

/** Stacked daily bars: total height = alerts sent, red segment = delivery failures. */
export function AlertTimeline({ buckets }: { buckets: DayBucket[] }) {
  const max = Math.max(1, ...buckets.map((bucket) => bucket.total));

  return (
    <div className="flex h-40 items-stretch gap-2">
      {buckets.map((bucket) => {
        const heightPct = (bucket.total / max) * 100;
        const failedPct = bucket.total === 0 ? 0 : (bucket.failed / bucket.total) * 100;

        return (
          <div key={bucket.label} className="flex h-full flex-1 flex-col items-center gap-1">
            <div className="text-[11px] font-medium text-gray-500">
              {bucket.total > 0 ? bucket.total : ''}
            </div>
            <div className="flex w-full flex-1 items-end">
              <div
                className="relative w-full overflow-hidden rounded-t-md bg-sky-500/80 transition-[height] duration-500"
                style={{ height: `${Math.max(heightPct, bucket.total > 0 ? 6 : 2)}%` }}
                title={`${bucket.label}: ${bucket.total} 件（失敗 ${bucket.failed}）`}
              >
                {bucket.total === 0 && <div className="h-full w-full bg-gray-100" />}
                {bucket.failed > 0 && (
                  <div
                    className="absolute inset-x-0 bottom-0 bg-red-500"
                    style={{ height: `${failedPct}%` }}
                  />
                )}
              </div>
            </div>
            <div className="text-[11px] text-gray-400">{bucket.label}</div>
          </div>
        );
      })}
    </div>
  );
}

/** Donut built from stroke-dasharray arcs so it needs no charting dependency. */
export function RiskDonut({ slices }: { slices: RiskSlice[] }) {
  const total = slices.reduce((sum, slice) => sum + slice.count, 0);
  const radius = 52;
  const circumference = 2 * Math.PI * radius;
  let offset = 0;

  return (
    <div className="flex items-center gap-5">
      <svg viewBox="0 0 140 140" className="h-32 w-32 shrink-0 -rotate-90">
        <circle cx="70" cy="70" r={radius} fill="none" stroke="#f1f5f9" strokeWidth="16" />
        {total > 0 &&
          slices.map((slice) => {
            const length = (slice.count / total) * circumference;
            const dash = `${length} ${circumference - length}`;
            const element = (
              <circle
                key={slice.level}
                cx="70"
                cy="70"
                r={radius}
                fill="none"
                stroke={slice.color}
                strokeWidth="16"
                strokeDasharray={dash}
                strokeDashoffset={-offset}
              >
                <title>{`${slice.label}: ${slice.count} 件`}</title>
              </circle>
            );
            offset += length;
            return element;
          })}
      </svg>

      <ul className="space-y-1.5 text-sm">
        {total === 0 && <li className="text-gray-400">通知がありません。</li>}
        {slices.map((slice) => (
          <li key={slice.level} className="flex items-center gap-2">
            <span
              className="h-2.5 w-2.5 rounded-full"
              style={{ backgroundColor: slice.color }}
            />
            <span className="text-gray-700">リスク{slice.label}</span>
            <span className="font-semibold text-gray-900">{slice.count}</span>
            <span className="text-xs text-gray-400">
              {total > 0 ? `${Math.round((slice.count / total) * 100)}%` : ''}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Radial gauge for LINE delivery success. */
export function DeliveryGauge({
  successRate,
  success,
  failed,
}: {
  successRate: number;
  success: number;
  failed: number;
}) {
  const radius = 52;
  const circumference = 2 * Math.PI * radius;
  const filled = (successRate / 100) * circumference;
  const color = successRate >= 90 ? '#10b981' : successRate >= 60 ? '#f59e0b' : '#dc2626';

  return (
    <div className="flex items-center gap-5">
      <div className="relative h-32 w-32 shrink-0">
        <svg viewBox="0 0 140 140" className="h-full w-full -rotate-90">
          <circle cx="70" cy="70" r={radius} fill="none" stroke="#f1f5f9" strokeWidth="16" />
          <circle
            cx="70"
            cy="70"
            r={radius}
            fill="none"
            stroke={color}
            strokeWidth="16"
            strokeLinecap="round"
            strokeDasharray={`${filled} ${circumference - filled}`}
            className="transition-[stroke-dasharray] duration-700"
          />
        </svg>
        <div className="absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-2xl font-semibold text-gray-900">{successRate}%</span>
          <span className="text-[11px] text-gray-400">配信成功</span>
        </div>
      </div>

      <ul className="space-y-1.5 text-sm">
        <li className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 rounded-full bg-emerald-500" />
          <span className="text-gray-700">成功</span>
          <span className="font-semibold text-gray-900">{success}</span>
        </li>
        <li className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 rounded-full bg-red-500" />
          <span className="text-gray-700">失敗</span>
          <span className="font-semibold text-gray-900">{failed}</span>
        </li>
      </ul>
    </div>
  );
}

/**
 * Gradient-filled area chart of daily device events, drawn as a Catmull-Rom
 * spline converted to cubic beziers so the line reads as a smooth "signal"
 * rather than a jagged polyline. Purely SVG -- no charting dependency.
 */
export function ActivityArea({ points }: { points: ActivityPoint[] }) {
  const width = 720;
  const height = 200;
  const padX = 8;
  const padTop = 16;
  const padBottom = 26;

  if (points.length === 0) {
    return <p className="text-sm text-gray-400">活動データがありません。</p>;
  }

  const max = Math.max(1, ...points.map((point) => point.events));
  const step =
    points.length > 1 ? (width - padX * 2) / (points.length - 1) : 0;
  const plotHeight = height - padTop - padBottom;

  const xy = points.map((point, index) => ({
    x: padX + index * step,
    y: padTop + plotHeight * (1 - point.events / max),
    on: padTop + plotHeight * (1 - point.onEvents / max),
    point,
  }));

  const spline = (key: 'y' | 'on') => {
    if (xy.length === 1) return `M ${xy[0].x} ${xy[0][key]}`;
    let d = `M ${xy[0].x} ${xy[0][key]}`;
    for (let i = 0; i < xy.length - 1; i += 1) {
      const p0 = xy[Math.max(0, i - 1)];
      const p1 = xy[i];
      const p2 = xy[i + 1];
      const p3 = xy[Math.min(xy.length - 1, i + 2)];
      const c1x = p1.x + (p2.x - p0.x) / 6;
      const c1y = p1[key] + (p2[key] - p0[key]) / 6;
      const c2x = p2.x - (p3.x - p1.x) / 6;
      const c2y = p2[key] - (p3[key] - p1[key]) / 6;
      d += ` C ${c1x} ${c1y}, ${c2x} ${c2y}, ${p2.x} ${p2[key]}`;
    }
    return d;
  };

  const line = spline('y');
  const area = `${line} L ${xy[xy.length - 1].x} ${height - padBottom} L ${xy[0].x} ${height - padBottom} Z`;
  const peak = xy.reduce((best, item) => (item.point.events > best.point.events ? item : best), xy[0]);
  const labelEvery = Math.max(1, Math.ceil(points.length / 8));

  return (
    <svg viewBox={`0 0 ${width} ${height}`} className="w-full" role="img">
      <defs>
        <linearGradient id="activity-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#38bdf8" stopOpacity="0.55" />
          <stop offset="100%" stopColor="#38bdf8" stopOpacity="0.02" />
        </linearGradient>
        <linearGradient id="activity-stroke" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#6366f1" />
          <stop offset="55%" stopColor="#0ea5e9" />
          <stop offset="100%" stopColor="#22d3ee" />
        </linearGradient>
        <filter id="activity-glow" x="-20%" y="-40%" width="140%" height="200%">
          <feGaussianBlur stdDeviation="4" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      {[0.25, 0.5, 0.75, 1].map((fraction) => (
        <line
          key={fraction}
          x1={padX}
          x2={width - padX}
          y1={padTop + plotHeight * (1 - fraction)}
          y2={padTop + plotHeight * (1 - fraction)}
          stroke="#e2e8f0"
          strokeWidth="1"
          strokeDasharray="3 5"
        />
      ))}

      <path d={area} fill="url(#activity-fill)" />
      <path d={spline('on')} fill="none" stroke="#a5b4fc" strokeWidth="1.5" strokeDasharray="4 4" />
      <path
        d={line}
        fill="none"
        stroke="url(#activity-stroke)"
        strokeWidth="2.5"
        strokeLinecap="round"
        filter="url(#activity-glow)"
      />

      {xy.map((item) => (
        <circle
          key={item.point.label}
          cx={item.x}
          cy={item.y}
          r={item === peak ? 4.5 : 2.5}
          fill={item === peak ? '#4f46e5' : '#0ea5e9'}
          stroke="#fff"
          strokeWidth="1.5"
        >
          <title>{`${item.point.label}: ${item.point.events} 件（ON ${item.point.onEvents}）`}</title>
        </circle>
      ))}

      <text
        x={Math.min(width - 60, Math.max(30, peak.x))}
        y={Math.max(12, peak.y - 10)}
        textAnchor="middle"
        className="fill-indigo-600 text-[11px] font-semibold"
      >
        最大 {peak.point.events}
      </text>

      {xy.map((item, index) =>
        index % labelEvery === 0 || index === xy.length - 1 ? (
          <text
            key={`label-${item.point.label}`}
            x={item.x}
            y={height - 8}
            textAnchor="middle"
            className="fill-gray-400 text-[10px]"
          >
            {item.point.label}
          </text>
        ) : null
      )}
    </svg>
  );
}

/** 24-slot bar histogram of when the home is active, in JST. */
export function RhythmHeatmap({ cells }: { cells: HeatmapCell[] }) {
  const max = Math.max(1, ...cells.map((cell) => cell.events));
  const formatValue = (cell: HeatmapCell) =>
    cell.mode === 'energy'
      ? `${cell.events.toFixed(cell.events >= 10 ? 0 : 1)} Wh/日`
      : `${cell.events} 件`;

  return (
    <div>
      <div className="flex h-28 items-stretch gap-[3px]">
        {cells.map((cell) => {
          const ratio = cell.events / max;
          return (
            <div key={cell.hour} className="flex h-full flex-1 flex-col justify-end">
              <div
                className="w-full rounded-t-[3px] transition-[height] duration-700"
                style={{
                  height: `${Math.max(ratio * 100, cell.events > 0 ? 8 : 3)}%`,
                  background:
                    cell.events === 0
                      ? '#f1f5f9'
                      : `linear-gradient(180deg, hsl(${210 - ratio * 170} 90% ${72 - ratio * 26}%), hsl(${210 - ratio * 170} 85% ${58 - ratio * 18}%))`,
                }}
                title={`${cell.hour}時台: ${formatValue(cell)}`}
              />
            </div>
          );
        })}
      </div>
      <div className="mt-1.5 flex justify-between text-[10px] text-gray-400">
        <span>0時</span>
        <span>6時</span>
        <span>12時</span>
        <span>18時</span>
        <span>23時</span>
      </div>
    </div>
  );
}

/** Per-device contribution bars. */
export function DeviceBreakdown({ slices }: { slices: DeviceSlice[] }) {
  const total = slices.reduce((sum, slice) => sum + slice.events, 0);
  const max = Math.max(1, ...slices.map((slice) => slice.events));

  if (slices.length === 0) {
    return <p className="text-sm text-gray-400">機器イベントがありません。</p>;
  }

  return (
    <div className="space-y-2.5">
      {slices.map((slice) => (
        <div key={slice.id} className="space-y-1">
          <div className="flex items-baseline justify-between text-sm">
            <span className="font-medium text-gray-800">{slice.name}</span>
            <span className="text-xs text-gray-500">
              {slice.events} 件
              {total > 0 && (
                <span className="ml-1 text-gray-400">
                  {Math.round((slice.events / total) * 100)}%
                </span>
              )}
            </span>
          </div>
          <div className="h-2 overflow-hidden rounded-full bg-gray-100">
            <div
              className="h-full rounded-full bg-gradient-to-r from-indigo-500 to-cyan-400 transition-[width] duration-700"
              style={{ width: `${(slice.events / max) * 100}%` }}
            />
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Which models Azure Model Router actually served requests with.
 *
 * Two bars per model on purpose: the call bar shows how much traffic the model
 * took, the latency bar shows what it cost. The rows deliberately carry no
 * "auto vs pinned" badge -- that split reflects how a request was asked for,
 * not what came back, and the log still holds rows from before the call sites
 * were narrowed down, so labelling them would say more than the data supports.
 * The model name and its measured cost are the facts.
 */
export function RouterModels({ models }: { models: ModelBar[] }) {
  if (models.length === 0) {
    return <p className="text-sm text-gray-400">AI 呼び出しの記録がありません。</p>;
  }

  const maxCalls = Math.max(1, ...models.map((model) => model.calls));
  const maxMs = Math.max(1, ...models.map((model) => model.avgMs));

  return (
    <div className="space-y-4">
      {models.map((model) => (
        <div key={model.model} className="space-y-1.5">
          <div className="flex flex-wrap items-baseline justify-between gap-x-2">
            <span
              className={
                model.unresolved
                  ? 'text-sm font-medium text-amber-700'
                  : 'font-mono text-sm font-medium text-gray-800'
              }
            >
              {model.model}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <div className="h-2 flex-1 overflow-hidden rounded-full bg-gray-100">
              <div
                className={
                  // Amber, not the model gradient: this bar is a failure, and
                  // giving it the same colour would read as another model.
                  model.unresolved
                    ? 'h-full rounded-full bg-amber-400 transition-[width] duration-700'
                    : 'h-full rounded-full bg-gradient-to-r from-rose-500 to-fuchsia-400 transition-[width] duration-700'
                }
                style={{ width: `${(model.calls / maxCalls) * 100}%` }}
              />
            </div>
            <span className="w-16 shrink-0 text-right text-xs text-gray-500">
              {model.calls} 回
            </span>
          </div>

          <div className="flex items-center gap-2">
            <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-gray-100">
              <div
                className="h-full rounded-full bg-gray-400 transition-[width] duration-700"
                style={{ width: `${(model.avgMs / maxMs) * 100}%` }}
              />
            </div>
            <span className="w-16 shrink-0 text-right text-xs text-gray-400">
              {model.avgMs.toLocaleString()} ms
            </span>
          </div>

          {model.unresolved ? (
            <p className="text-[11px] text-amber-700">
              モデルが応答する前に失敗した呼び出しです（用途:{' '}
              {model.purposes.join('、')}）。応答したモデル名が記録に残らないため、
              どのモデルの棒にも載せられません。
            </p>
          ) : (
            model.purposes.length > 0 && (
              <p className="text-[11px] text-gray-400">用途: {model.purposes.join('、')}</p>
            )
          )}
        </div>
      ))}
    </div>
  );
}

/** Horizontal bars comparing devices and alerts per household. */
export function HouseholdBars({ bars }: { bars: HouseholdBar[] }) {
  const maxDevices = Math.max(1, ...bars.map((bar) => bar.devices));
  const maxAlerts = Math.max(1, ...bars.map((bar) => bar.alerts));

  if (bars.length === 0) {
    return <p className="text-sm text-gray-400">世帯データがありません。</p>;
  }

  return (
    <div className="space-y-3">
      {bars.map((bar) => (
        <div key={bar.id} className="space-y-1">
          <div className="flex items-center justify-between text-sm">
            <span className="font-medium text-gray-800">
              {bar.name}
              {bar.needsAttention && (
                <span className="ml-2 rounded-full bg-red-100 px-2 py-0.5 text-[11px] text-red-700">
                  要対応
                </span>
              )}
            </span>
            <span className="text-xs text-gray-500">
              機器 {bar.devices} / 通知 {bar.alerts}
              {bar.failed > 0 && <span className="text-red-600">（失敗 {bar.failed}）</span>}
            </span>
          </div>
          <div className="flex items-center gap-2">
            <span className="w-10 shrink-0 text-[11px] text-gray-400">機器</span>
            <div className="h-2.5 flex-1 overflow-hidden rounded-full bg-gray-100">
              <div
                className="h-full rounded-full bg-indigo-500 transition-[width] duration-700"
                style={{ width: `${(bar.devices / maxDevices) * 100}%` }}
              />
            </div>
          </div>
          <div className="flex items-center gap-2">
            <span className="w-10 shrink-0 text-[11px] text-gray-400">通知</span>
            <div className="h-2.5 flex-1 overflow-hidden rounded-full bg-gray-100">
              <div
                className="h-full rounded-full bg-sky-400 transition-[width] duration-700"
                style={{ width: `${(bar.alerts / maxAlerts) * 100}%` }}
              />
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}
