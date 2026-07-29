import { TriangleAlert, LogOut, Ellipsis, Sparkles, ExternalLink } from 'lucide-react';
import { useAuth, ProviderName, ProviderAccount } from '../../Hooks/useAuth';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import Button from '../Button';

const VPZONE_BILLING_URL = 'https://vpzone.tv/settings/membership';

// Gamefolio shipped public-client support (RFC 8252 8.5) on 2026-07-29 and flipped VPULSE's app
// to public server-side: the token exchange is client_id + PKCE verifier only, which a desktop
// app can do honestly — no secret in the binary.
const GAMEFOLIO_AVAILABLE = true;

function openExternal(url: string) {
  sendMessageToBackend('OpenInBrowser', { Url: url });
}

function formatDate(iso: string | null): string | null {
  if (!iso) return null;
  try {
    return new Date(iso).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  } catch {
    return null;
  }
}

function Avatar({ account }: { account: ProviderAccount }) {
  return (
    <div className="w-14 h-14 rounded-full overflow-hidden bg-base-200 shrink-0">
      <img
        src={account.avatarUrl || '/default-avatar.png'}
        alt=""
        className="w-full h-full object-cover"
        onError={(e) => {
          (e.currentTarget as HTMLImageElement).src = '/default-avatar.png';
        }}
      />
    </div>
  );
}

function SignInError({ provider }: { provider: ProviderName }) {
  const { signInStatus, clearSignInStatus } = useAuth();
  const status = signInStatus[provider];
  if (status !== 'failed' && status !== 'expired' && status !== 'unavailable') return null;

  const message =
    status === 'expired'
      ? 'The sign-in link expired. Try again.'
      : status === 'unavailable'
        ? 'Sign-in is unavailable right now.'
        : "Sign-in couldn't be completed.";

  return (
    <div className="flex items-center gap-2 mt-3 text-sm text-error">
      <TriangleAlert size={16} />
      <span>{message}</span>
      <button className="underline ml-auto" onClick={() => clearSignInStatus(provider)}>
        Dismiss
      </button>
    </div>
  );
}

export default function AccountSection() {
  const { vpzone, gamefolio, vpzPlus, signInStatus, signIn, cancelSignIn, signOut } = useAuth();

  const renewal = formatDate(vpzPlus.currentPeriodEnd);
  const memberSince = formatDate(vpzPlus.since);

  return (
    <div className="space-y-8">
      <section>
        <h2 className="text-lg font-semibold text-white mb-1">VPULSE account</h2>
        <p className="text-sm text-gray-400 mb-4">
          Sign in with VPZONE to sync your membership and unlock VPZ+ features.
        </p>

        <div className="bg-base-200 rounded-lg p-4">
          {!vpzone.isSignedIn ? (
            <>
              <Button
                className="w-full"
                disabled={!vpzone.isConfigured || signInStatus.vpzone === 'waiting'}
                onClick={() => signIn('vpzone')}
              >
                {signInStatus.vpzone === 'waiting'
                  ? 'Waiting for your browser...'
                  : 'Continue with VPZONE'}
              </Button>

              {signInStatus.vpzone === 'waiting' && (
                <button
                  className="text-sm text-gray-400 underline mt-3 w-full"
                  onClick={() => cancelSignIn('vpzone')}
                >
                  Cancel
                </button>
              )}

              {!vpzone.isConfigured && (
                <p className="text-xs text-gray-500 mt-3">
                  VPZONE sign-in isn't configured in this build yet.
                </p>
              )}

              <SignInError provider="vpzone" />
            </>
          ) : (
            <>
              <div className="flex items-center gap-4">
                <Avatar account={vpzone} />
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="text-white font-semibold truncate">
                      {vpzone.displayName || vpzone.username}
                    </span>
                    {vpzPlus.isActive && (
                      <span className="flex items-center gap-1 text-xs font-semibold px-2 py-0.5 rounded-full bg-primary/15 text-primary border border-primary/30">
                        <Sparkles size={12} />
                        VPZ+
                      </span>
                    )}
                  </div>
                  <div className="text-sm text-gray-400 truncate">@{vpzone.username}</div>
                </div>

                <div className="dropdown dropdown-end ml-auto">
                  <div tabIndex={0} role="button" className="btn btn-ghost btn-sm">
                    <Ellipsis size={18} />
                  </div>
                  <ul
                    tabIndex={0}
                    className="dropdown-content menu bg-base-300 rounded-box z-10 w-44 p-2 shadow"
                  >
                    <li>
                      <button onClick={() => signOut('vpzone')}>
                        <LogOut size={16} />
                        Sign out
                      </button>
                    </li>
                  </ul>
                </div>
              </div>

              <div className="mt-4 pt-4 border-t border-base-400/50 text-sm">
                {vpzPlus.isActive ? (
                  <div className="flex items-center justify-between gap-4">
                    <div className="text-gray-400">
                      {vpzPlus.cancelAtPeriodEnd && renewal
                        ? `Ends ${renewal}`
                        : renewal
                          ? `Renews ${renewal}`
                          : 'Active'}
                      {memberSince && <span className="text-gray-500"> · member since {memberSince}</span>}
                    </div>
                    <button
                      className="flex items-center gap-1 text-primary hover:underline shrink-0"
                      onClick={() => openExternal(VPZONE_BILLING_URL)}
                    >
                      Manage
                      <ExternalLink size={14} />
                    </button>
                  </div>
                ) : (
                  <div className="flex items-center justify-between gap-4">
                    <span className="text-gray-400">
                      VPZ+ unlocks 1440p and 4K recording, longer replay buffers and automatic
                      highlights.
                    </span>
                    <button
                      className="flex items-center gap-1 text-primary hover:underline shrink-0"
                      onClick={() => openExternal(VPZONE_BILLING_URL)}
                    >
                      Get VPZ+
                      <ExternalLink size={14} />
                    </button>
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </section>

      <section>
        <h2 className="text-lg font-semibold text-white mb-1">Connected services</h2>
        <p className="text-sm text-gray-400 mb-4">
          Publish clips to a service without leaving VPULSE.
        </p>

        <div className="bg-base-200 rounded-lg p-4 flex items-center gap-4">
          <div className="min-w-0">
            <div className="text-white font-semibold">Gamefolio</div>
            <div className="text-sm text-gray-400 truncate">
              {!GAMEFOLIO_AVAILABLE
                ? 'Coming soon — clips stay on your PC for now.'
                : gamefolio.isSignedIn
                  ? `@${gamefolio.username}`
                  : 'Publish clips and get a share link.'}
            </div>
          </div>

          <div className="ml-auto shrink-0">
            {!GAMEFOLIO_AVAILABLE ? (
              <Button disabled>Coming soon</Button>
            ) : gamefolio.isSignedIn ? (
              <Button onClick={() => signOut('gamefolio')}>Disconnect</Button>
            ) : (
              <Button
                disabled={!gamefolio.isConfigured || signInStatus.gamefolio === 'waiting'}
                onClick={() => signIn('gamefolio')}
              >
                {signInStatus.gamefolio === 'waiting' ? 'Waiting...' : 'Connect'}
              </Button>
            )}
          </div>
        </div>

        {GAMEFOLIO_AVAILABLE && <SignInError provider="gamefolio" />}
      </section>
    </div>
  );
}
