import { useEffect, useMemo, useRef, useState } from 'react';

import type { PipelineStats } from '@/services/analytics';

/**
 * Animated architecture diagram.
 *
 * Three stacked layers share one pixel coordinate space:
 *  - SVG   : the static edges plus a dashed "flow" stroke (works without WebGL)
 *  - WebGL2: additive glow particles travelling along the same bezier curves
 *  - HTML  : the node cards, drawn on top so particles appear to enter them
 *
 * Every throughput number comes from the rows the tables render, so the picture
 * cannot drift from the data.
 */

type Accent = 'sensor' | 'app' | 'store' | 'risk' | 'deliver' | 'fabric' | 'ai';

interface FlowNode {
  id: string;
  /** Normalised position inside the panel (0..1, y grows downwards). */
  x: number;
  y: number;
  title: string;
  subtitle: string;
  metric: (stats: PipelineStats) => string;
  accent: Accent;
}

interface FlowEdge {
  from: string;
  to: string;
  label: string;
  /** Relative particle density; scaled by the live counts. */
  weight: (stats: PipelineStats) => number;
  color: [number, number, number];
  /** Fraction of particles rendered as failures, 0..1. */
  failureRate?: (stats: PipelineStats) => number;
}

const ACCENT_CLASS: Record<Accent, string> = {
  sensor: 'border-cyan-400/40 shadow-cyan-500/20',
  app: 'border-sky-400/40 shadow-sky-500/20',
  store: 'border-indigo-400/40 shadow-indigo-500/20',
  risk: 'border-violet-400/40 shadow-violet-500/20',
  deliver: 'border-amber-400/40 shadow-amber-500/20',
  fabric: 'border-emerald-400/40 shadow-emerald-500/20',
  ai: 'border-rose-400/40 shadow-rose-500/20',
};

const ACCENT_DOT: Record<Accent, string> = {
  sensor: 'bg-cyan-400',
  app: 'bg-sky-400',
  store: 'bg-indigo-400',
  risk: 'bg-violet-400',
  deliver: 'bg-amber-400',
  fabric: 'bg-emerald-400',
  ai: 'bg-rose-400',
};

const NODES: FlowNode[] = [
  {
    id: 'sensor',
    x: 0.085,
    y: 0.14,
    title: 'SwitchBot / センサー',
    subtitle: '人感・開閉・温湿度',
    metric: (s) =>
      s.activityEvents > 0 ? `${s.devices} 台 / ${s.activityEvents} 件` : `${s.devices} 台`,
    accent: 'sensor',
  },
  {
    id: 'app',
    x: 0.3,
    y: 0.14,
    title: '見守り隊 Web',
    subtitle: '.NET 10 Blazor / App Service',
    metric: (s) => `${s.households} 世帯`,
    accent: 'app',
  },
  {
    id: 'sql',
    x: 0.515,
    y: 0.14,
    title: 'Azure SQL',
    subtitle: 'スキーマ mimamori',
    metric: (s) => `本番 ${s.productionHouseholds} 世帯`,
    accent: 'store',
  },
  {
    id: 'risk',
    x: 0.725,
    y: 0.14,
    title: 'リスク判定',
    subtitle: '無反応・生活リズム逸脱',
    metric: (s) => `${s.alerts} 件検知`,
    accent: 'risk',
  },
  {
    id: 'line',
    x: 0.925,
    y: 0.14,
    title: 'LINE 配信',
    subtitle: '家族への通知',
    metric: (s) => `宛先 ${s.lineRecipients}`,
    accent: 'deliver',
  },
  {
    id: 'router',
    // Bottom row with the streaming branch, because both are things the app
    // reaches out to on its own -- neither is a step on the way to this console.
    x: 0.085,
    y: 0.78,
    title: 'Model Router',
    // Short enough to stay on one line inside the card; the longer wording
    // ("自動モデル選択") wrapped mid-word at this card width.
    subtitle: 'Azure AI Foundry / 自動選択',
    // Counts only the calls that actually reached the router, so the offline
    // stub used before a deployment exists can never inflate this.
    //
    // The model bars below now include a trailing bar for calls that never
    // resolved to a model, so they add up to the raw call count and this card
    // can lead with it again. The model count is a separate fact (how many
    // distinct models answered), not a claim that they cover every call.
    metric: (s) =>
      s.aiCalls === 0 ? '呼び出しなし' : `${s.aiCalls} 回 / ${s.aiModels} モデル`,
    accent: 'ai',
  },
  {
    id: 'eventstream',
    // Directly under the app, which is where this hop starts: SwitchBot's consumer
    // devices have no direct Azure path, so the poller is the only possible
    // producer. On its own row because this branch never reaches the console --
    // sharing a row with the sync chain read as one pipeline feeding the console.
    x: 0.3,
    y: 0.78,
    title: 'Eventstream',
    subtitle: 'カスタムエンドポイント',
    // Event Hub is the primary sink and direct Eventhouse ingestion the fallback
    // (FallbackEventStreamPublisher), so name the cadence, not a destination
    // count -- the destination is now its own card, and the cadence is the whole
    // reason this branch exists next to the 15-minute console sync.
    //
    // "リアルタイム" is about the cadence of the hop, not a claim that the plug
    // reaches Fabric unaided: SwitchBot is a vendor cloud with no AMQP or SAS of
    // its own, so naming the endpoint type says plainly that something on our side
    // has to open the connection.
    metric: () => 'リアルタイム',
    accent: 'fabric',
  },
  {
    id: 'eventhouse',
    x: 0.505,
    y: 0.78,
    title: 'Eventhouse',
    subtitle: 'KQL / MimamoriEventhouse',
    // DeviceEvents + SwitchBotPlugReadings -- the same readings the console shows,
    // arriving by the other route. Naming the reader rather than counting rows: a row
    // count would have to come from Eventhouse itself, which this console never
    // queries, and the question this card kept raising was never "how much is in
    // there" but "who reads it, if not this screen".
    metric: () => 'AI が読む / 2 テーブル',
    accent: 'fabric',
  },
  {
    id: 'dataagent',
    x: 0.71,
    y: 0.78,
    title: 'Fabric Data Agent',
    subtitle: 'MCP / 自然言語で照会',
    // Drawn to answer the obvious question the Eventhouse card raises: if the
    // console cannot read it, who does. This is who -- the app asks the Data
    // Agent, which queries the Eventhouse. Rose like Model Router because it is
    // the same branch: things the app consults to answer a question.
    metric: () => 'AI の質問応答',
    accent: 'ai',
  },
  {
    id: 'sync',
    x: 0.515,
    y: 0.47,
    title: 'コンソール同期',
    // The C# background service in the web app, not the ps1 used to bootstrap
    // the Fabric SQL side once.
    subtitle: 'FabricConsoleSync / 15分ごと',
    metric: (s) => (s.origin === 'fabric' ? '読み取り専用' : '停止中'),
    accent: 'fabric',
  },
  {
    id: 'fabric',
    x: 0.72,
    y: 0.47,
    title: 'Fabric SQL Database',
    subtitle: 'Rayfin プロビジョニング',
    metric: (s) =>
      s.origin === 'fabric' ? `${s.households + s.alerts + s.fabricRows} 行` : '接続不可',
    accent: 'fabric',
  },
  {
    id: 'console',
    x: 0.925,
    y: 0.47,
    title: '運用コンソール',
    subtitle: 'Rayfin + Fabric SSO',
    metric: (s) => (s.origin === 'fabric' ? 'ライブ' : 'スナップショット'),
    accent: 'fabric',
  },
];

const EDGES: FlowEdge[] = [
  {
    from: 'sensor',
    to: 'app',
    label: 'デバイスイベント',
    // Busier homes push visibly more particles down the ingest path.
    weight: (s) => 6 + s.devices * 2 + Math.min(18, s.activityEvents / 12),
    color: [0.31, 0.83, 0.98],
  },
  {
    from: 'app',
    to: 'sql',
    label: '永続化',
    weight: (s) => 5 + s.devices + Math.min(14, s.activityEvents / 16),
    color: [0.4, 0.68, 0.99],
  },
  {
    from: 'sql',
    to: 'risk',
    label: '直近7日を評価',
    weight: (s) => 4 + s.households * 2,
    color: [0.65, 0.55, 0.98],
  },
  {
    from: 'risk',
    to: 'line',
    label: 'アラート配信',
    weight: (s) => 3 + s.alerts * 2,
    color: [0.98, 0.75, 0.29],
    failureRate: (s) => (s.alerts === 0 ? 0 : s.failedAlerts / s.alerts),
  },
  {
    from: 'app',
    to: 'router',
    // One-way on purpose: a return edge between the same two nodes would take an
    // identical bend and land exactly on top of this one.
    label: '意図解析・要約を依頼',
    weight: (s) => 3 + Math.min(14, s.aiCalls / 4),
    color: [0.98, 0.44, 0.58],
  },
  {
    from: 'app',
    to: 'eventstream',
    // The app is the producer because it has to be: Eventstream's custom endpoint
    // speaks Event Hub/AMQP with a SAS credential, and SwitchBot's cloud can only
    // poll-answer over REST or POST plain JSON at a URL. Nothing in that chain can
    // hold a SAS token, so a gateway is not a shortcut here, it is the only join
    // available between a closed vendor cloud and Fabric.
    //
    // What it forwards, though, is the persisted backlog and not the packet in
    // flight -- the publisher reads DeviceEvents/PlugMiniReadings rows whose
    // PublishedToStreamAtUtc is still null. Labelling it "イベント転送" implied a
    // pass-through, which is why the picture read as if the web were the origin of
    // the data rather than the sender of it.
    label: '未送信行を転送',
    weight: (s) => 4 + s.devices + Math.min(12, s.activityEvents / 20),
    color: [0.2, 0.83, 0.6],
  },
  {
    from: 'eventstream',
    to: 'eventhouse',
    // Where the real-time branch actually lands. Drawn because without it the
    // stream read as a dead end, and a dead end invites the fair question of
    // why it exists next to the console sync at all.
    // Short on purpose: this label sits in the gutter between two adjacent cards,
    // which are drawn over the SVG, so anything longer disappears behind them.
    label: '取り込み',
    weight: (s) => 4 + Math.min(12, s.activityEvents / 20),
    color: [0.2, 0.83, 0.6],
  },
  {
    from: 'eventhouse',
    to: 'dataagent',
    // The Eventhouse is read, just not by this console. Leaving that unsaid is
    // what made the streaming branch look like a redundant copy of the sync --
    // it carries the same readings, it is simply read by someone else.
    label: 'KQL 照会',
    weight: (s) => 3 + Math.min(10, s.aiCalls / 6),
    color: [0.98, 0.44, 0.58],
  },
  {
    from: 'sql',
    to: 'sync',
    label: '集計スナップショット',
    weight: (s) => 3 + s.households,
    color: [0.2, 0.83, 0.6],
  },
  {
    from: 'sync',
    to: 'fabric',
    label: 'MERGE 取り込み',
    weight: (s) => 3 + s.households,
    color: [0.2, 0.83, 0.6],
  },
  {
    from: 'fabric',
    to: 'console',
    label: 'GraphQL 読み取り',
    weight: () => 8,
    color: [0.36, 0.9, 0.72],
  },
];

interface Particle {
  edge: number;
  t: number;
  speed: number;
  size: number;
  failed: boolean;
}

interface Pulse {
  node: string;
  age: number;
  color: [number, number, number];
}

interface Star {
  x: number;
  y: number;
  size: number;
  drift: number;
  phase: number;
}

/** Trail samples drawn behind each particle. Higher = longer comet tail. */
const TRAIL = 7;
/** Floats per vertex: vec2 pos, float size, vec4 color, float kind. */
const STRIDE = 8;
const PULSE_LIFE = 1.1;

const VERT = `#version 300 es
in vec2 a_pos;
in float a_size;
in vec4 a_color;
in float a_kind;
out vec4 v_color;
flat out float v_kind;
void main() {
  gl_Position = vec4(a_pos, 0.0, 1.0);
  gl_PointSize = a_size;
  v_color = a_color;
  v_kind = a_kind;
}`;

const FRAG = `#version 300 es
precision mediump float;
in vec4 v_color;
flat in float v_kind;
out vec4 outColor;
void main() {
  float d = length(gl_PointCoord - vec2(0.5));
  float a;
  if (v_kind > 0.5) {
    // Expanding ring: a bright annulus just inside the point sprite.
    a = smoothstep(0.50, 0.42, d) * smoothstep(0.30, 0.40, d);
  } else {
    float core = smoothstep(0.5, 0.0, d);
    float glow = smoothstep(0.5, 0.15, d);
    a = core * 0.55 + glow * 0.45;
  }
  outColor = vec4(v_color.rgb * a, a * v_color.a);
}`;

/** Cubic bezier control points for an edge, in normalised space. */
function controlPoints(a: FlowNode, b: FlowNode) {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  // Mostly-vertical hops bend sideways so they do not overlap the node cards.
  const bend = Math.abs(dx) < 0.02 ? 0.09 : 0;
  return {
    c1x: a.x + dx * 0.45 + bend,
    c1y: a.y + dy * 0.15,
    c2x: b.x - dx * 0.45 + bend,
    c2y: b.y - dy * 0.15,
  };
}

function bezier(t: number, p0: number, p1: number, p2: number, p3: number): number {
  const u = 1 - t;
  return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
}

export function DataFlowCanvas({ stats }: { stats: PipelineStats }) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const statsRef = useRef(stats);
  statsRef.current = stats;

  const nodeById = useMemo(() => {
    const map = new Map<string, FlowNode>();
    for (const node of NODES) map.set(node.id, node);
    return map;
  }, []);

  const paths = useMemo(() => {
    if (size.width === 0) return [];
    return EDGES.map((edge) => {
      const a = nodeById.get(edge.from)!;
      const b = nodeById.get(edge.to)!;
      const { c1x, c1y, c2x, c2y } = controlPoints(a, b);
      const px = (v: number) => v * size.width;
      const py = (v: number) => v * size.height;
      return {
        edge,
        d:
          `M ${px(a.x)} ${py(a.y)} ` +
          `C ${px(c1x)} ${py(c1y)}, ${px(c2x)} ${py(c2y)}, ${px(b.x)} ${py(b.y)}`,
        labelX: px(bezier(0.5, a.x, c1x, c2x, b.x)),
        // An edge between two nodes on the same row has its midpoint at the row's
        // own height, so a 10px lift left the label underneath the cards, which
        // are drawn over this SVG. Those are the labels that explain the diagram,
        // so lift same-row labels clear of the card instead. Sloped edges pass
        // between rows and only need the small nudge off the curve.
        labelY: py(bezier(0.5, a.y, c1y, c2y, b.y)) - (Math.abs(b.y - a.y) < 0.02 ? 40 : 10),
      };
    });
  }, [nodeById, size]);

  useEffect(() => {
    const element = wrapRef.current;
    if (!element) return;

    const observer = new ResizeObserver((entries) => {
      const rect = entries[0].contentRect;
      setSize({ width: rect.width, height: rect.height });
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || size.width === 0) return;

    const gl = canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true });
    // No WebGL2: the SVG layer below still shows the dashed flow animation.
    if (!gl) return;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.floor(size.width * dpr);
    canvas.height = Math.floor(size.height * dpr);

    const program = buildProgram(gl, VERT, FRAG);
    if (!program) return;

    const particles = seedParticles(statsRef.current);
    const stars = seedStars();
    const pulses: Pulse[] = [];
    const maxPulses = 24;
    const maxVertices =
      particles.length * TRAIL + stars.length + maxPulses;
    const data = new Float32Array(maxVertices * STRIDE);
    const buffer = gl.createBuffer();

    const aPos = gl.getAttribLocation(program, 'a_pos');
    const aSize = gl.getAttribLocation(program, 'a_size');
    const aColor = gl.getAttribLocation(program, 'a_color');
    const aKind = gl.getAttribLocation(program, 'a_kind');

    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, data.byteLength, gl.DYNAMIC_DRAW);
    const bytes = STRIDE * 4;
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, bytes, 0);
    gl.enableVertexAttribArray(aSize);
    gl.vertexAttribPointer(aSize, 1, gl.FLOAT, false, bytes, 8);
    gl.enableVertexAttribArray(aColor);
    gl.vertexAttribPointer(aColor, 4, gl.FLOAT, false, bytes, 12);
    gl.enableVertexAttribArray(aKind);
    gl.vertexAttribPointer(aKind, 1, gl.FLOAT, false, bytes, 28);

    gl.useProgram(program);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE);

    // Precompute the bezier control points once; nodes never move.
    const geometry = EDGES.map((edge) => {
      const a = nodeById.get(edge.from)!;
      const b = nodeById.get(edge.to)!;
      return { a, b, ...controlPoints(a, b), edge };
    });

    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    let raf = 0;
    let previous = performance.now();
    let elapsed = 0;

    const frame = (now: number) => {
      const dt = Math.min((now - previous) / 1000, 0.05);
      previous = now;
      elapsed += dt;

      let v = 0;
      const push = (
        nx: number,
        ny: number,
        size: number,
        r: number,
        g: number,
        b: number,
        alpha: number,
        kind: number
      ) => {
        if (v >= maxVertices) return;
        const offset = v * STRIDE;
        data[offset] = nx * 2 - 1;
        data[offset + 1] = 1 - ny * 2;
        data[offset + 2] = size;
        data[offset + 3] = r;
        data[offset + 4] = g;
        data[offset + 5] = b;
        data[offset + 6] = alpha;
        data[offset + 7] = kind;
        v += 1;
      };

      // Background motes: slow vertical drift with a gentle horizontal sway,
      // so the panel reads as "live" even where no edge passes.
      for (const star of stars) {
        const y = (star.y + (reduceMotion ? 0 : elapsed * star.drift)) % 1;
        const x = star.x + Math.sin(elapsed * 0.3 + star.phase) * 0.006;
        const twinkle = 0.35 + 0.3 * Math.sin(elapsed * 1.7 + star.phase);
        push(x, y, star.size * dpr, 0.58, 0.72, 0.95, twinkle * 0.5, 0);
      }

      for (const particle of particles) {
        const geo = geometry[particle.edge];

        if (!reduceMotion) {
          particle.t += particle.speed * dt;
          if (particle.t > 1) {
            particle.t -= 1;
            // Arrival at the downstream node: ripple out from the card.
            if (pulses.length < maxPulses) {
              pulses.push({
                node: geo.edge.to,
                age: 0,
                color: particle.failed ? [0.98, 0.35, 0.35] : geo.edge.color,
              });
            }
          }
        }

        const [r, g, b] = particle.failed ? [0.98, 0.35, 0.35] : geo.edge.color;

        for (let k = 0; k < TRAIL; k += 1) {
          // Sample slightly behind the head; the tail thins and dims.
          const t = particle.t - k * 0.016;
          if (t < 0 || t > 1) continue;

          const nx = bezier(t, geo.a.x, geo.c1x, geo.c2x, geo.b.x);
          const ny = bezier(t, geo.a.y, geo.c1y, geo.c2y, geo.b.y);

          // Fade near the cards so particles look absorbed rather than clipped.
          const edgeFade = Math.min(1, Math.min(t, 1 - t) / 0.12);
          const decay = 1 - k / TRAIL;
          const head = k === 0 ? 1 : 0.62;

          push(
            nx,
            ny,
            particle.size * dpr * (particle.failed ? 1.35 : 1) * (0.35 + decay * 0.65),
            r,
            g,
            b,
            (0.25 + edgeFade * 0.75) * decay * decay * head,
            0
          );
        }
      }

      for (let i = pulses.length - 1; i >= 0; i -= 1) {
        const pulse = pulses[i];
        pulse.age += dt;
        if (pulse.age >= PULSE_LIFE) {
          pulses.splice(i, 1);
          continue;
        }
        const node = nodeById.get(pulse.node);
        if (!node) continue;
        const progress = pulse.age / PULSE_LIFE;
        // Ease-out so the ring snaps outward then relaxes.
        const eased = 1 - (1 - progress) * (1 - progress);
        push(
          node.x,
          node.y,
          (26 + eased * 92) * dpr,
          pulse.color[0],
          pulse.color[1],
          pulse.color[2],
          (1 - progress) * 0.5,
          1
        );
      }

      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, data.subarray(0, v * STRIDE));
      gl.drawArrays(gl.POINTS, 0, v);

      raf = requestAnimationFrame(frame);
    };

    raf = requestAnimationFrame(frame);

    return () => {
      cancelAnimationFrame(raf);
      gl.deleteBuffer(buffer);
      gl.deleteProgram(program);
    };
  }, [nodeById, size, stats]);

  return (
    <div className="overflow-x-auto">
      <div
        ref={wrapRef}
        className="relative h-[470px] min-w-[900px] overflow-hidden rounded-xl bg-gradient-to-br from-slate-950 via-slate-900 to-slate-950"
      >
        <div className="pointer-events-none absolute inset-0 opacity-40 [background-image:radial-gradient(circle_at_1px_1px,rgba(148,163,184,0.25)_1px,transparent_0)] [background-size:26px_26px]" />

        <svg
          className="absolute inset-0 h-full w-full"
          viewBox={`0 0 ${Math.max(size.width, 1)} ${Math.max(size.height, 1)}`}
        >
          {paths.map(({ edge, d, labelX, labelY }) => (
            <g key={`${edge.from}-${edge.to}`}>
              <path d={d} fill="none" stroke="rgba(148,163,184,0.28)" strokeWidth={1.5} />
              <path
                d={d}
                fill="none"
                stroke={`rgba(${edge.color.map((c) => Math.round(c * 255)).join(',')},0.55)`}
                strokeWidth={1.5}
                strokeDasharray="5 14"
                className="dataflow-dash"
              />
              <text
                x={labelX}
                y={labelY}
                textAnchor="middle"
                className="fill-slate-400 text-[11px]"
              >
                {edge.label}
              </text>
            </g>
          ))}
        </svg>

        <canvas
          ref={canvasRef}
          className="absolute inset-0 h-full w-full"
          style={{ width: size.width, height: size.height }}
        />

        {NODES.map((node) => (
          <div
            key={node.id}
            // 132px, not the 148px this used to be: the lower row now carries six
            // cards across the same span, and at the 900px floor that leaves only
            // 151px per column. Narrower keeps a real gutter instead of letting
            // neighbours touch on a small laptop.
            className={`absolute w-[132px] -translate-x-1/2 -translate-y-1/2 rounded-lg border bg-slate-900/85 px-3 py-2 shadow-lg backdrop-blur-sm ${ACCENT_CLASS[node.accent]}`}
            style={{ left: `${node.x * 100}%`, top: `${node.y * 100}%` }}
          >
            <div className="flex items-center gap-1.5">
              <span className={`h-1.5 w-1.5 rounded-full ${ACCENT_DOT[node.accent]}`} />
              <span className="text-[12px] font-semibold text-slate-100">{node.title}</span>
            </div>
            <div className="mt-0.5 text-[10px] leading-tight text-slate-400">
              {node.subtitle}
            </div>
            <div className="mt-1 text-[11px] font-medium text-slate-200">
              {node.metric(stats)}
            </div>
          </div>
        ))}

        <div className="absolute bottom-3 left-4 right-4 flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-slate-400">
            <span className="flex items-center gap-1.5">
              <span className="h-1.5 w-1.5 rounded-full bg-sky-400" />
              リアルタイム見守り経路
            </span>
            <span className="flex items-center gap-1.5">
              <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
              分析経路（Fabric）
            </span>
            {stats.aiCalls > 0 && (
              <span className="flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 rounded-full bg-rose-400" />
                AI 経路（Model Router / Data Agent）
              </span>
            )}
            {stats.failedAlerts > 0 && (
              <span className="flex items-center gap-1.5 text-red-300">
                <span className="h-1.5 w-1.5 rounded-full bg-red-400" />
                配信失敗 {stats.failedAlerts} 件
              </span>
            )}
          </div>
          {/*
            The Fabric branch forks into two chains that never rejoin, and drawn without
            a word of explanation that reads as one of them being redundant -- the
            question it kept prompting was why the Eventstream exists at all, or why the
            Eventhouse is not wired to this screen. Neither is drawn wrong: they carry
            the same measurements and are read by different consumers. Saying so here,
            under the picture, is the honest fix; drawing an Eventhouse-to-console edge
            would be the dishonest one, because this console runs no KQL.
          */}
          <div className="text-[10px] leading-tight text-slate-500">
            Eventstream と取り込みバッチは同じ計測値を運びます。Eventhouse は AI の質問応答が、
            Fabric SQL Database はこの画面が読みます。
          </div>
        </div>
      </div>
    </div>
  );
}

function seedParticles(stats: PipelineStats): Particle[] {
  const particles: Particle[] = [];

  EDGES.forEach((edge, index) => {
    const count = Math.max(4, Math.min(34, Math.round(edge.weight(stats))));
    const failureRate = edge.failureRate?.(stats) ?? 0;

    for (let i = 0; i < count; i += 1) {
      particles.push({
        edge: index,
        t: i / count + Math.random() * 0.02,
        speed: 0.11 + Math.random() * 0.07,
        size: 3.2 + Math.random() * 3.4,
        failed: failureRate > 0 && i / count < failureRate,
      });
    }
  });

  return particles;
}

/** Slow-drifting background motes, so idle areas of the panel still breathe. */
function seedStars(count = 90): Star[] {
  const stars: Star[] = [];
  for (let i = 0; i < count; i += 1) {
    stars.push({
      x: Math.random(),
      y: Math.random(),
      size: 1.2 + Math.random() * 2.2,
      drift: 0.008 + Math.random() * 0.022,
      phase: Math.random() * Math.PI * 2,
    });
  }
  return stars;
}

function buildProgram(
  gl: WebGL2RenderingContext,
  vertexSource: string,
  fragmentSource: string
): WebGLProgram | null {
  const compile = (type: number, source: string) => {
    const shader = gl.createShader(type);
    if (!shader) return null;
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      gl.deleteShader(shader);
      return null;
    }
    return shader;
  };

  const vertex = compile(gl.VERTEX_SHADER, vertexSource);
  const fragment = compile(gl.FRAGMENT_SHADER, fragmentSource);
  if (!vertex || !fragment) return null;

  const program = gl.createProgram();
  if (!program) return null;
  gl.attachShader(program, vertex);
  gl.attachShader(program, fragment);
  gl.linkProgram(program);
  gl.deleteShader(vertex);
  gl.deleteShader(fragment);

  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    gl.deleteProgram(program);
    return null;
  }
  return program;
}
