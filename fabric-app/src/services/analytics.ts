import type {
  ActivityRow,
  AiRouterCallRow,
  AlertRow,
  DataOrigin,
  HouseholdRow,
  OutdoorRow,
} from './monitoring';

/** Rayfin returns datetimes as `Date`, but a rehydrated JSON payload may hand back a string. */
export function toDate(value: Date | string): Date {
  return value instanceof Date ? value : new Date(value);
}

function toInt(value: string): number {
  const parsed = Number.parseInt(value, 10);
  return Number.isNaN(parsed) ? 0 : parsed;
}

/**
 * Which households a page is looking at.
 *
 * Demo and production households live side by side in the same tables, and every
 * chart below sums whatever rows it is handed. Left alone, a seeded demo home and
 * a real one are added together into a single line, which is worse than showing
 * neither: the numbers look authoritative and describe nobody.
 */
export type DataScope = 'all' | 'Production' | 'Sample';

export interface ScopedRows {
  households: HouseholdRow[];
  alerts: AlertRow[];
  activity: ActivityRow[];
}

/**
 * Narrows the three household-shaped tables to one data source.
 *
 * Alerts and activity carry a household id but not its mode, so the households
 * table is the authority and rows pointing at a household it does not contain are
 * dropped. That loses orphans left behind by a deleted household -- which is the
 * safe direction, because an orphan cannot be attributed to demo or production
 * and would otherwise land in whichever view the reader happened to open.
 */
export function scopeRows(
  households: HouseholdRow[],
  alerts: AlertRow[],
  activity: ActivityRow[],
  scope: DataScope
): ScopedRows {
  if (scope === 'all') return { households, alerts, activity };

  const kept = households.filter((row) => row.dataSourceMode === scope);
  const ids = new Set(kept.map((row) => row.householdId));

  return {
    households: kept,
    alerts: alerts.filter((row) => ids.has(row.householdId)),
    activity: activity.filter((row) => ids.has(row.householdId)),
  };
}

export interface DayBucket {
  /** Local midnight of the bucket. */
  date: Date;
  label: string;
  total: number;
  failed: number;
}

/**
 * Buckets alerts into the last `days` local days, oldest first. Empty days are
 * kept so the timeline keeps a constant width regardless of activity.
 */
export function alertsByDay(alerts: AlertRow[], days = 7, now = new Date()): DayBucket[] {
  const buckets: DayBucket[] = [];
  const base = new Date(now.getFullYear(), now.getMonth(), now.getDate());

  for (let offset = days - 1; offset >= 0; offset -= 1) {
    const date = new Date(base);
    date.setDate(base.getDate() - offset);
    buckets.push({
      date,
      label: `${date.getMonth() + 1}/${date.getDate()}`,
      total: 0,
      failed: 0,
    });
  }

  const firstMs = buckets[0].date.getTime();

  for (const alert of alerts) {
    const sent = toDate(alert.sentAt);
    if (Number.isNaN(sent.getTime())) continue;

    const sentDay = new Date(sent.getFullYear(), sent.getMonth(), sent.getDate());
    const index = Math.round((sentDay.getTime() - firstMs) / 86_400_000);
    if (index < 0 || index >= buckets.length) continue;

    buckets[index].total += 1;
    if (!alert.success) buckets[index].failed += 1;
  }

  return buckets;
}

export interface RiskSlice {
  level: 'High' | 'Medium' | 'Low' | 'Unknown';
  label: string;
  count: number;
  color: string;
}

const RISK_ORDER: RiskSlice[] = [
  { level: 'High', label: '高', count: 0, color: '#dc2626' },
  { level: 'Medium', label: '中', count: 0, color: '#f59e0b' },
  { level: 'Low', label: '低', count: 0, color: '#10b981' },
  { level: 'Unknown', label: '不明', count: 0, color: '#cbd5e1' },
];

export function riskDistribution(alerts: AlertRow[]): RiskSlice[] {
  const slices = RISK_ORDER.map((slice) => ({ ...slice }));

  for (const alert of alerts) {
    const match = slices.find((slice) => slice.level === alert.riskLevel);
    (match ?? slices[slices.length - 1]).count += 1;
  }

  return slices.filter((slice) => slice.count > 0);
}

export interface DeliveryStats {
  total: number;
  success: number;
  failed: number;
  /** 0–100, rounded. `100` when there is nothing to deliver. */
  successRate: number;
}

export function deliveryStats(alerts: AlertRow[]): DeliveryStats {
  const total = alerts.length;
  const failed = alerts.filter((alert) => !alert.success).length;
  const success = total - failed;

  return {
    total,
    success,
    failed,
    successRate: total === 0 ? 100 : Math.round((success / total) * 100),
  };
}

export interface HouseholdBar {
  id: string;
  name: string;
  devices: number;
  alerts: number;
  failed: number;
  needsAttention: boolean;
}

export function householdBars(rows: HouseholdRow[]): HouseholdBar[] {
  return rows
    .map((row) => ({
      id: row.id,
      name: row.name || '(名称未設定)',
      devices: toInt(row.deviceCount),
      alerts: toInt(row.alertsInWindow),
      failed: toInt(row.failedAlertsInWindow),
      needsAttention: row.needsAttention,
    }))
    .sort((a, b) => b.devices - a.devices || a.name.localeCompare(b.name, 'ja'));
}

export interface ActivityPoint {
  /** UTC midnight of the day. */
  date: Date;
  label: string;
  events: number;
  onEvents: number;
}

/**
 * Daily totals across whatever window the buckets actually cover. The range is
 * taken from the data rather than "the last N days from now" so a historical
 * export still renders a continuous line instead of a flat empty chart.
 * Days with no events are filled with zero to keep the x-axis linear in time.
 */
export function dailyActivity(buckets: ActivityRow[], maxDays = 30): ActivityPoint[] {
  if (buckets.length === 0) return [];

  const byDay = new Map<number, ActivityPoint>();
  let min = Infinity;
  let max = -Infinity;

  for (const bucket of buckets) {
    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;

    const key = Date.UTC(
      start.getUTCFullYear(),
      start.getUTCMonth(),
      start.getUTCDate()
    );
    min = Math.min(min, key);
    max = Math.max(max, key);

    const point = byDay.get(key);
    if (point) {
      point.events += toInt(bucket.eventCount);
      point.onEvents += toInt(bucket.onCount);
    } else {
      const date = new Date(key);
      byDay.set(key, {
        date,
        label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
        events: toInt(bucket.eventCount),
        onEvents: toInt(bucket.onCount),
      });
    }
  }

  if (!Number.isFinite(min)) return [];

  const oldestAllowed = max - (maxDays - 1) * 86_400_000;
  const from = Math.max(min, oldestAllowed);
  const points: ActivityPoint[] = [];

  for (let key = from; key <= max; key += 86_400_000) {
    const date = new Date(key);
    points.push(
      byDay.get(key) ?? {
        date,
        label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
        events: 0,
        onEvents: 0,
      }
    );
  }

  return points;
}

export interface HeatmapCell {
  hour: number;
  /** Event count in fallback mode, or mean Wh in energy mode. */
  events: number;
  mode: 'energy' | 'events';
}

/**
 * 24-slot histogram of living rhythm in the household's local time (JST).
 * Metered homes use hourly watt-hours because plugs stay energised; demo/legacy
 * rows with no energy fall back to event counts.
 */
export function hourlyRhythm(buckets: ActivityRow[], utcOffsetHours = 9): HeatmapCell[] {
  const hasEnergy = buckets.some((bucket) => toEnergy(bucket.energyWh) !== null);
  const cells: HeatmapCell[] = Array.from({ length: 24 }, (_, hour) => ({
    hour,
    events: 0,
    mode: hasEnergy ? 'energy' : 'events',
  }));

  if (hasEnergy) {
    const offsetMs = utcOffsetHours * 3_600_000;
    const days = Array.from({ length: 24 }, () => new Set<number>());

    for (const bucket of buckets) {
      const wh = toEnergy(bucket.energyWh);
      if (wh === null) continue;

      const start = toDate(bucket.bucketStart);
      if (Number.isNaN(start.getTime())) continue;

      const local = new Date(start.getTime() + offsetMs);
      const hour = local.getUTCHours();
      cells[hour].events += wh;
      days[hour].add(
        Date.UTC(local.getUTCFullYear(), local.getUTCMonth(), local.getUTCDate())
      );
    }

    return cells.map((cell) => ({
      ...cell,
      events: days[cell.hour].size === 0 ? 0 : cell.events / days[cell.hour].size,
    }));
  }

  for (const bucket of buckets) {
    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;
    const hour = (start.getUTCHours() + utcOffsetHours + 24) % 24;
    cells[hour].events += toInt(bucket.eventCount);
  }

  return cells;
}

export interface EnergyPoint {
  /** Local (JST) midnight of the day, expressed as a UTC instant for labelling. */
  date: Date;
  label: string;
  wh: number;
  /** False for a day the meter never reported, which is drawn as a gap. */
  measured: boolean;
}

/**
 * Daily electricity totals, bucketed by the household's local day.
 *
 * Deliberately local rather than UTC: a family reads "yesterday" as their own
 * calendar day, and a chart whose days end at 09:00 JST would disagree with the
 * answer the assistant gives them for the same question.
 *
 * Days with no reading are kept in the series but flagged unmeasured, so a poller
 * outage shows as a hole instead of a day the household used no power at all.
 */
export function dailyEnergy(
  buckets: ActivityRow[],
  maxDays = 30,
  utcOffsetHours = 9
): EnergyPoint[] {
  const offsetMs = utcOffsetHours * 3_600_000;
  const byDay = new Map<number, number>();
  let min = Infinity;
  let max = -Infinity;

  for (const bucket of buckets) {
    const wh = toEnergy(bucket.energyWh);
    if (wh === null) continue;

    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;

    const local = new Date(start.getTime() + offsetMs);
    const key = Date.UTC(
      local.getUTCFullYear(),
      local.getUTCMonth(),
      local.getUTCDate()
    );
    min = Math.min(min, key);
    max = Math.max(max, key);
    byDay.set(key, (byDay.get(key) ?? 0) + wh);
  }

  if (!Number.isFinite(min)) return [];

  const from = Math.max(min, max - (maxDays - 1) * 86_400_000);
  const points: EnergyPoint[] = [];

  for (let key = from; key <= max; key += 86_400_000) {
    const date = new Date(key);
    const wh = byDay.get(key);
    points.push({
      date,
      label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
      wh: wh ?? 0,
      measured: wh !== undefined,
    });
  }

  return points;
}

export interface EnergyHour {
  hour: number;
  /** Mean watt-hours for this hour across the days that actually reported. */
  avgWh: number;
  days: number;
}

/**
 * 24-slot profile of when the electricity is actually used, in local time.
 *
 * Averaged per reporting day rather than summed, so a window that happens to hold
 * more mornings than evenings does not read as "this family uses more power before
 * noon". The shape is the point: it is what makes an unusual night visible.
 */
export function hourlyEnergy(
  buckets: ActivityRow[],
  utcOffsetHours = 9
): EnergyHour[] {
  const offsetMs = utcOffsetHours * 3_600_000;
  const totals = Array.from({ length: 24 }, () => 0);
  const days = Array.from({ length: 24 }, () => new Set<number>());

  for (const bucket of buckets) {
    const wh = toEnergy(bucket.energyWh);
    if (wh === null) continue;

    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;

    const local = new Date(start.getTime() + offsetMs);
    const hour = local.getUTCHours();
    totals[hour] += wh;
    days[hour].add(
      Date.UTC(local.getUTCFullYear(), local.getUTCMonth(), local.getUTCDate())
    );
  }

  return totals.map((total, hour) => ({
    hour,
    days: days[hour].size,
    avgWh: days[hour].size === 0 ? 0 : total / days[hour].size,
  }));
}

/** Empty means "not metered", which is not the same as zero and must not become one. */
function toEnergy(value: string | undefined): number | null {
  if (value === undefined || value === null || value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export interface OutdoorPoint {
  /** Local (JST) midnight of the day, expressed as a UTC instant for labelling. */
  date: Date;
  label: string;
  /** Mean of the hourly means, in °C. Zero when the day observed nothing. */
  avgC: number;
  minC: number;
  maxC: number;
  /** Highest 暑さ指数 seen that day, or null out of season. */
  maxWbgt: number | null;
  /** False for a day nothing was observed, which is drawn as a gap. */
  measured: boolean;
}

/**
 * Daily outdoor temperature, bucketed by the household's local day.
 *
 * Same local-day rule as {@link dailyEnergy} for the same reason: the electricity
 * chart and the temperature chart are read side by side, and days that started at
 * different hours would make the pairing meaningless.
 *
 * A day nothing was observed stays in the series flagged unmeasured. That matters
 * more here than for power: 0 °C is a perfectly ordinary winter reading, so a gap
 * silently filled with zero would look like a cold snap that never happened.
 */
export function dailyOutdoor(
  rows: OutdoorRow[],
  maxDays = 30,
  utcOffsetHours = 9
): OutdoorPoint[] {
  const offsetMs = utcOffsetHours * 3_600_000;
  const byDay = new Map<
    number,
    { sum: number; count: number; min: number; max: number; wbgt: number | null }
  >();
  let min = Infinity;
  let max = -Infinity;

  for (const row of rows) {
    const start = toDate(row.bucketStart);
    if (Number.isNaN(start.getTime())) continue;

    const mean = toMeasure(row.temperatureC);
    if (mean === null) continue;

    const local = new Date(start.getTime() + offsetMs);
    const key = Date.UTC(
      local.getUTCFullYear(),
      local.getUTCMonth(),
      local.getUTCDate()
    );
    min = Math.min(min, key);
    max = Math.max(max, key);

    const lo = toMeasure(row.minTemperatureC) ?? mean;
    const hi = toMeasure(row.maxTemperatureC) ?? mean;
    const wbgt = toMeasure(row.maxWbgt);

    const day = byDay.get(key);
    if (day === undefined) {
      byDay.set(key, { sum: mean, count: 1, min: lo, max: hi, wbgt });
      continue;
    }

    day.sum += mean;
    day.count += 1;
    day.min = Math.min(day.min, lo);
    day.max = Math.max(day.max, hi);
    if (wbgt !== null) day.wbgt = day.wbgt === null ? wbgt : Math.max(day.wbgt, wbgt);
  }

  if (!Number.isFinite(min)) return [];

  const from = Math.max(min, max - (maxDays - 1) * 86_400_000);
  const points: OutdoorPoint[] = [];

  for (let key = from; key <= max; key += 86_400_000) {
    const date = new Date(key);
    const day = byDay.get(key);
    points.push({
      date,
      label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
      avgC: day ? day.sum / day.count : 0,
      minC: day ? day.min : 0,
      maxC: day ? day.max : 0,
      maxWbgt: day ? day.wbgt : null,
      measured: day !== undefined,
    });
  }

  return points;
}

export interface OutdoorSummary {
  /** Hourly rows behind the figures below. Zero means nothing has been synced yet. */
  hours: number;
  points: number;
  /** Newest hour observed, or null when nothing has been synced. */
  latestAt: Date | null;
  latestArea: string;
  /** Newest observed temperature in °C, or null when never observed. */
  latestC: number | null;
  /** Highest / lowest hourly mean across the whole window. */
  maxC: number | null;
  minC: number | null;
  /** Highest 暑さ指数 in the window, or null when out of season. */
  maxWbgt: number | null;
  /** Hours the 環境省 band reached 警戒 (3) or above. */
  cautionHours: number;
}

/**
 * The one-line answer to "what has it been like outside".
 *
 * Every field is nullable rather than zero-defaulted: the console must be able to
 * say 未計測 out loud. The heat band is counted from level 3 (警戒) because that is
 * where 環境省 starts advising active avoidance, which is the point at which a
 * family watching from far away would want to have been told.
 */
export function outdoorSummary(rows: OutdoorRow[]): OutdoorSummary {
  let latestAt: Date | null = null;
  let latestArea = '';
  let latestC: number | null = null;
  let maxC: number | null = null;
  let minC: number | null = null;
  let maxWbgt: number | null = null;
  let cautionHours = 0;
  const points = new Set<string>();

  for (const row of rows) {
    if (row.pointCode) points.add(row.pointCode);

    const at = toDate(row.bucketStart);
    const temp = toMeasure(row.temperatureC);
    const wbgt = toMeasure(row.maxWbgt);

    if (temp !== null) {
      maxC = maxC === null ? temp : Math.max(maxC, temp);
      minC = minC === null ? temp : Math.min(minC, temp);
    }
    if (wbgt !== null) maxWbgt = maxWbgt === null ? wbgt : Math.max(maxWbgt, wbgt);
    if (toInt(row.heatLevel) >= 3) cautionHours += 1;

    if (!Number.isNaN(at.getTime()) && (latestAt === null || at > latestAt)) {
      latestAt = at;
      latestArea = row.areaName;
      latestC = temp;
    }
  }

  return {
    hours: rows.length,
    points: points.size,
    latestAt,
    latestArea,
    latestC,
    maxC,
    minC,
    maxWbgt,
    cautionHours,
  };
}

/** Empty means "not observed", which is not the same as 0 °C and must not become one. */
function toMeasure(value: string | undefined): number | null {
  if (value === undefined || value === null || value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export interface DeviceSlice {
  id: string;
  name: string;
  type: string;
  events: number;
  latest: Date | null;
}

/** Per-device event totals, busiest first, for the contribution bars. */
export function deviceBreakdown(buckets: ActivityRow[], limit = 8): DeviceSlice[] {
  const byDevice = new Map<string, DeviceSlice>();

  for (const bucket of buckets) {
    const name = bucket.deviceName || '(不明な機器)';
    const key = bucket.deviceId?.trim() || `name:${name}`;
    const start = toDate(bucket.bucketStart);
    const latest = Number.isNaN(start.getTime()) ? null : start;
    const slice = byDevice.get(key);
    if (slice) {
      slice.events += toInt(bucket.eventCount);
      if (latest && (!slice.latest || latest > slice.latest)) {
        slice.name = name;
        slice.type = bucket.deviceType || '-';
        slice.latest = latest;
      }
    } else {
      byDevice.set(key, {
        id: key,
        name,
        type: bucket.deviceType || '-',
        events: toInt(bucket.eventCount),
        latest,
      });
    }
  }

  return [...byDevice.values()]
    .sort((a, b) => b.events - a.events || a.name.localeCompare(b.name, 'ja'))
    .slice(0, limit);
}

export interface ActivitySummary {
  events: number;
  buckets: number;
  devices: number;
  days: number;
  /** Distinct ingestion sources seen (`SwitchBotPoll`, `AppCommand`, ...). */
  sources: string[];
  from: Date | null;
  to: Date | null;
}

export function activitySummary(buckets: ActivityRow[]): ActivitySummary {
  const devices = new Set<string>();
  const days = new Set<number>();
  const sources = new Set<string>();
  let events = 0;
  let from: Date | null = null;
  let to: Date | null = null;

  for (const bucket of buckets) {
    events += toInt(bucket.eventCount);
    const deviceKey = bucket.deviceId?.trim() || bucket.deviceName;
    if (deviceKey) devices.add(deviceKey);
    if (bucket.source) sources.add(bucket.source);

    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;
    days.add(
      Date.UTC(start.getUTCFullYear(), start.getUTCMonth(), start.getUTCDate())
    );
    if (!from || start < from) from = start;
    if (!to || start > to) to = start;
  }

  return {
    events,
    buckets: buckets.length,
    devices: devices.size,
    days: days.size,
    sources: [...sources].sort(),
    from,
    to,
  };
}

/** The offline stub used when no Azure Model Router deployment is configured. */
export const MOCK_ROUTER = 'MockAiRouter';

/** Every router value except the local stub reached Azure Model Router. */
export function viaModelRouter(row: AiRouterCallRow): boolean {
  return row.router !== MOCK_ROUTER;
}

/**
 * A model Azure Model Router actually served requests with.
 *
 * The router picks the model per request, so every bar here is a model it chose;
 * the app never names one. `purposes` is what the app did control -- which call
 * sites ended up on that model.
 */
export interface ModelBar {
  model: string;
  calls: number;
  success: number;
  /** Call-weighted mean latency, milliseconds. */
  avgMs: number;
  purposes: string[];
  /**
   * True for the single synthetic bar holding calls that never resolved to a
   * model. It is not a model and must not be drawn or counted as one.
   */
  unresolved?: boolean;
}

/** Label for the synthetic bar. Not a model name; see {@link ModelBar.unresolved}. */
export const UNRESOLVED_BAR = '未応答（失敗）';

/**
 * Collapses the (purpose, router, model) grain down to one bar per model, plus
 * one trailing bar for the calls that never resolved to a model.
 *
 * A call that fails before a model answers logs no model at all, so there is no
 * model to attribute it to. Dropping it made the bars add up to
 * less than the call count printed above them, which reads as a miscount rather
 * than as a failure. Giving the failures their own bar means the bars total the
 * call count exactly, and the failure is visible instead of inferred.
 */
export function routerModels(rows: AiRouterCallRow[]): ModelBar[] {
  const byModel = new Map<string, ModelBar & { weighted: number }>();
  let unresolvedCalls = 0;
  let unresolvedSuccess = 0;
  let unresolvedWeighted = 0;
  const unresolvedPurposes: string[] = [];

  for (const row of rows) {
    if (!viaModelRouter(row)) continue;

    const calls = toInt(row.callCount);
    const model = row.resolvedModel;

    if (!model || model === 'auto') {
      unresolvedCalls += calls;
      unresolvedSuccess += toInt(row.successCount);
      unresolvedWeighted += toInt(row.avgDurationMs) * calls;
      if (!unresolvedPurposes.includes(row.purpose)) unresolvedPurposes.push(row.purpose);
      continue;
    }

    const entry = byModel.get(model) ?? {
      model,
      calls: 0,
      success: 0,
      avgMs: 0,
      purposes: [],
      weighted: 0,
    };

    entry.calls += calls;
    entry.success += toInt(row.successCount);
    entry.weighted += toInt(row.avgDurationMs) * calls;
    if (!entry.purposes.includes(row.purpose)) entry.purposes.push(row.purpose);
    byModel.set(model, entry);
  }

  const bars = [...byModel.values()]
    .map(({ weighted, ...bar }) => ({
      ...bar,
      avgMs: bar.calls > 0 ? Math.round(weighted / bar.calls) : 0,
      purposes: bar.purposes.sort(),
    }))
    .sort((a, b) => b.calls - a.calls);

  if (unresolvedCalls > 0) {
    // Always last: it is the leftover, and sorting it among the models by call
    // count would imply it competes with them.
    bars.push({
      model: UNRESOLVED_BAR,
      calls: unresolvedCalls,
      success: unresolvedSuccess,
      avgMs: Math.round(unresolvedWeighted / unresolvedCalls),
      purposes: unresolvedPurposes.sort(),
      unresolved: true,
    });
  }

  return bars;
}

export interface RouterSummary {
  /** Calls that went through Azure Model Router. */
  calls: number;
  success: number;
  /** Distinct models Azure Model Router resolved to. */
  models: number;
  /** Call-weighted mean latency across router calls, milliseconds. */
  avgMs: number;
  /** Calls served by the offline stub, i.e. never sent to the router. */
  mockCalls: number;
  /**
   * Calls that reached the router but never resolved to a model name (a failed
   * call is logged with no model). {@link routerModels} has no bar to
   * put these on, so without showing this number the bars silently fail to add
   * up to {@link calls} and the page looks like it is miscounting.
   */
  unresolvedCalls: number;
  lastCalledAt: Date | null;
}

/** Totals for the diagram and the caption above the model chart. */
export function routerSummary(rows: AiRouterCallRow[]): RouterSummary {
  let calls = 0;
  let success = 0;
  let weighted = 0;
  let mockCalls = 0;
  let unresolvedCalls = 0;
  let lastCalledAt: Date | null = null;
  const models = new Set<string>();

  for (const row of rows) {
    const count = toInt(row.callCount);

    if (!viaModelRouter(row)) {
      mockCalls += count;
      continue;
    }

    calls += count;
    success += toInt(row.successCount);
    if (row.resolvedModel && row.resolvedModel !== 'auto') {
      models.add(row.resolvedModel);
    } else {
      unresolvedCalls += count;
    }

    weighted += toInt(row.avgDurationMs) * count;

    const called = row.lastCalledAt ? toDate(row.lastCalledAt) : null;
    if (called && !Number.isNaN(called.getTime()) && (!lastCalledAt || called > lastCalledAt)) {
      lastCalledAt = called;
    }
  }

  return {
    calls,
    success,
    models: models.size,
    avgMs: calls > 0 ? Math.round(weighted / calls) : 0,
    mockCalls,
    unresolvedCalls,
    lastCalledAt,
  };
}

export interface PipelineStats {
  devices: number;
  households: number;
  productionHouseholds: number;
  lineRecipients: number;
  alerts: number;
  failedAlerts: number;
  connectedSwitchBots: number;
  /** Device events ingested into the Fabric activity table. */
  activityEvents: number;
  /** Hourly activity rows stored in Fabric (one per household/device/hour). */
  fabricRows: number;
  /** Most recent device event across all households, or `null` when unknown. */
  lastEvent: Date | null;
  lastSync: Date | null;
  /** Calls routed through Azure Model Router, and how many distinct models it resolved to. */
  aiCalls: number;
  aiModels: number;
  /**
   * The subset of `aiCalls` that resolved to a named model, i.e. exactly what the
   * model bars below the diagram add up to. The diagram used to read
   * "79 回 / 4 モデル", which says those four models account for all 79 calls --
   * but a call that fails before a model answers is still logged, with no model
   * name to file it under, so the bars only totalled 78. Carrying the resolved
   * count here lets the diagram state both numbers instead of implying one.
   */
  aiResolvedCalls: number;
  /** Call-weighted mean router latency, milliseconds. */
  aiAvgMs: number;
  /** Where the rendered rows came from. Drives the console node's label. */
  origin: DataOrigin;
}

/**
 * Throughput numbers for the architecture animation. Everything is derived from
 * the same rows the tables render, so the diagram can never disagree with them.
 */
export function pipelineStats(
  rows: HouseholdRow[],
  alerts: AlertRow[],
  activity: ActivityRow[] = [],
  origin: DataOrigin = 'fabric',
  aiCalls: AiRouterCallRow[] = []
): PipelineStats {
  let lastEvent: Date | null = null;
  let lastSync: Date | null = null;
  const ai = routerSummary(aiCalls);

  for (const row of rows) {
    const event = row.lastEventUtc ? new Date(row.lastEventUtc) : null;
    if (event && !Number.isNaN(event.getTime()) && (!lastEvent || event > lastEvent)) {
      lastEvent = event;
    }

    const captured = row.capturedAt ? toDate(row.capturedAt) : null;
    if (captured && !Number.isNaN(captured.getTime()) && (!lastSync || captured > lastSync)) {
      lastSync = captured;
    }
  }

  return {
    devices: rows.reduce((sum, row) => sum + toInt(row.deviceCount), 0),
    households: rows.length,
    productionHouseholds: rows.filter((row) => row.dataSourceMode === 'Production').length,
    lineRecipients: rows.reduce((sum, row) => sum + toInt(row.activeLineRecipients), 0),
    alerts: alerts.length,
    failedAlerts: alerts.filter((alert) => !alert.success).length,
    connectedSwitchBots: rows.filter((row) => row.switchBotStatus === 'Connected').length,
    activityEvents: activity.reduce((sum, bucket) => sum + toInt(bucket.eventCount), 0),
    fabricRows: activity.length,
    lastEvent,
    lastSync,
    aiCalls: ai.calls,
    aiModels: ai.models,
    aiResolvedCalls: ai.calls - ai.unresolvedCalls,
    aiAvgMs: ai.avgMs,
    origin,
  };
}
