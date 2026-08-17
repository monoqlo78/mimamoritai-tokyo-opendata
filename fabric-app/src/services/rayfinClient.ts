import { RayfinClient } from '@microsoft/rayfin-client';

import type { MimamoriAdminSchema } from '../../rayfin/data/schema';

export interface RayfinClientConfig {
  baseUrl: string;
  publishableKey: string;
  /** True when the API URL points at localhost. Exposed via {@link isLocalBackend}. */
  localDev: boolean;
}

type AdminClient = RayfinClient<MimamoriAdminSchema>;

let client: AdminClient | null = null;
let localDev = false;

export function initRayfinClient(config: RayfinClientConfig): AdminClient {
  if (client) {
    throw new Error('Rayfin client is already initialized.');
  }
  client = new RayfinClient<MimamoriAdminSchema>({
    baseUrl: config.baseUrl,
    publishableKey: config.publishableKey,
    authStorage: true,
  });
  localDev = config.localDev;
  return client;
}

export function getRayfinClient(): AdminClient {
  if (!client) {
    throw new Error(
      'Rayfin client not initialized. Call bootstrapAuth() first.'
    );
  }
  return client;
}

/** True when the app was bootstrapped against a localhost backend. */
export function isLocalBackend(): boolean {
  return localDev;
}
