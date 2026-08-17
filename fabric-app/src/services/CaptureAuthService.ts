import { type AuthUser, type IAuthService } from './IAuthService';

/**
 * Screen-capture auth service. Used only when recording the demo video.
 *
 * The console is behind Fabric brokered sign-in, which means recording it
 * would put a real tenant and a real account name on screen -- neither of
 * which belongs in a public submission. This service hands back a fixed,
 * obviously-fake operator so the page renders without any sign-in, and the
 * reads underneath fall through to the bundled production snapshot
 * (`snapshotFallback.ts`), which is the same data the published capture
 * shows.
 *
 * It is selected only when BOTH hold:
 *
 * - `import.meta.env.DEV` — true only under `vite` dev server. Vite replaces
 *   this with the literal `false` in every production build, so the branch in
 *   `bootstrapAuth()` is dead code and is dropped at build time. It cannot
 *   ship.
 * - `VITE_DEMO_CAPTURE=1` — has to be asked for explicitly.
 */
export class CaptureAuthService implements IAuthService {
  readonly fabricAuthEnabled = false;

  private static readonly USER: AuthUser = {
    id: '00000000-0000-0000-0000-000000000000',
    email: 'admin@contoso-demo.example',
    name: '運用担当',
  };

  async signIn(): Promise<AuthUser> {
    return CaptureAuthService.USER;
  }

  async signOut(): Promise<void> {
    // Nothing to tear down; the session is a constant.
  }

  async getCurrentUser(): Promise<AuthUser | null> {
    return CaptureAuthService.USER;
  }

  async initEmbeddedAuth(): Promise<AuthUser | null> {
    return null;
  }
}
