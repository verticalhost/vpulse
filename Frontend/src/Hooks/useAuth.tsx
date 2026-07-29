import { createContext, useContext, useEffect, useMemo, useState, ReactNode } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// The backend owns the sessions. It never sends tokens over the websocket, so everything here is
// display state: a username, an avatar and a few booleans. Signing in and out are messages; this
// side cannot authenticate on its own and holds nothing worth stealing.

export type ProviderName = 'vpzone' | 'gamefolio';

export interface ProviderAccount {
  isSignedIn: boolean;
  /** False until a client id is configured for the provider, which hides its sign-in button. */
  isConfigured: boolean;
  username: string | null;
  displayName: string | null;
  avatarUrl: string | null;
}

export interface VpzPlusState {
  isActive: boolean;
  since: string | null;
  currentPeriodEnd: string | null;
  cancelAtPeriodEnd: boolean;
  /** 'stripe' for a paid subscription, 'granted' for a gift or comp. */
  source: string | null;
  capabilities: string[];
}

interface AuthState {
  vpzone: ProviderAccount;
  gamefolio: ProviderAccount;
  vpzPlus: VpzPlusState;
}

export type SignInStatus = 'idle' | 'waiting' | 'failed' | 'expired' | 'unavailable';

interface AuthContextType extends AuthState {
  signInStatus: Record<ProviderName, SignInStatus>;
  signIn: (provider: ProviderName) => void;
  cancelSignIn: (provider: ProviderName) => void;
  signOut: (provider: ProviderName) => void;
  refresh: () => void;
  clearSignInStatus: (provider: ProviderName) => void;
}

const emptyAccount: ProviderAccount = {
  isSignedIn: false,
  isConfigured: false,
  username: null,
  displayName: null,
  avatarUrl: null,
};

const emptyVpzPlus: VpzPlusState = {
  isActive: false,
  since: null,
  currentPeriodEnd: null,
  cancelAtPeriodEnd: false,
  source: null,
  capabilities: [],
};

const AuthContext = createContext<AuthContextType | null>(null);

// Sign-out callbacks external code can register (e.g. queryClient.clear()).
const signOutCallbacks: Array<() => void> = [];
export function onSignOut(cb: () => void) {
  signOutCallbacks.push(cb);
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({
    vpzone: emptyAccount,
    gamefolio: emptyAccount,
    vpzPlus: emptyVpzPlus,
  });
  const [signInStatus, setSignInStatus] = useState<Record<ProviderName, SignInStatus>>({
    vpzone: 'idle',
    gamefolio: 'idle',
  });

  useEffect(() => {
    const onMessage = (event: Event) => {
      const data = (event as CustomEvent).detail;

      if (data?.method === 'AuthState' && data.content) {
        setState((previous) => {
          const next = {
            vpzone: { ...emptyAccount, ...data.content.vpzone },
            gamefolio: { ...emptyAccount, ...data.content.gamefolio },
            vpzPlus: { ...emptyVpzPlus, ...data.content.vpzPlus },
          };
          if (previous.vpzone.isSignedIn && !next.vpzone.isSignedIn) {
            signOutCallbacks.forEach((cb) => cb());
          }
          return next;
        });
      }

      if (data?.method === 'OAuthLoginResult' && data.content) {
        const provider = data.content.provider as ProviderName;
        const status = data.content.status as string;
        // 'success' and 'cancelled' both return to idle: on success the AuthState push that
        // follows is what the UI should react to, and a cancel is not an error worth showing.
        const resolved: SignInStatus =
          status === 'success' || status === 'cancelled' ? 'idle' : (status as SignInStatus);
        setSignInStatus((previous) => ({ ...previous, [provider]: resolved }));
      }
    };

    window.addEventListener('websocket-message', onMessage);
    return () => window.removeEventListener('websocket-message', onMessage);
  }, []);

  const value = useMemo<AuthContextType>(
    () => ({
      ...state,
      signInStatus,
      signIn: (provider) => {
        setSignInStatus((previous) => ({ ...previous, [provider]: 'waiting' }));
        sendMessageToBackend('StartOAuthLogin', { provider });
      },
      cancelSignIn: (provider) => {
        setSignInStatus((previous) => ({ ...previous, [provider]: 'idle' }));
        sendMessageToBackend('CancelOAuthLogin', { provider });
      },
      signOut: (provider) => sendMessageToBackend('SignOutProvider', { provider }),
      refresh: () => sendMessageToBackend('RefreshAuthState', {}),
      clearSignInStatus: (provider) =>
        setSignInStatus((previous) => ({ ...previous, [provider]: 'idle' })),
    }),
    [state, signInStatus],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextType {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}

/** Convenience for the settings sections that gate controls on the plan. */
export function useVpzPlus() {
  const { vpzPlus } = useAuth();
  return {
    ...vpzPlus,
    has: (capability: string) => vpzPlus.capabilities.includes(capability),
  };
}
