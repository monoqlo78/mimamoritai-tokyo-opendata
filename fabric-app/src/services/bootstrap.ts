import { CaptureAuthService } from './CaptureAuthService';
import type { IAuthService } from './IAuthService';
import { MockAuthService } from './MockAuthService';
import { RayfinAuthService } from './RayfinAuthService';
import { initRayfinClient } from './rayfinClient';

function isLocalBackendUrl(url: string): boolean {
  try {
    const { hostname } = new URL(url);
    return hostname === 'localhost' || hostname === '127.0.0.1';
  } catch {
    return false;
  }
}

/**
 * True only while recording the demo video on a dev server.
 *
 * `import.meta.env.DEV` is substituted with the literal `false` by Vite in
 * every production build, so this collapses to `false` and the capture path
 * is dropped from the bundle. It is not a runtime switch that can be flipped
 * on the deployed console.
 */
export function isDemoCapture(): boolean {
  return import.meta.env.DEV && import.meta.env.VITE_DEMO_CAPTURE === '1';
}

/**
 * Read VITE_* env vars, initialize the Rayfin client, and return the right
 * auth service for the target backend.
 *
 * - Localhost API URL → {@link MockAuthService}
 * - Anything else     → {@link RayfinAuthService} (requires VITE_FABRIC_* vars)
 *
 * Ahead of both: the demo-capture escape hatch. `import.meta.env.DEV` is
 * replaced with `false` when Vite builds for production, so the branch below
 * is removed at build time and cannot be reached by anything we publish.
 */
export function bootstrapAuth(): IAuthService {
  if (isDemoCapture()) {
    // Deliberately not a localhost dev-server URL: `localDev` selects the
    // synthetic local-dev fixtures, and the point of the capture is to show the
    // snapshot extracted from the production Fabric database instead. Port 1 on
    // the loopback refuses immediately, so every read fails in milliseconds and
    // falls back to the snapshot. (An unroutable public address also works, but
    // costs ~21s of TCP retries per read and stalls the recording.)
    initRayfinClient({
      baseUrl: 'http://127.0.0.1:1/',
      publishableKey: 'demo-capture',
      localDev: false,
    });
    return new CaptureAuthService();
  }
  const apiUrl = import.meta.env.VITE_RAYFIN_API_URL || 'http://localhost:5168';
  const localDev = isLocalBackendUrl(apiUrl);
  const publishableKey = import.meta.env.VITE_RAYFIN_PUBLISHABLE_KEY;

  if (!publishableKey && !localDev) {
    throw new Error(
      'VITE_RAYFIN_PUBLISHABLE_KEY environment variable is required'
    );
  }

  const client = initRayfinClient({
    baseUrl: apiUrl.endsWith('/') ? apiUrl : `${apiUrl}/`,
    publishableKey: publishableKey ?? 'local-dev-key',
    localDev,
  });

  if (localDev) {
    return new MockAuthService(client);
  }

  const workspaceId = import.meta.env.VITE_FABRIC_WORKSPACE_ID;
  const projectId = import.meta.env.VITE_FABRIC_ITEM_ID;
  const fabricPortalUrl = import.meta.env.VITE_FABRIC_PORTAL_URL;

  if (!workspaceId || !projectId || !fabricPortalUrl) {
    throw new Error(
      'Missing required Fabric config. Set VITE_FABRIC_WORKSPACE_ID, VITE_FABRIC_ITEM_ID, and VITE_FABRIC_PORTAL_URL.'
    );
  }

  return new RayfinAuthService(client, {
    workspaceId,
    projectId,
    fabricPortalUrl,
    returnOrigin: window.location.origin,
  });
}
