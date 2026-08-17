import { entity, role, text, date, uuid } from '@microsoft/rayfin-core';

/**
 * Rollup of AI router traffic, mirrored from mimamori.AiRequestLogs.
 *
 * 見守り隊 talks to every language model through Azure AI Foundry's Model Router
 * deployment, so this table is the evidence that the routing actually happens:
 * one row per (purpose, router, resolvedModel) with counts and a mean latency.
 *
 * Counts only -- never a prompt, never a completion, never a household id.
 * The source table stores no prompt text either, but aggregating here keeps the
 * console's grain identical to what the charts draw.
 *
 * Reading the rows:
 *  - `router` is the client that served the call. "Azure Model Router" means the
 *    request went to the router deployment, which chose the model per request.
 *  - "MockAiRouter" is the offline stub used before a deployment is configured,
 *    and is the one value that did NOT go through the router.
 */
@entity()
@role('authenticated', 'read')
export class AiRouterCall {
  @uuid() id!: string;

  /** Call site: "intent" | "intent-repair" | "summary" | "summary-fast" | "conversation" | "alert-message" | "console-question". */
  @text({ max: 64 }) purpose!: string;

  /** "Azure Model Router" (the router chose the model) or "MockAiRouter" (offline stub). */
  @text({ max: 64 }) router!: string;

  /** The model the router actually served the request with, e.g. "gpt-4.1-mini-2025-04-14". */
  @text({ max: 128 }) resolvedModel!: string;

  /** Requests in this group. */
  @text({ max: 10 }) callCount!: string;

  /** Subset of `callCount` that returned a usable completion. */
  @text({ max: 10 }) successCount!: string;

  /** Mean end-to-end latency in milliseconds -- the number behind the model-pinning decision. */
  @text({ max: 12 }) avgDurationMs!: string;

  /** Most recent call in this group. */
  @date() lastCalledAt!: Date;
}
