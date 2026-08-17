import { getRayfinClient, isLocalBackend } from './rayfinClient';
import {
  SNAPSHOT_ACTIVITY,
  SNAPSHOT_AI_CALLS,
  SNAPSHOT_ALERTS,
  SNAPSHOT_CAPTURED_AT,
  SNAPSHOT_HOUSEHOLDS,
} from './snapshotFallback';

export interface HouseholdRow {
  id: string;
  householdId: string;
  name: string;
  dataSourceMode: string;
  memberCount: string;
  residentCount: string;
  deviceCount: string;
  lastEventUtc: string;
  switchBotStatus: string;
  switchBotError: string;
  activeLineRecipients: string;
  alertsInWindow: string;
  failedAlertsInWindow: string;
  latestRiskLevel: string;
  needsAttention: boolean;
  capturedAt: Date;

  /**
   * Watt-hours used so far today, as a decimal string. Optional because rows
   * captured before the console started carrying power have no value, and a
   * paused capacity must not make the page throw.
   */
  powerTodayWh?: string;

  /** The usual figure by this hour: median of the last fourteen days. */
  powerBaselineWh?: string;

  /** "Higher" | "Lower" | "Typical" | "Unknown". */
  powerTrend?: string;
}

export interface AlertRow {
  id: string;
  householdId: string;
  householdName: string;
  riskLevel: string;
  score: string;
  reason: string;
  success: boolean;
  error: string;
  sentAt: Date;
}

export interface ActivityRow {
  id: string;
  householdId: string;
  householdName: string;
  deviceId?: string;
  deviceName: string;
  deviceType: string;
  bucketStart: Date;
  eventCount: string;
  onCount: string;
  source: string;
  /**
   * Watt-hours drawn during the hour. Optional and possibly empty: a workspace on an
   * older model, or an unmetered device, has no reading, and that is a gap in the chart
   * rather than a measured zero.
   */
  energyWh?: string;
}

/**
 * One (purpose, router, resolvedModel) group of AI calls.
 *
 * `router` is the client that served the call, recorded by AzureModelRouterClient:
 *  - "Azure Model Router" the router deployment chose the model for that request
 *  - "MockAiRouter" the offline stub -- the only value that did not reach the router
 */
export interface AiRouterCallRow {
  id: string;
  purpose: string;
  router: string;
  resolvedModel: string;
  callCount: string;
  successCount: string;
  avgDurationMs: string;
  lastCalledAt: Date;
}

/**
 * One hour of public outdoor observation for one 観測地点 (環境省 WBGT / 気象庁 AMeDAS).
 *
 * Every measurement is a string that may be empty. Empty means "not observed",
 * which is never the same as a measured zero -- 0 °C is an ordinary winter reading,
 * and WBGT is simply not published outside late April to late October.
 */
export interface OutdoorRow {
  id: string;
  pointCode: string;
  areaName: string;
  bucketStart: Date;
  temperatureC: string;
  minTemperatureC: string;
  maxTemperatureC: string;
  humidityPercent: string;
  maxWbgt: string;
  heatLevel: string;
  coldLevel: string;
  sampleCount: string;
}

const HOUSEHOLD_FIELDS = [
  'id',
  'householdId',
  'name',
  'dataSourceMode',
  'memberCount',
  'residentCount',
  'deviceCount',
  'lastEventUtc',
  'switchBotStatus',
  'switchBotError',
  'activeLineRecipients',
  'alertsInWindow',
  'failedAlertsInWindow',
  'latestRiskLevel',
  'needsAttention',
  'capturedAt',
  'powerTodayWh',
  'powerBaselineWh',
  'powerTrend',
] as const;

const ALERT_FIELDS = [
  'id',
  'householdId',
  'householdName',
  'riskLevel',
  'score',
  'reason',
  'success',
  'error',
  'sentAt',
] as const;

const ACTIVITY_FIELDS = [
  'id',
  'householdId',
  'householdName',
  'deviceId',
  'deviceName',
  'deviceType',
  'bucketStart',
  'eventCount',
  'onCount',
  'source',
  'energyWh',
] as const;

const AI_FIELDS = [
  'id',
  'purpose',
  'router',
  'resolvedModel',
  'callCount',
  'successCount',
  'avgDurationMs',
  'lastCalledAt',
] as const;

const OUTDOOR_FIELDS = [
  'id',
  'pointCode',
  'areaName',
  'bucketStart',
  'temperatureC',
  'minTemperatureC',
  'maxTemperatureC',
  'humidityPercent',
  'maxWbgt',
  'heatLevel',
  'coldLevel',
  'sampleCount',
] as const;

// Local-dev fallback. `rayfin up` has not provisioned a Fabric SQL database
// yet when running purely on localhost, so the console renders a small fixture
// that mirrors the shape the Blazor app pushes. This keeps the UI reviewable
// before a Fabric capacity is available -- it is never used once
// VITE_RAYFIN_API_URL points at a deployed backend.
const SAMPLE_HOUSEHOLDS: HouseholdRow[] = [
  {
    id: '00000000-0000-0000-0000-000000000001',
    householdId: '11111111-1111-1111-1111-111111111111',
    name: 'サンプル家族',
    dataSourceMode: 'Sample',
    memberCount: '1',
    residentCount: '1',
    deviceCount: '4',
    lastEventUtc: new Date(Date.now() - 20 * 60_000).toISOString(),
    switchBotStatus: 'NotConfigured',
    switchBotError: '',
    activeLineRecipients: '0',
    alertsInWindow: '0',
    failedAlertsInWindow: '0',
    latestRiskLevel: 'Low',
    needsAttention: false,
    capturedAt: new Date(),
    powerTodayWh: '412',
    powerBaselineWh: '398',
    powerTrend: 'Typical',
  },
  {
    id: '00000000-0000-0000-0000-000000000002',
    householdId: '22222222-2222-2222-2222-222222222222',
    name: '田中家',
    dataSourceMode: 'Production',
    memberCount: '3',
    residentCount: '1',
    deviceCount: '6',
    lastEventUtc: new Date(Date.now() - 9 * 3600_000).toISOString(),
    switchBotStatus: 'Error',
    switchBotError: 'SwitchBot API returned 401 (token may have been revoked)',
    activeLineRecipients: '2',
    alertsInWindow: '3',
    failedAlertsInWindow: '1',
    latestRiskLevel: 'High',
    needsAttention: true,
    capturedAt: new Date(),
    // The shape that matters operationally: a home well below its own habit.
    powerTodayWh: '86',
    powerBaselineWh: '540',
    powerTrend: 'Lower',
  },
];

const SAMPLE_ALERTS: AlertRow[] = [
  {
    id: '00000000-0000-0000-0000-0000000000a1',
    householdId: '22222222-2222-2222-2222-222222222222',
    householdName: '田中家',
    riskLevel: 'High',
    score: '82',
    reason: '長時間の無反応',
    success: false,
    error: 'LINE push failed (429 rate limited)',
    sentAt: new Date(Date.now() - 3 * 3600_000),
  },
  {
    id: '00000000-0000-0000-0000-0000000000a2',
    householdId: '22222222-2222-2222-2222-222222222222',
    householdName: '田中家',
    riskLevel: 'Medium',
    score: '48',
    reason: '深夜の活動増加',
    success: true,
    error: '',
    sentAt: new Date(Date.now() - 26 * 3600_000),
  },
];

// Local-dev sample activity: a synthetic two-week rhythm so the charts have
// shape before a Fabric backend exists. Deliberately labelled `Sample` so it is
// distinguishable from the SwitchBotPoll/AppCommand sources of real data.
const SAMPLE_ACTIVITY: ActivityRow[] = (() => {
  const rows: ActivityRow[] = [];
  const devices = [
    { id: 'sample-living-light', name: 'リビング照明', type: 'Light' },
    { id: 'sample-bedroom-light', name: '寝室照明', type: 'Light' },
    { id: 'sample-fan', name: '扇風機', type: 'Fan' },
  ];
  // Rough diurnal weighting: quiet at night, busy morning / evening.
  const weights = [
    0, 0, 1, 2, 1, 0, 0, 3, 4, 2, 1, 1, 1, 2, 3, 1, 1, 1, 2, 3, 4, 5, 6, 2,
  ];
  const start = new Date();
  start.setUTCHours(0, 0, 0, 0);
  for (let day = 13; day >= 0; day -= 1) {
    for (let hour = 0; hour < 24; hour += 1) {
      const count = weights[hour];
      if (count === 0) continue;
      const device = devices[(day + hour) % devices.length];
      const bucket = new Date(start);
      bucket.setUTCDate(bucket.getUTCDate() - day);
      bucket.setUTCHours(hour);
      rows.push({
        id: `sample-${day}-${hour}`,
        householdId: '22222222-2222-2222-2222-222222222222',
        householdName: '田中家',
        deviceId: device.id,
        deviceName: device.name,
        deviceType: device.type,
        bucketStart: bucket,
        eventCount: String(count),
        onCount: String(Math.ceil(count / 2)),
        source: 'Sample',
        // Roughly proportional to how busy the hour is, so the sample charts have the
        // same shape as the activity ones without pretending to be a real meter.
        energyWh: (count * 18 + hour * 2).toFixed(1),
      });
    }
  }
  return rows;
})();

/**
 * Local-dev sample router traffic. Every row is MockAiRouter on purpose: with no
 * Model Router deployment configured, that is exactly what the Blazor app records, so
 * the fixture cannot be mistaken for evidence of real routing.
 */
const SAMPLE_AI_CALLS: AiRouterCallRow[] = [
  {
    id: 'sample-ai-1',
    purpose: 'intent',
    router: 'MockAiRouter',
    resolvedModel: 'mock/local-rules',
    callCount: '2',
    successCount: '2',
    avgDurationMs: '1',
    lastCalledAt: new Date(Date.now() - 3 * 3600_000),
  },
];

/**
 * How the rows on screen were obtained. The console must never imply a live
 * Fabric read when it is actually serving the bundled snapshot, so the UI reads
 * this and says so.
 */
export type DataOrigin = 'fabric' | 'snapshot' | 'sample';

let dataOrigin: DataOrigin = 'fabric';

export function getDataOrigin(): DataOrigin {
  return dataOrigin;
}

/**
 * Call once before a refresh loads the tables.
 *
 * The origin is a module-level value shared by four reads that run concurrently,
 * so "did anything degrade?" can only be answered per refresh. Clearing it here
 * and letting the reads below downgrade it -- never upgrade it -- keeps both
 * halves honest: the banner disappears once Fabric comes back, and it still
 * appears when only one of the four tables fell back.
 *
 * This is the part that was wrong twice. Guarding the downgrade made the banner
 * stick for the rest of the session; writing 'fabric' on every success instead
 * let whichever read happened to finish last erase a real fallback, which is
 * worse -- the console then draws bundled numbers while claiming they are live.
 */
export function beginRefresh(): void {
  dataOrigin = 'fabric';
}

export const SNAPSHOT_TAKEN_AT = SNAPSHOT_CAPTURED_AT;

/**
 * Reads from Fabric, but degrades to the bundled production snapshot when the
 * backend is unreachable (typically because the Fabric capacity is paused) or
 * returns nothing at all. An empty result is treated as unavailable so the
 * console shows real history rather than a blank chart.
 */
async function withSnapshotFallback<T>(
  snapshot: T[],
  read: () => Promise<T[]>
): Promise<T[]> {
  try {
    const rows = await read();
    if (rows.length > 0) {
      // Deliberately does not write 'fabric' back. These four reads race, and a
      // success arriving after a fallback must not cancel it out. beginRefresh()
      // is what clears the value at the top of each refresh.
      return rows;
    }
  } catch (error) {
    console.warn('Fabric read failed; falling back to the bundled snapshot', error);
  }

  dataOrigin = 'snapshot';
  return snapshot.map((row) => ({ ...row }));
}

export async function getHouseholds(): Promise<HouseholdRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return sortHouseholds([...SAMPLE_HOUSEHOLDS]);
  }

  return withSnapshotFallback(SNAPSHOT_HOUSEHOLDS, async () => {
    const client = getRayfinClient();
    const results = await client.data.HouseholdSnapshot.select([
      ...HOUSEHOLD_FIELDS,
    ]).execute();

    return results as unknown as HouseholdRow[];
  }).then(sortHouseholds);
}

export async function getAlerts(limit = 50): Promise<AlertRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_ALERTS]
      .sort((a, b) => b.sentAt.getTime() - a.sentAt.getTime())
      .slice(0, limit);
  }

  const rows = await withSnapshotFallback(SNAPSHOT_ALERTS, async () => {
    const client = getRayfinClient();
    const results = await client.data.AlertRecord.select([...ALERT_FIELDS])
      .orderBy({ sentAt: 'desc' })
      .first(limit)
      .execute();

    return results as unknown as AlertRow[];
  });

  return rows
    .slice()
    .sort((a, b) => new Date(b.sentAt).getTime() - new Date(a.sentAt).getTime())
    .slice(0, limit);
}

/** Hourly device-activity buckets, oldest first, for the timeline charts. */
export async function getActivity(limit = 2000): Promise<ActivityRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_ACTIVITY].slice(-limit);
  }

  const rows = await withSnapshotFallback(SNAPSHOT_ACTIVITY, async () => {
    const client = getRayfinClient();
    const results = await client.data.ActivityBucket.select([...ACTIVITY_FIELDS])
      .orderBy({ bucketStart: 'desc' })
      .first(limit)
      .execute();

    return results as unknown as ActivityRow[];
  });

  return rows
    .slice(-limit)
    .sort(
      (a, b) => new Date(a.bucketStart).getTime() - new Date(b.bucketStart).getTime()
    );
}

/**
 * Local-dev sample outdoor observations: one point, a synthetic daily swing, so the
 * temperature chart has shape before a Fabric backend exists. Labelled 'sample' as the
 * point code for the same reason SAMPLE_ACTIVITY uses source 'Sample' -- it must never
 * be mistaken for a 気象庁 / 環境省 observation.
 *
 * WBGT is left empty outside the warm hours rather than computed, because the real
 * series is published, not derived, and inventing one here would teach the chart to
 * draw a number the source never issued.
 */
const SAMPLE_OUTDOOR: OutdoorRow[] = (() => {
  const rows: OutdoorRow[] = [];
  const start = new Date();
  start.setUTCHours(0, 0, 0, 0);

  for (let day = 6; day >= 0; day -= 1) {
    for (let hour = 0; hour < 24; hour += 1) {
      const bucket = new Date(start);
      bucket.setUTCDate(bucket.getUTCDate() - day);
      bucket.setUTCHours(hour);

      // Coolest before dawn, warmest mid-afternoon (JST ~14:00 = 05:00 UTC).
      const swing = Math.cos(((hour - 5 + 24) % 24) * (Math.PI / 12));
      const temp = 26 + swing * 6;
      const warm = temp >= 28;

      rows.push({
        id: `sample-outdoor-${day}-${hour}`,
        pointCode: 'sample',
        areaName: 'サンプル地点',
        bucketStart: bucket,
        temperatureC: temp.toFixed(1),
        minTemperatureC: (temp - 0.6).toFixed(1),
        maxTemperatureC: (temp + 0.6).toFixed(1),
        humidityPercent: (72 - swing * 12).toFixed(0),
        maxWbgt: warm ? (temp - 2.5).toFixed(1) : '',
        heatLevel: warm ? (temp >= 31 ? '4' : '3') : '0',
        coldLevel: '0',
        sampleCount: '6',
      });
    }
  }

  return rows;
})();

/** AI router traffic, busiest group first. */
export async function getAiRouterCalls(): Promise<AiRouterCallRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_AI_CALLS];
  }

  const rows = await withSnapshotFallback(SNAPSHOT_AI_CALLS, async () => {
    const client = getRayfinClient();
    const results = await client.data.AiRouterCall.select([...AI_FIELDS]).execute();

    return results as unknown as AiRouterCallRow[];
  });

  return rows
    .slice()
    .sort((a, b) => Number(b.callCount || 0) - Number(a.callCount || 0));
}

/**
 * Hourly outdoor observations, oldest first.
 *
 * Unlike the other reads this does not fall back to the bundled snapshot. The table
 * is newer than the deployment, so an empty result is the ordinary "the sync has not
 * written any weather yet" state, and flipping the whole console's origin badge to
 * "snapshot" over it would misreport where every other figure came from. An empty
 * list reaches the UI as 未計測, which is the truth.
 */
export async function getOutdoor(limit = 2000): Promise<OutdoorRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_OUTDOOR].slice(-limit);
  }

  let rows: OutdoorRow[] = [];
  try {
    const client = getRayfinClient();
    rows = (await client.data.OutdoorReading.select([...OUTDOOR_FIELDS])
      .orderBy({ bucketStart: 'desc' })
      .first(limit)
      .execute()) as unknown as OutdoorRow[];
  } catch (error) {
    console.warn('Outdoor read failed; the console will show 未計測', error);
    return [];
  }

  return rows
    .slice()
    .sort(
      (a, b) => new Date(a.bucketStart).getTime() - new Date(b.bucketStart).getTime()
    );
}

/** Households needing attention first, then by name, so triage is the default view. */
export function sortHouseholds(rows: HouseholdRow[]): HouseholdRow[] {
  return rows.sort((a, b) => {
    if (a.needsAttention !== b.needsAttention) return a.needsAttention ? -1 : 1;
    return a.name.localeCompare(b.name, 'ja');
  });
}

export interface ConsoleTotals {
  households: number;
  production: number;
  devices: number;
  alerts: number;
  failedAlerts: number;
  needingAttention: number;
}

export function summarize(rows: HouseholdRow[]): ConsoleTotals {
  const toInt = (value: string) => {
    const parsed = Number.parseInt(value, 10);
    return Number.isNaN(parsed) ? 0 : parsed;
  };

  return {
    households: rows.length,
    production: rows.filter((r) => r.dataSourceMode === 'Production').length,
    devices: rows.reduce((sum, r) => sum + toInt(r.deviceCount), 0),
    alerts: rows.reduce((sum, r) => sum + toInt(r.alertsInWindow), 0),
    failedAlerts: rows.reduce(
      (sum, r) => sum + toInt(r.failedAlertsInWindow),
      0
    ),
    needingAttention: rows.filter((r) => r.needsAttention).length,
  };
}
