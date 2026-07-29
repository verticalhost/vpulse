import { useEffect, useState } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// Mirrors StreamerModeService.SendStateAsync on the backend.
export interface StreamerModeState {
  isConnected: boolean;
  /** False when OBS isn't installed, or its WebSocket server is off. `reason` says which. */
  isReady: boolean;
  reason: string;
}

const initial: StreamerModeState = { isConnected: false, isReady: false, reason: '' };

export function useStreamerMode(): StreamerModeState {
  const [state, setState] = useState<StreamerModeState>(initial);

  useEffect(() => {
    const onMessage = (event: Event) => {
      const data = (event as CustomEvent).detail;
      if (data?.method === 'StreamerModeState' && data.content) {
        setState({ ...initial, ...data.content });
      }
    };

    window.addEventListener('websocket-message', onMessage);

    // The backend only pushes this on connection and on change, and this component mounts after
    // that first push, so ask for it rather than depending on the ordering.
    sendMessageToBackend('GetStreamerModeState');

    return () => window.removeEventListener('websocket-message', onMessage);
  }, []);

  return state;
}
