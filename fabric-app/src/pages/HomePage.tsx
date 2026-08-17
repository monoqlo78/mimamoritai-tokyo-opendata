import { useCallback, useEffect, useState } from 'react';

import {
  ActivityArea,
  AlertTimeline,
  DeliveryGauge,
  DeviceBreakdown,
  EnergyProfile,
  EnergyTrend,
  formatC,
  formatWh,
  HouseholdBars,
  OutdoorTrend,
  RhythmHeatmap,
  RiskDonut,
  RouterModels,
} from '@/components/charts';
import { DataFlowCanvas } from '@/components/DataFlowCanvas';
import { useAuth } from '@/hooks/AuthContext';
import { askConsole, isAskAvailable, SUGGESTED_QUESTIONS, type AskResult } from '@/services/ask';
import {
  activitySummary,
  alertsByDay,
  dailyActivity,
  dailyEnergy,
  dailyOutdoor,
  deliveryStats,
  deviceBreakdown,
  hourlyEnergy,
  hourlyRhythm,
  householdBars,
  outdoorSummary,
  pipelineStats,
  riskDistribution,
  routerModels,
  routerSummary,
  scopeRows,
  type DataScope,
} from '@/services/analytics';
import {
  beginRefresh,
  getActivity,
  getAiRouterCalls,
  getAlerts,
  getDataOrigin,
  getHouseholds,
  getOutdoor,
  summarize,
  SNAPSHOT_TAKEN_AT,
  type ActivityRow,
  type AiRouterCallRow,
  type AlertRow,
  type DataOrigin,
  type HouseholdRow,
  type OutdoorRow,
} from '@/services/monitoring';
import { isDemoCapture } from '@/services/bootstrap';
import { isLocalBackend } from '@/services/rayfinClient';

export function HomePage() {
  const { signOut, user } = useAuth();
  const [households, setHouseholds] = useState<HouseholdRow[]>([]);
  const [alerts, setAlerts] = useState<AlertRow[]>([]);
  const [activity, setActivity] = useState<ActivityRow[]>([]);
  const [aiCalls, setAiCalls] = useState<AiRouterCallRow[]>([]);
  const [outdoor, setOutdoor] = useState<OutdoorRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [hasLoaded, setHasLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [origin, setOrigin] = useState<DataOrigin>('fabric');
  const [energyDays, setEnergyDays] = useState(7);

  // Production by default. An operator opening this console is asking about real
  // homes, and the seeded demo household is the larger of the two by event count --
  // leaving it in by default makes every chart mostly describe data nobody lives in.
  const [scope, setScope] = useState<DataScope>('Production');

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    beginRefresh();
    try {
      const [householdRows, alertRows, activityRows, aiRows, outdoorRows] = await Promise.all([
        getHouseholds(),
        getAlerts(),
        // Activity is the newest table; tolerate a backend that predates it so
        // the rest of the console still renders.
        getActivity().catch(() => [] as ActivityRow[]),
        getAiRouterCalls().catch(() => [] as AiRouterCallRow[]),
        // Outdoor observations are newer still, for the same reason.
        getOutdoor().catch(() => [] as OutdoorRow[]),
      ]);
      setHouseholds(householdRows);
      setAlerts(alertRows);
      setActivity(activityRows);
      setAiCalls(aiRows);
      setOutdoor(outdoorRows);
      setOrigin(getDataOrigin());
      setHasLoaded(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // The first load has no numbers yet, and every counter on this page renders 0
  // until it does. A dashboard reading "世帯 0 / 呼び出し 0 回" is not an empty
  // state -- it looks exactly like a working console reporting that nothing is
  // there, which is the one thing an operator must never be told by mistake.
  // Refreshes keep the previous numbers on screen instead of blanking them.
  const firstLoad = loading && !hasLoaded;

  // Everything below is derived from the scoped rows, so no chart can quietly add a
  // demo home to a real one. AI router calls are the exception: they are recorded
  // per purpose, not per household, and cannot be attributed either way.
  const scoped = scopeRows(households, alerts, activity, scope);
  const scopeCounts = {
    all: households.length,
    Production: households.filter((h) => h.dataSourceMode === 'Production').length,
    Sample: households.filter((h) => h.dataSourceMode === 'Sample').length,
  };

  const totals = summarize(scoped.households);
  const flow = pipelineStats(scoped.households, scoped.alerts, scoped.activity, origin, aiCalls);
  const timeline = alertsByDay(scoped.alerts);
  const risks = riskDistribution(scoped.alerts);
  const delivery = deliveryStats(scoped.alerts);
  const bars = householdBars(scoped.households);
  const activityDaily = dailyActivity(scoped.activity);
  const rhythm = hourlyRhythm(scoped.activity);
  const rhythmUsesEnergy = rhythm.some((cell) => cell.mode === 'energy');
  const devices = deviceBreakdown(scoped.activity);
  const activityTotals = activitySummary(scoped.activity);
  const energyDaily = dailyEnergy(scoped.activity, energyDays);
  const energyHours = hourlyEnergy(scoped.activity);
  const energyTotal = energyDaily.reduce((sum, point) => sum + point.wh, 0);
  const aiModels = routerModels(aiCalls);
  const aiSummary = routerSummary(aiCalls);

  // Outdoor observations are a public series for an observation point, not a
  // per-household measurement, so they deliberately ignore the household scope.
  const outdoorDaily = dailyOutdoor(outdoor, energyDays);
  const outdoorTotals = outdoorSummary(outdoor);

  // Every "全世帯" caption on this page is now a claim about the selected slice, so it
  // has to name the slice or it silently overstates what was counted.
  const scopeWord = scope === 'all' ? '全世帯' : scope === 'Production' ? '本番世帯' : 'デモ世帯';

  return (
    <div className="bg-gray-50 min-h-screen">
      <header className="flex items-center justify-between px-8 py-5 bg-white border-b border-gray-200">
        <div>
          <h1 className="text-xl font-bold text-gray-900">
            見守り隊 運用コンソール
          </h1>
          <p className="text-xs text-gray-500">
            Microsoft Fabric 上で全世帯の稼働状況を確認します
          </p>
        </div>
        <div className="flex items-center gap-4">
          <button
            onClick={() => void refresh()}
            className="rounded-lg border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
          >
            更新
          </button>
          {user?.email && (
            <span className="text-sm text-gray-600" title={user.email}>
              {user.email}
            </span>
          )}
          <button
            onClick={() => void signOut()}
            className="text-gray-400 hover:text-gray-600 transition-colors text-sm"
          >
            サインアウト
          </button>
        </div>
      </header>

      <main className="mx-auto max-w-7xl px-4 py-8 space-y-6">
        {isDemoCapture() && (
          <div className="rounded-xl border border-slate-300 bg-slate-50 px-4 py-3 text-sm text-slate-700">
            収録用の表示です。{SNAPSHOT_TAKEN_AT.toLocaleString('ja-JP')} 時点で
            Microsoft Fabric の本番データベースから抽出したスナップショットを表示しています。
          </div>
        )}

        {!isDemoCapture() && isLocalBackend() && (
          <div className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            ローカル開発モードです。Fabric のバックエンドが未接続のため、
            サンプルデータを表示しています。
          </div>
        )}

        {!isDemoCapture() && !isLocalBackend() && origin === 'snapshot' && (
          <div className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <span className="font-semibold">Fabric SQL に接続できていません。</span>{' '}
            {SNAPSHOT_TAKEN_AT.toLocaleString('ja-JP')} 時点で本番データベースから
            抽出したスナップショットを表示しています。これは現在の状況ではありません。
            <span className="block mt-1 text-xs text-amber-800">
              Fabric の容量が一時停止している可能性があります。復旧すると自動的に
              最新データの表示に戻ります。
            </span>
          </div>
        )}

        {error && (
          <div className="rounded-xl border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800">
            読み込みに失敗しました: {error}
          </div>
        )}

        {firstLoad ? (
          <section className="rounded-xl border border-gray-200 bg-white p-10 text-center">
            <div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
            <p className="mt-3 text-sm font-medium text-gray-800">
              Fabric から読み込んでいます…
            </p>
            <p className="mt-1 text-xs text-gray-500">
              数字が出るまで少しかかります。0 と表示されないよう、
              読み終わるまで伏せています。
            </p>
          </section>
        ) : (
          <>
        <section className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-gray-200 bg-white px-4 py-3">
          <div>
            <h2 className="text-sm font-semibold text-gray-900">表示するデータ</h2>
            <p className="text-xs text-gray-500">
              デモ世帯と本番世帯は同じテーブルに並んでいます。混ぜて合計すると
              どちらの暮らしも表さない数字になるため、既定では本番だけを集計します。
              {scope !== 'all' && scopeCounts[scope] === 0 && (
                <span className="ml-1 font-medium text-amber-700">
                  この区分の世帯はまだありません。
                </span>
              )}
            </p>
          </div>
          <div className="flex rounded-md border border-gray-200 p-0.5">
            {([
              ['Production', '本番'],
              ['Sample', 'デモ'],
              ['all', 'すべて'],
            ] as const).map(([value, label]) => (
              <button
                key={value}
                type="button"
                onClick={() => setScope(value)}
                className={`rounded px-3 py-1 text-xs font-medium transition ${
                  scope === value
                    ? 'bg-gray-900 text-white'
                    : 'text-gray-500 hover:text-gray-800'
                }`}
              >
                {label}
                <span className={scope === value ? 'ml-1 text-gray-300' : 'ml-1 text-gray-400'}>
                  {scopeCounts[value]}
                </span>
              </button>
            ))}
          </div>
        </section>

        <section className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          <Kpi
            label="世帯"
            value={totals.households}
            sub={scope === 'all' ? `本番 ${totals.production}` : scope === 'Production' ? '本番のみ' : 'デモのみ'}
          />
          <Kpi label="デバイス" value={totals.devices} sub={`${scopeWord}合計`} />
          <Kpi label="通知" value={totals.alerts} sub={`失敗 ${totals.failedAlerts}`} />
          <Kpi
            label="要対応"
            value={totals.needingAttention}
            sub="世帯"
            alert={totals.needingAttention > 0}
          />
          <Kpi label="通知失敗" value={totals.failedAlerts} sub="直近期間" alert={totals.failedAlerts > 0} />
        </section>

        <AskPanel />

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <div className="mb-4 flex flex-wrap items-end justify-between gap-2">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                データはこう流れています
              </h2>
              <p className="text-xs text-gray-500">
                センサーから家族への通知まで。Fabric へは Eventstream と取り込みバッチの2経路があり、
                運ばれる中身は同じ計測値です。この画面が読んでいるのは Azure SQL からの集計同期（15分ごと）、
                Eventhouse を読むのは AI の質問応答で、同じ中身を別の経路・別の読み手が見ています。
                数字はすべて下の表と同じ実データです。
              </p>
            </div>
            <div className="text-right text-xs text-gray-500">
              <div>
                最終イベント <span className="font-medium text-gray-800">{formatTime2(flow.lastEvent)}</span>
              </div>
              <div>
                最終同期 <span className="font-medium text-gray-800">{formatTime2(flow.lastSync)}</span>
              </div>
            </div>
          </div>
          <DataFlowCanvas stats={flow} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <div className="mb-4">
            <h2 className="text-base font-semibold text-gray-900">
              Azure Model Router が選んだモデル
            </h2>
            <p className="mt-1 text-xs text-gray-500">
              見守り隊の AI 呼び出しは Azure AI Foundry の Model Router を通ります。
              記録した {aiSummary.calls.toLocaleString()} 回のうち{' '}
              <span className="font-medium text-rose-700">
                {(aiSummary.calls - aiSummary.unresolvedCalls).toLocaleString()} 回
              </span>{' '}
              が {aiSummary.models} 種類のモデルで応答しました（成功{' '}
              {aiSummary.success.toLocaleString()} 回）。
              モデルはリクエストごとに Model Router が自動で選びます。
            </p>
            <p className="mt-1 text-[11px] text-gray-400">
              AI の呼び出しは用途単位で記録しており世帯に紐づかないため、
              上の本番／デモの絞り込みは掛かりません（常に全件）。
            </p>
            <p className="mt-1 text-xs text-gray-500">
              下段の細い棒は平均応答時間です。LINE の webhook は 8 秒でイベントを
              打ち切るため、締切のある経路には短いタイムアウトを設定しています。
            </p>
            {aiSummary.unresolvedCalls > 0 && (
              <p className="mt-1 text-[11px] text-gray-400">
                残り {aiSummary.unresolvedCalls} 回はモデルが応答する前に失敗した
                呼び出しです。応答したモデル名が記録に残らないため、モデルごとの棒
                には載せられず、末尾の「未応答（失敗）」の棒にまとめています。棒の
                合計は {aiSummary.calls.toLocaleString()} 回になります。
              </p>
            )}
            {aiSummary.mockCalls > 0 && (
              <p className="mt-1 text-[11px] text-gray-400">
                デプロイ未設定時のローカルスタブ {aiSummary.mockCalls} 回は
                Model Router を経由していないため、上の集計から除いています。
              </p>
            )}
          </div>
          <RouterModels models={aiModels} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <div className="mb-4 flex flex-wrap items-end justify-between gap-2">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                機器イベントの推移
              </h2>
              <p className="text-xs text-gray-500">
                実線=総イベント数／点線=ON 判定。1 時間単位で集計した
                DeviceEvents を日次にまとめています。
              </p>
            </div>
            <div className="text-right text-xs text-gray-500">
              <div>
                <span className="font-medium text-gray-800">
                  {activityTotals.events.toLocaleString()}
                </span>{' '}
                イベント / {activityTotals.days} 日 / {activityTotals.devices} 台
              </div>
              {activityTotals.sources.length > 0 && (
                <div>取込元: {activityTotals.sources.join('、')}</div>
              )}
            </div>
          </div>

          {activity.length === 0 ? (
            <p className="text-sm text-gray-400">
              機器イベントがまだ取り込まれていません。
            </p>
          ) : (
            <>
              <ActivityArea points={activityDaily} />
              <div className="mt-6 grid gap-6 lg:grid-cols-2">
                <div>
                  <h3 className="text-sm font-semibold text-gray-800">
                    生活リズム（時間帯別・JST）
                  </h3>
                  <p className="mb-3 text-xs text-gray-500">
                    {rhythmUsesEnergy
                      ? '計測できた日の時間帯別平均電力量。暖色ほど使用が多い時間帯です。'
                      : '電力データがないためイベント数で表示しています。暖色ほど活動が多い時間帯です。'}
                  </p>
                  <RhythmHeatmap cells={rhythm} />
                </div>
                <div>
                  <h3 className="text-sm font-semibold text-gray-800">機器別の寄与</h3>
                  <p className="mb-3 text-xs text-gray-500">イベント数の多い順</p>
                  <DeviceBreakdown slices={devices} />
                </div>
              </div>
            </>
          )}
        </section>

        <section className="mt-6 rounded-lg border border-gray-200 bg-white p-6">
          <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                電気の使用量（SwitchBot プラグ実測）
              </h2>
              <p className="mt-1 text-xs text-gray-500">
                プラグが報告する実電力（W）を1時間ごとに積分した電力量です。
                電源の ON/OFF ではなく「どれだけ使ったか」で暮らしの変化を捉えます。
              </p>
            </div>
            <div className="flex items-center gap-3">
              <span className="text-xs text-gray-500">
                期間合計{' '}
                <span className="font-semibold text-amber-600">
                  {formatWh(energyTotal)}
                </span>
              </span>
              <div className="flex rounded-md border border-gray-200 p-0.5">
                {[7, 30].map((days) => (
                  <button
                    key={days}
                    type="button"
                    onClick={() => setEnergyDays(days)}
                    className={`rounded px-3 py-1 text-xs font-medium transition ${
                      energyDays === days
                        ? 'bg-amber-500 text-white'
                        : 'text-gray-500 hover:text-gray-800'
                    }`}
                  >
                    {days === 7 ? '1週間' : '1カ月'}
                  </button>
                ))}
              </div>
            </div>
          </div>

          {energyDaily.length === 0 ? (
            <p className="text-sm text-gray-400">
              {scope === 'Sample'
                ? 'デモ世帯は実機のプラグを持たないため、電力量の計測はありません。上の切り替えで「本番」を選んでください。'
                : '電力量がまだ取り込まれていません。SwitchBot プラグミニを接続した世帯があると表示されます。'}
            </p>
          ) : (
            <div className="grid gap-6 lg:grid-cols-2">
              <div>
                <h3 className="text-sm font-semibold text-gray-800">
                  日別の使用量（{energyDays === 7 ? '直近1週間' : '直近1カ月'}）
                </h3>
                <p className="mb-3 text-xs text-gray-500">
                  破線は期間平均。斜線のバーは計測できなかった日です。
                </p>
                <EnergyTrend points={energyDaily} />
              </div>
              <div>
                <h3 className="text-sm font-semibold text-gray-800">
                  時間帯別の傾向（JST）
                </h3>
                <p className="mb-3 text-xs text-gray-500">
                  計測できた日の平均。生活リズムと見比べてください。
                </p>
                <EnergyProfile hours={energyHours} />
              </div>
            </div>
          )}
        </section>

        <section className="mt-6 rounded-lg border border-gray-200 bg-white p-6">
          <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                外の暑さ・寒さ（環境省 WBGT／気象庁 AMeDAS）
              </h2>
              <p className="mt-1 text-xs text-gray-500">
                観測地点ごとの公開データを1時間単位にまとめたものです。世帯単位の計測ではないため、
                上の世帯フィルターの影響を受けません。電気の使い方と並べて見ると
                「暑いのに冷房が動いていない」が分かります。
              </p>
            </div>
            {outdoorTotals.latestC !== null && outdoorTotals.latestAt !== null && (
              <div className="text-right">
                <div className="text-xs text-gray-500">
                  最新（{outdoorTotals.latestArea || '観測地点'}）
                </div>
                <div className="text-lg font-semibold text-orange-600">
                  {formatC(outdoorTotals.latestC)}
                </div>
                <div className="text-[11px] text-gray-400">
                  {outdoorTotals.latestAt.toLocaleString('ja-JP')}
                </div>
              </div>
            )}
          </div>

          {outdoorTotals.hours === 0 ? (
            <p className="text-sm text-gray-400">
              気温データがまだ取り込まれていません。屋外データの同期が動くと表示されます。
            </p>
          ) : (
            <div className="grid gap-6 lg:grid-cols-2">
              <div>
                <h3 className="text-sm font-semibold text-gray-800">
                  日別の気温（{energyDays === 7 ? '直近1週間' : '直近1カ月'}）
                </h3>
                <p className="mb-3 text-xs text-gray-500">
                  帯は最低〜最高、白線はその日の平均。斜線の日は観測がありません。
                </p>
                <OutdoorTrend points={outdoorDaily} />
              </div>
              <div>
                <h3 className="text-sm font-semibold text-gray-800">期間のまとめ</h3>
                <p className="mb-3 text-xs text-gray-500">
                  観測地点 {outdoorTotals.points} 地点 ／ {outdoorTotals.hours} 時間分
                </p>
                <dl className="grid grid-cols-2 gap-3 text-sm">
                  <Fact
                    label="最高気温"
                    value={outdoorTotals.maxC === null ? '未計測' : formatC(outdoorTotals.maxC)}
                  />
                  <Fact
                    label="最低気温"
                    value={outdoorTotals.minC === null ? '未計測' : formatC(outdoorTotals.minC)}
                  />
                  <Fact
                    label="暑さ指数（最高）"
                    value={
                      outdoorTotals.maxWbgt === null
                        ? '未計測'
                        : outdoorTotals.maxWbgt.toFixed(1)
                    }
                    note={
                      outdoorTotals.maxWbgt === null
                        ? '環境省の公開は4月下旬〜10月下旬です'
                        : undefined
                    }
                  />
                  <Fact
                    label="警戒以上の時間"
                    value={`${outdoorTotals.cautionHours} 時間`}
                    note="環境省の区分で「警戒」以上"
                  />
                </dl>
              </div>
            </div>
          )}
        </section>

        <section className="grid gap-4 lg:grid-cols-3">
          <Panel title="通知の推移" sub="直近7日／赤は配信失敗">
            <AlertTimeline buckets={timeline} />
          </Panel>
          <Panel title="リスク内訳" sub="通知時点の判定">
            <RiskDonut slices={risks} />
          </Panel>
          <Panel title="LINE 配信結果" sub="取得済みの通知全件">
            <DeliveryGauge
              successRate={delivery.successRate}
              success={delivery.success}
              failed={delivery.failed}
            />
          </Panel>
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-1 text-base font-semibold text-gray-900">世帯別の規模と通知量</h2>
          <p className="mb-4 text-xs text-gray-500">機器数の多い順</p>
          <HouseholdBars bars={bars} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-4 text-base font-semibold text-gray-900">世帯一覧</h2>
          {loading ? (
            <p className="text-sm text-gray-400">読み込み中…</p>
          ) : scoped.households.length === 0 ? (
            <p className="text-sm text-gray-400">
              {households.length === 0
                ? 'スナップショットがまだ届いていません。'
                : 'この区分の世帯はありません。上の切り替えで「すべて」を選ぶと表示されます。'}
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs uppercase tracking-wide text-gray-400">
                    <Th>世帯</Th>
                    <Th>データ</Th>
                    <Th>家族</Th>
                    <Th>機器</Th>
                    <Th>最終イベント</Th>
                    <Th>SwitchBot</Th>
                    <Th>LINE</Th>
                    <Th>通知/失敗</Th>
                    <Th>使用電力量</Th>
                    <Th>リスク</Th>
                  </tr>
                </thead>
                <tbody>
                  {scoped.households.map((row) => (
                    <tr
                      key={row.id}
                      className={`border-t border-gray-100 ${
                        row.needsAttention ? 'bg-red-50/60' : ''
                      }`}
                    >
                      <Td>
                        <span className="font-medium text-gray-900">{row.name}</span>
                        {row.needsAttention && (
                          <span className="ml-2 rounded-full bg-red-100 px-2 py-0.5 text-[11px] text-red-700">
                            要対応
                          </span>
                        )}
                      </Td>
                      <Td>{row.dataSourceMode === 'Sample' ? 'デモ' : '本番'}</Td>
                      <Td>{row.memberCount}</Td>
                      <Td>{row.deviceCount}</Td>
                      <Td>{formatTime(row.lastEventUtc)}</Td>
                      <Td>
                        {switchBotLabel(row.switchBotStatus)}
                        {row.switchBotError && (
                          <div className="text-[11px] text-gray-500">
                            {row.switchBotError}
                          </div>
                        )}
                      </Td>
                      <Td>{row.activeLineRecipients}</Td>
                      <Td>
                        {row.alertsInWindow} /{' '}
                        <span
                          className={
                            row.failedAlertsInWindow !== '0'
                              ? 'text-red-600'
                              : undefined
                          }
                        >
                          {row.failedAlertsInWindow}
                        </span>
                      </Td>
                      <Td>
                        {powerToday(row.powerTodayWh)}
                        <div className={`text-[11px] ${powerTrendClass(row.powerTrend)}`}>
                          {powerTrendLabel(row.powerTrend, row.powerBaselineWh)}
                        </div>
                      </Td>
                      <Td>{riskLabel(row.latestRiskLevel)}</Td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-4 text-base font-semibold text-gray-900">直近の通知</h2>
          {loading ? (
            <p className="text-sm text-gray-400">読み込み中…</p>
          ) : alerts.length === 0 ? (
            <p className="text-sm text-gray-400">通知はありません。</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs uppercase tracking-wide text-gray-400">
                    <Th>日時</Th>
                    <Th>世帯</Th>
                    <Th>リスク</Th>
                    <Th>スコア</Th>
                    <Th>理由</Th>
                    <Th>結果</Th>
                  </tr>
                </thead>
                <tbody>
                  {alerts.map((alert) => (
                    <tr key={alert.id} className="border-t border-gray-100">
                      <Td>{formatDate(alert.sentAt)}</Td>
                      <Td>{alert.householdName}</Td>
                      <Td>{riskLabel(alert.riskLevel)}</Td>
                      <Td>{alert.score}</Td>
                      <Td>{alert.reason}</Td>
                      <Td>
                        {alert.success ? (
                          <span className="rounded-full bg-gray-100 px-2 py-0.5 text-[11px] text-gray-700">
                            成功
                          </span>
                        ) : (
                          <>
                            <span className="rounded-full bg-red-100 px-2 py-0.5 text-[11px] text-red-700">
                              失敗
                            </span>
                            {alert.error && (
                              <div className="text-[11px] text-gray-500">
                                {alert.error}
                              </div>
                            )}
                          </>
                        )}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
          </>
        )}
      </main>
    </div>
  );
}

/**
 * Ask the console a question in Japanese.
 *
 * The answer is written by a model, but the figures it is allowed to use are
 * assembled server-side from the same Fabric tables the charts above draw, and
 * are shown here next to the answer. That pairing is the point: an operator can
 * check every sentence against the numbers it came from, so a fluent-sounding
 * paragraph cannot pass for evidence on its own.
 */
function AskPanel() {
  const [question, setQuestion] = useState('');
  const [asking, setAsking] = useState(false);
  const [result, setResult] = useState<AskResult | null>(null);
  const [asked, setAsked] = useState('');
  const [error, setError] = useState<string | null>(null);
  const available = isAskAvailable();

  const submit = async (text: string) => {
    const trimmed = text.trim();
    if (trimmed === '' || asking) return;
    setAsking(true);
    setError(null);
    setAsked(trimmed);
    try {
      setResult(await askConsole(trimmed));
    } catch (e) {
      setResult(null);
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setAsking(false);
    }
  };

  return (
    <section className="rounded-xl border border-gray-200 bg-white p-5">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <h2 className="text-base font-semibold text-gray-900">この画面に質問する</h2>
          <p className="text-xs text-gray-500">
            日本語で聞くと、Azure の Model Router が答えます。読んでいるのはこの画面と同じ
            Fabric のテーブルで、答えに使ってよい数字はサーバー側で組み立てて渡しています。
            根拠はそのまま下に表示するので、文章と数字を突き合わせて確認できます。
          </p>
        </div>
      </div>

      <form
        className="mt-4 flex flex-col gap-2 sm:flex-row"
        onSubmit={(e) => {
          e.preventDefault();
          void submit(question);
        }}
      >
        <input
          type="text"
          value={question}
          maxLength={400}
          disabled={!available}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="例）今いちばん気にかけるべき世帯はどこ？"
          className="flex-1 rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder:text-gray-400 focus:border-gray-500 focus:outline-none disabled:bg-gray-100"
        />
        <button
          type="submit"
          disabled={!available || asking || question.trim() === ''}
          className="rounded-lg bg-gray-900 px-4 py-2 text-sm font-medium text-white transition hover:bg-gray-700 disabled:cursor-not-allowed disabled:bg-gray-300"
        >
          {asking ? '考えています…' : '質問する'}
        </button>
      </form>

      {!available && (
        <p className="mt-2 text-xs text-amber-700">
          この環境では AI 分析の接続先が設定されていないため、質問を送信できません。
        </p>
      )}

      <div className="mt-2 flex flex-wrap gap-2">
        {SUGGESTED_QUESTIONS.map((q) => (
          <button
            key={q}
            type="button"
            disabled={!available || asking}
            onClick={() => {
              setQuestion(q);
              void submit(q);
            }}
            className="rounded-full border border-gray-200 px-3 py-1 text-xs text-gray-600 transition hover:border-gray-400 hover:text-gray-900 disabled:opacity-50"
          >
            {q}
          </button>
        ))}
      </div>

      {error && (
        <div className="mt-4 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800">
          回答できませんでした: {error}
        </div>
      )}

      {result && (
        <div className="mt-4 rounded-lg border border-gray-200 bg-gray-50 p-4">
          <p className="text-xs text-gray-500">質問: {asked}</p>
          <p className="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-gray-900">
            {result.answer}
          </p>
          <div className="mt-3 flex flex-wrap items-center gap-2 text-[11px] text-gray-500">
            <span className="rounded-full bg-white px-2 py-0.5 ring-1 ring-gray-200">
              Model Router が選んだモデル: {result.model}
            </span>
            <span>{new Date(result.answeredAt).toLocaleString('ja-JP')} 時点</span>
          </div>
          <details className="mt-3">
            <summary className="cursor-pointer text-xs font-medium text-gray-600 hover:text-gray-900">
              この回答が使った数字（{result.evidence.length}行）
            </summary>
            <pre className="mt-2 max-h-72 overflow-auto whitespace-pre-wrap rounded-md bg-white p-3 text-[11px] leading-relaxed text-gray-700 ring-1 ring-gray-200">
              {result.evidence.join('\n')}
            </pre>
          </details>
        </div>
      )}
    </section>
  );
}

function Kpi({
  label,
  value,
  sub,
  alert,
}: {
  label: string;
  value: number;
  sub: string;
  alert?: boolean;
}) {
  return (
    <div
      className={`rounded-xl border bg-white p-4 ${
        alert ? 'border-red-300' : 'border-gray-200'
      }`}
    >
      <div className="text-xs text-gray-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold text-gray-900">{value}</div>
      <div className="text-xs text-gray-400">{sub}</div>
    </div>
  );
}

/**
 * One labelled figure. `value` is a string on purpose: the callers pass 未計測
 * for anything that was never observed, and a number type would tempt a 0.
 */
function Fact({
  label,
  value,
  note,
}: {
  label: string;
  value: string;
  note?: string;
}) {
  return (
    <div className="rounded-lg border border-gray-100 bg-gray-50 p-3">
      <dt className="text-[11px] text-gray-500">{label}</dt>
      <dd className="mt-0.5 font-semibold text-gray-900">{value}</dd>
      {note && <p className="mt-1 text-[10px] leading-tight text-gray-400">{note}</p>}
    </div>
  );
}

function Panel({
  title,
  sub,
  children,
}: {
  title: string;
  sub: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-gray-200 bg-white p-5">
      <h2 className="text-base font-semibold text-gray-900">{title}</h2>
      <p className="mb-4 text-xs text-gray-500">{sub}</p>
      {children}
    </div>
  );
}

function Th({ children }: { children: React.ReactNode }) {
  return <th className="whitespace-nowrap px-3 py-2 font-semibold">{children}</th>;
}

function Td({ children }: { children: React.ReactNode }) {
  return <td className="whitespace-nowrap px-3 py-2 align-top">{children}</td>;
}

function formatTime(iso: string): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '—' : formatDate(date);
}

function formatTime2(date: Date | null): string {
  return date ? formatDate(date) : '—';
}

function formatDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(date.getMonth() + 1)}/${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * Watt-hours consumed so far today. Shown in kWh once it passes a thousand so a
 * busy home does not turn into an unreadable five-digit number.
 */
function powerToday(value: string | undefined): string {
  const wh = Number(value ?? '');
  if (!value || Number.isNaN(wh)) return '—';
  return wh >= 1000 ? `${(wh / 1000).toFixed(2)} kWh` : `${wh.toFixed(wh < 10 ? 1 : 0)} Wh`;
}

/**
 * The comparison is what an operator acts on: an unusually quiet home matters
 * far more than the absolute figure, which varies wildly between households.
 */
function powerTrendLabel(trend: string | undefined, baseline: string | undefined): string {
  const usual = Number(baseline ?? '');
  const suffix = baseline && !Number.isNaN(usual) ? `（いつも約${usual.toFixed(0)}Wh）` : '';

  switch (trend) {
    case 'Higher':
      return `いつもより多め${suffix}`;
    case 'Lower':
      return `いつもより少なめ${suffix}`;
    case 'Typical':
      return `ほぼいつもどおり${suffix}`;
    default:
      return '比較データ収集中';
  }
}

function powerTrendClass(trend: string | undefined): string {
  switch (trend) {
    case 'Higher':
      return 'text-amber-600';
    case 'Lower':
      return 'text-red-600';
    case 'Typical':
      return 'text-emerald-600';
    default:
      return 'text-gray-400';
  }
}

function switchBotLabel(status: string): string {
  switch (status) {
    case 'Connected':
      return '接続済み';
    case 'Error':
      return 'エラー';
    case 'NotConfigured':
    case '':
      return '未設定';
    default:
      return status;
  }
}

function riskLabel(level: string): string {
  switch (level) {
    case 'Low':
      return '低';
    case 'Medium':
      return '中';
    case 'High':
      return '高';
    default:
      return '—';
  }
}
