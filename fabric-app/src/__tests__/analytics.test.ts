import { describe, expect, it } from 'vitest';

import {
  activitySummary,
  alertsByDay,
  dailyActivity,
  dailyOutdoor,
  deliveryStats,
  deviceBreakdown,
  hourlyRhythm,
  householdBars,
  outdoorSummary,
  pipelineStats,
  riskDistribution,
  routerModels,
  routerSummary,
  scopeRows,
  UNRESOLVED_BAR,
} from '@/services/analytics';
import type {
  ActivityRow,
  AiRouterCallRow,
  AlertRow,
  HouseholdRow,
  OutdoorRow,
} from '@/services/monitoring';

function outdoor(overrides: Partial<OutdoorRow> = {}): OutdoorRow {
  return {
    id: 'o1',
    pointCode: '44132',
    areaName: '東京',
    bucketStart: new Date('2026-08-01T03:00:00Z'),
    temperatureC: '30',
    minTemperatureC: '29',
    maxTemperatureC: '31',
    humidityPercent: '60',
    maxWbgt: '28',
    heatLevel: '3',
    coldLevel: '0',
    sampleCount: '6',
    ...overrides,
  };
}

describe('outdoor observations', () => {
  it('keeps an unobserved day as a gap rather than 0℃', () => {
    // 2026-08-01 09:00 JST and 2026-08-03 09:00 JST: the 2nd was never observed.
    const points = dailyOutdoor([
      outdoor({ bucketStart: new Date('2026-08-01T00:00:00Z') }),
      outdoor({ id: 'o2', bucketStart: new Date('2026-08-03T00:00:00Z') }),
    ]);

    expect(points).toHaveLength(3);
    expect(points[1].measured).toBe(false);

    // The gap must not read as a cold snap: nothing downstream may treat this as 0℃.
    expect(points[0].measured).toBe(true);
    expect(points[2].measured).toBe(true);
  });

  it('bands a day by its own min and max, not by the hourly mean', () => {
    const [point] = dailyOutdoor([
      outdoor({
        bucketStart: new Date('2026-08-01T00:00:00Z'),
        temperatureC: '26',
        minTemperatureC: '24',
        maxTemperatureC: '28',
      }),
      outdoor({
        id: 'o2',
        bucketStart: new Date('2026-08-01T05:00:00Z'),
        temperatureC: '34',
        minTemperatureC: '33',
        maxTemperatureC: '36',
        maxWbgt: '31',
      }),
    ]);

    expect(point.minC).toBe(24);
    expect(point.maxC).toBe(36);
    expect(point.avgC).toBe(30);
    // The peak, not the mean: the warning is about the worst moment of the day.
    expect(point.maxWbgt).toBe(31);
  });

  it('reports 未計測 as null instead of zero when WBGT is out of season', () => {
    const summary = outdoorSummary([
      outdoor({ temperatureC: '2', maxWbgt: '', heatLevel: '0', coldLevel: '2' }),
    ]);

    expect(summary.maxWbgt).toBeNull();
    expect(summary.minC).toBe(2);
    expect(summary.cautionHours).toBe(0);
  });

  it('counts 警戒 hours and the newest observation', () => {
    const summary = outdoorSummary([
      outdoor({ bucketStart: new Date('2026-08-01T03:00:00Z'), heatLevel: '3' }),
      outdoor({
        id: 'o2',
        bucketStart: new Date('2026-08-01T05:00:00Z'),
        temperatureC: '35',
        heatLevel: '4',
        areaName: '東京',
      }),
      outdoor({ id: 'o3', bucketStart: new Date('2026-08-01T04:00:00Z'), heatLevel: '2' }),
    ]);

    expect(summary.hours).toBe(3);
    expect(summary.points).toBe(1);
    expect(summary.cautionHours).toBe(2);
    expect(summary.latestC).toBe(35);
    expect(summary.maxC).toBe(35);
    expect(summary.latestAt?.toISOString()).toBe('2026-08-01T05:00:00.000Z');
  });
});

function aiCall(overrides: Partial<AiRouterCallRow> = {}): AiRouterCallRow {
  return {
    id: 'ai1',
    purpose: 'intent',
    router: 'Azure Model Router',
    resolvedModel: 'deepseek-v4-pro',
    callCount: '7',
    successCount: '7',
    avgDurationMs: '3856',
    lastCalledAt: new Date('2026-08-09T04:03:38.000Z'),
    ...overrides,
  };
}

function bucket(overrides: Partial<ActivityRow> = {}): ActivityRow {
  return {
    id: 'a1',
    householdId: 'hh1',
    householdName: 'テスト世帯',
    deviceName: 'リビング照明',
    deviceType: 'Light',
    bucketStart: new Date('2026-08-10T00:00:00.000Z'),
    eventCount: '3',
    onCount: '2',
    source: 'SwitchBotPoll',
    ...overrides,
  };
}

function household(overrides: Partial<HouseholdRow> = {}): HouseholdRow {
  return {
    id: 'h1',
    householdId: 'hh1',
    name: 'テスト世帯',
    dataSourceMode: 'Production',
    memberCount: '2',
    residentCount: '1',
    deviceCount: '3',
    lastEventUtc: '2026-08-11T00:00:00.000Z',
    switchBotStatus: 'Connected',
    switchBotError: '',
    activeLineRecipients: '2',
    alertsInWindow: '4',
    failedAlertsInWindow: '1',
    latestRiskLevel: 'Medium',
    needsAttention: false,
    capturedAt: new Date('2026-08-11T01:00:00.000Z'),
    ...overrides,
  };
}

function alert(overrides: Partial<AlertRow> = {}): AlertRow {
  return {
    id: 'a1',
    householdId: 'hh1',
    householdName: 'テスト世帯',
    riskLevel: 'Medium',
    score: '35',
    reason: '無反応',
    success: true,
    error: '',
    sentAt: new Date('2026-08-11T09:00:00'),
    ...overrides,
  };
}

const NOW = new Date('2026-08-11T23:00:00');

describe('scopeRows', () => {
  const real = household({ householdId: 'hh-real', dataSourceMode: 'Production' });
  const demo = household({ id: 'h2', householdId: 'hh-demo', dataSourceMode: 'Sample' });
  const alerts = [
    alert({ householdId: 'hh-real' }),
    alert({ id: 'a2', householdId: 'hh-demo' }),
  ];
  const activity = [
    bucket({ householdId: 'hh-real' }),
    bucket({ id: 'a2', householdId: 'hh-demo' }),
  ];

  it('keeps only the rows belonging to the chosen data source', () => {
    const scoped = scopeRows([real, demo], alerts, activity, 'Production');

    expect(scoped.households).toEqual([real]);
    expect(scoped.alerts.map((a) => a.householdId)).toEqual(['hh-real']);
    expect(scoped.activity.map((a) => a.householdId)).toEqual(['hh-real']);
  });

  it('selects the demo side without touching the production rows', () => {
    const scoped = scopeRows([real, demo], alerts, activity, 'Sample');

    expect(scoped.households).toEqual([demo]);
    expect(scoped.alerts.map((a) => a.householdId)).toEqual(['hh-demo']);
    expect(scoped.activity.map((a) => a.householdId)).toEqual(['hh-demo']);
  });

  it('passes everything through untouched when nothing is being separated', () => {
    const scoped = scopeRows([real, demo], alerts, activity, 'all');

    expect(scoped.households).toHaveLength(2);
    expect(scoped.alerts).toHaveLength(2);
    expect(scoped.activity).toHaveLength(2);
  });

  // An alert whose household has since been deleted cannot be called demo or
  // production, and silently filing it under whichever tab is open would make a
  // real delivery failure appear in the demo view.
  it('drops rows that point at a household the table does not know', () => {
    const orphan = alert({ id: 'a9', householdId: 'hh-gone' });
    const scoped = scopeRows([real], [...alerts, orphan], activity, 'Production');

    expect(scoped.alerts.map((a) => a.id)).toEqual(['a1']);
  });
});

describe('alertsByDay', () => {
  it('always returns a fixed-width window, oldest first', () => {
    const buckets = alertsByDay([], 7, NOW);

    expect(buckets).toHaveLength(7);
    expect(buckets[6].label).toBe('8/11');
    expect(buckets[0].label).toBe('8/5');
    expect(buckets.every((bucket) => bucket.total === 0)).toBe(true);
  });

  it('counts totals and failures into the matching day', () => {
    const buckets = alertsByDay(
      [
        alert({ sentAt: new Date('2026-08-11T09:00:00') }),
        alert({ id: 'a2', sentAt: new Date('2026-08-11T20:00:00'), success: false }),
        alert({ id: 'a3', sentAt: new Date('2026-08-09T12:00:00') }),
      ],
      7,
      NOW
    );

    expect(buckets[6]).toMatchObject({ total: 2, failed: 1 });
    expect(buckets[4]).toMatchObject({ total: 1, failed: 0 });
  });

  it('ignores alerts outside the window and unparsable timestamps', () => {
    const buckets = alertsByDay(
      [
        alert({ sentAt: new Date('2026-01-01T00:00:00') }),
        alert({ id: 'a2', sentAt: new Date('nope') }),
      ],
      7,
      NOW
    );

    expect(buckets.reduce((sum, bucket) => sum + bucket.total, 0)).toBe(0);
  });

  it('accepts serialised dates', () => {
    const buckets = alertsByDay(
      [alert({ sentAt: '2026-08-11T09:00:00' as unknown as Date })],
      7,
      NOW
    );

    expect(buckets[6].total).toBe(1);
  });
});

describe('riskDistribution', () => {
  it('drops empty levels and keeps High first', () => {
    const slices = riskDistribution([
      alert({ riskLevel: 'High' }),
      alert({ id: 'a2', riskLevel: 'Medium' }),
      alert({ id: 'a3', riskLevel: 'Medium' }),
    ]);

    expect(slices.map((slice) => [slice.level, slice.count])).toEqual([
      ['High', 1],
      ['Medium', 2],
    ]);
  });

  it('buckets unrecognised levels as unknown', () => {
    const slices = riskDistribution([alert({ riskLevel: '' })]);

    expect(slices).toEqual([expect.objectContaining({ level: 'Unknown', count: 1 })]);
  });
});

describe('deliveryStats', () => {
  it('reports a 100% rate when there is nothing to deliver', () => {
    expect(deliveryStats([])).toMatchObject({ total: 0, successRate: 100 });
  });

  it('rounds the success rate', () => {
    const stats = deliveryStats([
      alert(),
      alert({ id: 'a2' }),
      alert({ id: 'a3', success: false }),
    ]);

    expect(stats).toMatchObject({ total: 3, success: 2, failed: 1, successRate: 67 });
  });
});

describe('householdBars', () => {
  it('sorts by device count descending and coerces the string counters', () => {
    const bars = householdBars([
      household({ id: 'a', name: '少ない', deviceCount: '1' }),
      household({ id: 'b', name: '多い', deviceCount: '9', alertsInWindow: 'x' }),
    ]);

    expect(bars.map((bar) => bar.name)).toEqual(['多い', '少ない']);
    expect(bars[0].alerts).toBe(0);
  });
});

describe('pipelineStats', () => {
  it('aggregates the numbers the diagram labels', () => {
    const stats = pipelineStats(
      [
        household({ id: 'a', deviceCount: '3', activeLineRecipients: '2' }),
        household({
          id: 'b',
          deviceCount: '4',
          activeLineRecipients: '1',
          dataSourceMode: 'Sample',
          switchBotStatus: 'NotConfigured',
          lastEventUtc: '2026-08-12T00:00:00.000Z',
        }),
      ],
      [alert(), alert({ id: 'a2', success: false })]
    );

    expect(stats).toMatchObject({
      devices: 7,
      households: 2,
      productionHouseholds: 1,
      lineRecipients: 3,
      alerts: 2,
      failedAlerts: 1,
      connectedSwitchBots: 1,
    });
    expect(stats.lastEvent?.toISOString()).toBe('2026-08-12T00:00:00.000Z');
  });

  it('leaves timestamps null when the source rows have none', () => {
    const stats = pipelineStats([household({ lastEventUtc: '' })], []);

    expect(stats.lastEvent).toBeNull();
  });

  it('counts activity events and rows for the diagram', () => {
    const stats = pipelineStats([household()], [], [
      bucket({ eventCount: '3' }),
      bucket({ id: 'a2', eventCount: '5' }),
    ]);

    expect(stats.activityEvents).toBe(8);
    expect(stats.fabricRows).toBe(2);
  });

  it('carries the data origin so the diagram cannot claim to be live', () => {
    expect(pipelineStats([household()], []).origin).toBe('fabric');
    expect(pipelineStats([household()], [], [], 'snapshot').origin).toBe('snapshot');
  });
});

describe('dailyActivity', () => {
  it('sums buckets per UTC day and fills gaps with zero', () => {
    const points = dailyActivity([
      bucket({ bucketStart: new Date('2026-08-10T01:00:00.000Z'), eventCount: '2', onCount: '1' }),
      bucket({ bucketStart: new Date('2026-08-10T05:00:00.000Z'), eventCount: '3', onCount: '2' }),
      bucket({ bucketStart: new Date('2026-08-12T09:00:00.000Z'), eventCount: '4', onCount: '0' }),
    ]);

    expect(points.map((point) => point.events)).toEqual([5, 0, 4]);
    expect(points[0].onEvents).toBe(3);
    expect(points[1].label).toBe('8/11');
  });

  it('caps the window to the most recent days', () => {
    const points = dailyActivity(
      [
        bucket({ bucketStart: new Date('2026-07-01T00:00:00.000Z') }),
        bucket({ bucketStart: new Date('2026-08-10T00:00:00.000Z') }),
      ],
      3
    );

    expect(points).toHaveLength(3);
    expect(points[points.length - 1].label).toBe('8/10');
  });

  it('returns nothing when there is no activity', () => {
    expect(dailyActivity([])).toEqual([]);
  });
});

describe('hourlyRhythm', () => {
  it('always returns 24 slots shifted into JST', () => {
    const cells = hourlyRhythm([
      bucket({ bucketStart: new Date('2026-08-10T22:00:00.000Z'), eventCount: '4' }),
    ]);

    expect(cells).toHaveLength(24);
    // 22:00 UTC is 07:00 the next day in JST.
    expect(cells[7].events).toBe(4);
    expect(cells[7].mode).toBe('events');
    expect(cells[22].events).toBe(0);
  });

  it('uses hourly energy when metered data is present', () => {
    const cells = hourlyRhythm([
      bucket({ bucketStart: new Date('2026-08-10T22:00:00.000Z'), eventCount: '4', energyWh: '12' }),
      bucket({ bucketStart: new Date('2026-08-11T22:00:00.000Z'), eventCount: '2', energyWh: '8' }),
    ]);

    expect(cells[7].mode).toBe('energy');
    expect(cells[7].events).toBe(10);
  });
});

describe('deviceBreakdown', () => {
  it('totals per device, busiest first', () => {
    const slices = deviceBreakdown([
      bucket({ deviceName: '扇風機', eventCount: '2' }),
      bucket({ deviceName: 'リビング照明', eventCount: '5' }),
      bucket({ deviceName: '扇風機', eventCount: '6' }),
    ]);

    expect(slices.map((slice) => [slice.name, slice.events])).toEqual([
      ['扇風機', 8],
      ['リビング照明', 5],
    ]);
  });

  it('groups renamed buckets by device id and keeps the latest name', () => {
    const slices = deviceBreakdown([
      bucket({
        deviceId: 'dev-1',
        deviceName: 'リビングの電気',
        bucketStart: new Date('2026-08-10T00:00:00.000Z'),
        eventCount: '11',
      }),
      bucket({
        deviceId: 'dev-1',
        deviceName: 'プラグミニ 92',
        bucketStart: new Date('2026-08-11T00:00:00.000Z'),
        eventCount: '12',
      }),
    ]);

    expect(slices).toHaveLength(1);
    expect(slices[0].name).toBe('プラグミニ 92');
    expect(slices[0].events).toBe(23);
  });
});

describe('activitySummary', () => {
  it('reports totals, coverage and distinct ingestion sources', () => {
    const summary = activitySummary([
      bucket({ eventCount: '2', source: 'SwitchBotPoll' }),
      bucket({
        bucketStart: new Date('2026-08-11T10:00:00.000Z'),
        deviceName: '扇風機',
        eventCount: '3',
        source: 'AppCommand',
      }),
    ]);

    expect(summary.events).toBe(5);
    expect(summary.buckets).toBe(2);
    expect(summary.devices).toBe(2);
    expect(summary.days).toBe(2);
    expect(summary.sources).toEqual(['AppCommand', 'SwitchBotPoll']);
    expect(summary.from?.toISOString()).toBe('2026-08-10T00:00:00.000Z');
  });

  it('counts distinct devices by stable device id when present', () => {
    const summary = activitySummary([
      bucket({ deviceId: 'dev-1', deviceName: 'リビングの電気' }),
      bucket({ deviceId: 'dev-1', deviceName: 'プラグミニ 92' }),
    ]);

    expect(summary.devices).toBe(1);
  });
});

describe('routerModels', () => {
  it('collapses purposes into one bar per model and weights latency by call count', () => {
    const bars = routerModels([
      aiCall({ purpose: 'intent', resolvedModel: 'qwen3.7-plus', callCount: '6', avgDurationMs: '1000' }),
      aiCall({ purpose: 'summary', resolvedModel: 'qwen3.7-plus', callCount: '2', avgDurationMs: '5000' }),
    ]);

    expect(bars).toHaveLength(1);
    expect(bars[0].calls).toBe(8);
    expect(bars[0].purposes).toEqual(['intent', 'summary']);
    // (6*1000 + 2*5000) / 8 -- not the unweighted 3000.
    expect(bars[0].avgMs).toBe(2000);
  });

  it('excludes the offline stub but keeps failures as their own bar', () => {
    const bars = routerModels([
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2' }),
      aiCall({ resolvedModel: '', callCount: '1', successCount: '0' }),
    ]);

    // The stub never reached the router, so it gets no bar at all. The failure
    // did reach it and gets one, flagged so it is not drawn as a model.
    expect(bars).toHaveLength(1);
    expect(bars[0].unresolved).toBe(true);
    expect(bars[0].model).toBe(UNRESOLVED_BAR);
    expect(bars[0].calls).toBe(1);
  });

  it('gives no failure bar when every call resolved', () => {
    const bars = routerModels([
      aiCall({ resolvedModel: 'gpt-4.1-mini', callCount: '5' }),
    ]);

    expect(bars.some((bar) => bar.unresolved)).toBe(false);
  });
});

describe('routerSummary', () => {
  it('separates Model Router traffic from the local stub', () => {
    const summary = routerSummary([
      aiCall({ resolvedModel: 'gpt-4.1-mini', callCount: '50', successCount: '50', avgDurationMs: '1800' }),
      aiCall({ resolvedModel: 'glm-5.2', callCount: '10', successCount: '10', avgDurationMs: '6000' }),
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2', successCount: '2', avgDurationMs: '1' }),
    ]);

    expect(summary.calls).toBe(60);
    expect(summary.mockCalls).toBe(2);
    expect(summary.models).toBe(2);
    // (50*1800 + 10*6000) / 60 -- the stub is excluded, so it cannot drag this down.
    expect(summary.avgMs).toBe(2500);
  });

  it('counts a failed call even though it has no model to attribute', () => {
    const summary = routerSummary([
      aiCall({ resolvedModel: '', callCount: '1', successCount: '0' }),
    ]);

    expect(summary.calls).toBe(1);
    expect(summary.success).toBe(0);
    expect(summary.models).toBe(0);
    expect(summary.unresolvedCalls).toBe(1);
  });

  // The console used to print "記録した 77 回" above bars that added up to 76 and
  // said nothing about the gap, which reads as a miscount. The failed call now
  // gets its own bar, so the bars total every router call.
  it('draws a bar for every router call, including the ones with no model', () => {
    const rows = [
      aiCall({ resolvedModel: 'gpt-4.1-mini', callCount: '52' }),
      aiCall({ resolvedModel: 'deepseek-v4-pro', callCount: '10' }),
      aiCall({ resolvedModel: 'qwen3.7-plus', callCount: '9' }),
      aiCall({ resolvedModel: 'glm-5.2', callCount: '5' }),
      aiCall({ resolvedModel: '', callCount: '1', successCount: '0' }),
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2' }),
    ];

    const summary = routerSummary(rows);
    const bars = routerModels(rows);
    const barTotal = bars.reduce((sum, bar) => sum + bar.calls, 0);

    expect(summary.calls).toBe(77);
    expect(barTotal).toBe(77);

    // The failure is one bar, flagged, and last -- it is a leftover, not a model
    // competing with the others for rank.
    const failures = bars.filter((bar) => bar.unresolved);
    expect(failures).toHaveLength(1);
    expect(failures[0].calls).toBe(1);
    expect(failures[0].success).toBe(0);
    expect(bars[bars.length - 1].unresolved).toBe(true);

    // The failure bar is not a model, so it must not inflate the model count.
    expect(summary.models).toBe(4);
    expect(bars.filter((bar) => !bar.unresolved)).toHaveLength(4);

    // The diagram card reads off pipelineStats and leads with aiCalls, which is
    // exactly what the bars now add up to. This is the pair the user caught
    // disagreeing on screen.
    const stats = pipelineStats([household()], [alert()], [], 'fabric', rows);
    expect(stats.aiCalls).toBe(barTotal);
    expect(stats.aiResolvedCalls).toBe(barTotal - 1);
  });
});

describe('pipelineStats AI totals', () => {
  it('feeds the diagram only calls that reached the router', () => {
    const stats = pipelineStats([household()], [alert()], [], 'fabric', [
      aiCall({ resolvedModel: 'qwen3.7-plus', callCount: '6' }),
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2' }),
    ]);

    expect(stats.aiCalls).toBe(6);
    expect(stats.aiModels).toBe(1);
    expect(stats.aiResolvedCalls).toBe(6);
  });

  it('reports no AI traffic when the table is empty', () => {
    const stats = pipelineStats([household()], [alert()]);
    expect(stats.aiCalls).toBe(0);
    expect(stats.aiModels).toBe(0);
  });
});
