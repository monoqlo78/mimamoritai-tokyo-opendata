export interface AskResult {
  answer: string;
  model: string;
  evidence: string[];
  answeredAt: string;
}

interface AskResponse extends AskResult {
  success: boolean;
  error?: string | null;
}

/**
 * Questions worth one click. These are the three things an operator actually
 * opens this console to find out, phrased as they would say them out loud.
 */
export const SUGGESTED_QUESTIONS = [
  '今いちばん気にかけるべき世帯はどこ？',
  '暑さと電力の使い方に、気になる組み合わせはある？',
  '通知が届かなかった世帯はある？',
] as const;

/**
 * Where the question is answered. Supplied at build time because this console is
 * served as a static bundle and has nowhere to keep a model key of its own: the
 * request goes to the watch app's own backend, which holds the credential and
 * assembles the figures the model is allowed to use from the same data these charts
 * are drawn from. Left blank in local development, where there is nothing to call.
 */
const ASK_URL = (import.meta.env.VITE_CONSOLE_ASK_URL as string | undefined) ?? '';

export function isAskAvailable(): boolean {
  return ASK_URL !== '';
}

/** Ask the console a question in Japanese. */
export async function askConsole(question: string): Promise<AskResult> {
  if (!isAskAvailable()) {
    // Refusing is the honest answer. Returning a canned paragraph here would put
    // sentences about real households on screen that no data produced.
    throw new Error('この環境では AI 分析を利用できません。');
  }

  const res = await fetch(ASK_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question }),
  });

  if (res.status === 429) {
    throw new Error('短時間に質問が集中しています。少し待ってからもう一度お試しください。');
  }

  if (!res.ok) {
    throw new Error(`分析サービスに接続できませんでした (HTTP ${res.status})`);
  }

  const body = (await res.json()) as AskResponse;
  if (!body.success) {
    throw new Error(body.error ?? '回答を生成できませんでした。');
  }

  return {
    answer: body.answer,
    model: body.model,
    evidence: body.evidence ?? [],
    answeredAt: body.answeredAt,
  };
}
