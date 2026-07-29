/**
 * Which of the two clipping modes a game falls into.
 *
 *   'auto'      A native integration reads the game's own files or overlay and bookmarks kills
 *               while you play. Nothing to set up, nothing to run afterwards.
 *   'semi-auto' No integration exists, so kills are found by reading the kill feed back off the
 *               recording afterwards — calibrate once, then scan a session from its card.
 *
 * The names below mirror the matching in Backend/Games/GameIntegrationService.cs. This copy is
 * presentational only: it decides which badge to show, never whether an integration runs. If the
 * two ever drift the badge is wrong, which is worth knowing but harms nothing.
 */
export type ClippingMode = 'auto' | 'semi-auto';

const NATIVE_INTEGRATION_PATTERNS = [
  'counter-strike 2',
  'league of legends',
  'pubg',
  "playerunknown's battlegrounds",
  'rocket league',
  'grand theft auto',
  'fivem',
  'rage multiplayer',
  'dota 2',
  'rust',
  'minecraft',
  'dragonwilds',
  'war thunder',
];

export function getClippingMode(gameName: string | undefined | null): ClippingMode {
  if (!gameName) return 'semi-auto';

  const name = gameName.toLowerCase();
  return NATIVE_INTEGRATION_PATTERNS.some((pattern) => name.includes(pattern)) ? 'auto' : 'semi-auto';
}
