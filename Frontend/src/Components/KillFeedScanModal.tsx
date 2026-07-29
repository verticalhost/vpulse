import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Crosshair, Loader2, ScanSearch, Skull, TriangleAlert } from 'lucide-react';
import Button from './Button';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { Content, KillFeedProfile } from '../Models/types';

interface RecognisedWord {
  text: string;
  relativeX: number;
}

interface RecognisedLine {
  text: string;
  words: RecognisedWord[];
}

interface Candidate {
  time: string;
  role: 'Kill' | 'Death' | 'Ambiguous';
  opponent: string;
  frameCount: number;
}

interface Region {
  x: number;
  y: number;
  width: number;
  height: number;
}

type Step = 'loading' | 'calibrate' | 'scanning' | 'review';

/**
 * Finds kills in a recording after the fact, by reading the game's kill feed.
 *
 * This is the fallback for games VPULSE has no native integration for. Games that detect live
 * (PUBG, GTA, Rocket League) mark their own bookmarks while recording and never need this.
 */
export default function KillFeedScanModal({
  content,
  onClose,
}: {
  content: Content;
  onClose: () => void;
}) {
  // Profiles are files on disk, one per game, not part of the settings blob — so they are asked
  // for rather than read from local state. 'loading' keeps the calibration screen from flashing up
  // for a game that turns out to already have one.
  const [step, setStep] = useState<Step>('loading');
  const [profilePath, setProfilePath] = useState<string | null>(null);
  const [frame, setFrame] = useState<string | null>(null);
  const [frameLoading, setFrameLoading] = useState(false);
  const [atSeconds, setAtSeconds] = useState(0);
  const [region, setRegion] = useState<Region | null>(null);
  const [playerName, setPlayerName] = useState('');
  const [includeDeaths, setIncludeDeaths] = useState(true);
  const [lines, setLines] = useState<RecognisedLine[] | null>(null);
  const [testing, setTesting] = useState(false);
  const [percent, setPercent] = useState(0);
  const [candidates, setCandidates] = useState<Candidate[] | null>(null);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [error, setError] = useState<string | null>(null);

  const imageRef = useRef<HTMLImageElement>(null);
  const dragStart = useRef<{ x: number; y: number } | null>(null);

  const durationSeconds = useMemo(() => {
    const parts = (content.duration ?? '0:00').split(':').map(Number);
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    return 0;
  }, [content.duration]);

  const requestFrame = useCallback(
    (seconds: number) => {
      setFrameLoading(true);
      setLines(null);
      sendMessageToBackend('GetKillFeedCalibrationFrame', {
        FilePath: content.filePath,
        AtSeconds: seconds,
      });
    },
    [content.filePath],
  );

  useEffect(() => {
    if (step !== 'calibrate') return;
    // Start a third of the way in — the opening of a session is usually a lobby with no feed.
    const start = Math.floor(durationSeconds / 3);
    setAtSeconds(start);
    requestFrame(start);
  }, [step, durationSeconds, requestFrame]);

  useEffect(() => {
    sendMessageToBackend('GetKillFeedProfile', { GameName: content.game });
  }, [content.game]);

  useEffect(() => {
    const onMessage = (event: CustomEvent<{ method: string; content: any }>) => {
      const { method, content: payload } = event.detail;

      if (method === 'KillFeedProfile') {
        const profile: KillFeedProfile | null = payload.profile ?? null;
        setProfilePath(payload.filePath ?? null);

        if (profile) {
          setRegion({
            x: profile.regionX,
            y: profile.regionY,
            width: profile.regionWidth,
            height: profile.regionHeight,
          });
          setPlayerName(profile.playerName ?? '');
          setIncludeDeaths(profile.includeDeaths ?? true);
        }

        // A shipped profile carries a region but no player name, since that is the one thing that
        // cannot be shared. Treat it as a head start on calibration, not a finished profile.
        setStep((current) =>
          current === 'loading'
            ? profile?.playerName
              ? 'review'
              : 'calibrate'
            : current,
        );
      } else if (method === 'KillFeedCalibrationFrame') {
        setFrame(`data:image/png;base64,${payload.imageBase64}`);
        setFrameLoading(false);
      } else if (method === 'KillFeedCalibrationTest') {
        setLines(payload.lines ?? []);
        setTesting(false);
      } else if (method === 'KillFeedScanProgress') {
        setPercent(payload.percent ?? 0);
      } else if (method === 'KillFeedScanResult') {
        const found: Candidate[] = payload.candidates ?? [];
        setCandidates(found);
        // Ambiguous rows are where every false positive landed in testing, so they start
        // unchecked — visible, because hiding them would hide a badly placed region too.
        setSelected(
          new Set(found.map((c, i) => (c.role === 'Ambiguous' ? -1 : i)).filter((i) => i >= 0)),
        );
        setStep('review');
      } else if (method === 'KillFeedScanCancelled') {
        setStep('review');
      } else if (method === 'KillFeedScanError') {
        setError(payload.reason ?? 'Something went wrong.');
        setFrameLoading(false);
        setTesting(false);
        setStep((current) => (current === 'scanning' ? 'review' : current));
      }
    };

    window.addEventListener('websocket-message', onMessage as EventListener);
    return () => window.removeEventListener('websocket-message', onMessage as EventListener);
  }, []);

  const onMouseDown = (e: React.MouseEvent<HTMLImageElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    dragStart.current = {
      x: (e.clientX - rect.left) / rect.width,
      y: (e.clientY - rect.top) / rect.height,
    };
    setRegion(null);
    setLines(null);
  };

  const onMouseMove = (e: React.MouseEvent<HTMLImageElement>) => {
    if (!dragStart.current) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = (e.clientX - rect.left) / rect.width;
    const y = (e.clientY - rect.top) / rect.height;
    setRegion({
      x: Math.min(dragStart.current.x, x),
      y: Math.min(dragStart.current.y, y),
      width: Math.abs(x - dragStart.current.x),
      height: Math.abs(y - dragStart.current.y),
    });
  };

  const onMouseUp = () => {
    dragStart.current = null;
    // A stray click leaves a degenerate box; ignore it rather than testing an empty region.
    if (region && region.width > 0.02 && region.height > 0.01) {
      setTesting(true);
      sendMessageToBackend('TestKillFeedCalibration', {
        FilePath: content.filePath,
        AtSeconds: atSeconds,
        RegionX: region.x,
        RegionY: region.y,
        RegionWidth: region.width,
        RegionHeight: region.height,
      });
    }
  };

  const startScan = () => {
    if (!region || !playerName.trim()) return;
    setError(null);
    setPercent(0);
    setCandidates(null);
    setStep('scanning');

    sendMessageToBackend('SaveKillFeedProfile', {
      GameName: content.game,
      PlayerName: playerName.trim(),
      RegionX: region.x,
      RegionY: region.y,
      RegionWidth: region.width,
      RegionHeight: region.height,
      IncludeDeaths: includeDeaths,
    });

    sendMessageToBackend('ScanKillFeed', {
      FilePath: content.filePath,
      PlayerName: playerName.trim(),
      RegionX: region.x,
      RegionY: region.y,
      RegionWidth: region.width,
      RegionHeight: region.height,
    });
  };

  const applyBookmarks = () => {
    if (!candidates) return;

    // Sent as one batch, not one message per bookmark: each single AddBookmark rewrites the whole
    // metadata file, so several at once overwrite each other and only the last survives.
    const chosen = candidates
      .filter((_, index) => selected.has(index))
      .filter((candidate) => candidate.role !== 'Death' || includeDeaths)
      .map((candidate) => ({
        Type: candidate.role === 'Death' ? 'Death' : 'Kill',
        Time: candidate.time,
      }));

    if (chosen.length > 0) {
      sendMessageToBackend('AddBookmarks', {
        FilePath: content.filePath,
        ContentType: content.type,
        Bookmarks: chosen,
      });
    }

    onClose();
  };

  const toggle = (index: number) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(index)) next.delete(index);
      else next.add(index);
      return next;
    });
  };

  const canScan = Boolean(region && playerName.trim());

  return (
    <>
      <div className="modal-header pb-4 border-b border-gray-700">
        <div className="flex items-center">
          <ScanSearch className="text-primary mr-3" size={30} />
          <h2 className="font-bold text-2xl mb-0">Find kills in this recording</h2>
        </div>
        <p className="text-gray-400 mt-2">
          Reads {content.game}&rsquo;s kill feed from the video itself, for games VPULSE cannot
          detect while recording.
        </p>
        <Button variant="ghost" icon className="absolute right-4 top-4 z-10" onClick={onClose}>
          ✕
        </Button>
      </div>

      <div className="modal-body py-4 space-y-4">
        {error && (
          <div className="alert alert-error text-sm">
            <TriangleAlert size={18} />
            <span>{error}</span>
          </div>
        )}

        {step === 'loading' && (
          <div className="py-10 flex justify-center">
            <Loader2 className="animate-spin text-primary" size={28} />
          </div>
        )}

        {step === 'calibrate' && (
          <>
            <p className="text-sm text-gray-300">
              Drag a box around the kill feed. Include the whole row: your name, and the name of who
              you killed.
            </p>
            {region && !playerName && (
              <div className="rounded-lg bg-base-200 p-3 text-sm text-gray-300">
                A region for {content.game} ships with VPULSE and is drawn below. Add your in-game
                name to finish — that part cannot come from a shared profile.
              </div>
            )}

            <div className="relative select-none rounded-lg overflow-hidden bg-base-200">
              {frameLoading && (
                <div className="absolute inset-0 flex items-center justify-center z-10 bg-base-300/70">
                  <Loader2 className="animate-spin" size={28} />
                </div>
              )}
              {frame && (
                <img
                  ref={imageRef}
                  src={frame}
                  alt=""
                  draggable={false}
                  className="w-full cursor-crosshair"
                  onMouseDown={onMouseDown}
                  onMouseMove={onMouseMove}
                  onMouseUp={onMouseUp}
                  onMouseLeave={onMouseUp}
                />
              )}
              {region && (
                <div
                  className="absolute border-2 border-primary bg-primary/10 pointer-events-none"
                  style={{
                    left: `${region.x * 100}%`,
                    top: `${region.y * 100}%`,
                    width: `${region.width * 100}%`,
                    height: `${region.height * 100}%`,
                  }}
                />
              )}
            </div>

            <div className="flex items-center gap-3">
              <span className="text-xs text-gray-400 shrink-0">Frame</span>
              <input
                type="range"
                className="range range-xs range-primary"
                min={0}
                max={Math.max(1, durationSeconds - 1)}
                value={atSeconds}
                onChange={(e) => setAtSeconds(Number(e.target.value))}
                onMouseUp={() => requestFrame(atSeconds)}
                onTouchEnd={() => requestFrame(atSeconds)}
              />
              <span className="text-xs text-gray-400 tabular-nums shrink-0">
                {new Date(atSeconds * 1000).toISOString().substring(11, 19)}
              </span>
            </div>
            <p className="text-xs text-gray-500">
              Move to a moment where a kill is on screen, so you can check the box is right.
            </p>

            {testing && (
              <div className="flex items-center gap-2 text-sm text-gray-400">
                <Loader2 className="animate-spin" size={16} />
                Reading that region…
              </div>
            )}

            {lines !== null && !testing && (
              <div className="rounded-lg bg-base-200 p-3 space-y-2">
                {lines.length === 0 ? (
                  <p className="text-sm text-warning">
                    Nothing readable in that box. Try including more of the row, or pick a frame
                    where a kill is showing.
                  </p>
                ) : (
                  <>
                    <p className="text-xs text-gray-400">
                      Read in that box — click your own name:
                    </p>
                    {lines.map((line, i) => (
                      <div key={i} className="flex flex-wrap gap-1">
                        {line.words.map((word, j) => (
                          <button
                            key={j}
                            type="button"
                            onClick={() => setPlayerName(word.text)}
                            className={`px-2 py-0.5 rounded text-sm transition-colors ${
                              playerName === word.text
                                ? 'bg-primary text-primary-content'
                                : 'bg-base-300 hover:bg-base-100'
                            }`}
                          >
                            {word.text}
                          </button>
                        ))}
                      </div>
                    ))}
                  </>
                )}
              </div>
            )}

            <div className="space-y-2">
              <label className="text-sm text-gray-300">Your in-game name</label>
              <input
                type="text"
                className="input input-bordered w-full bg-base-200"
                value={playerName}
                onChange={(e) => setPlayerName(e.target.value)}
                placeholder="Exactly as it appears in the feed"
              />
              <p className="text-xs text-gray-500">
                Where your name sits in the row is what separates a kill from a death — first means
                you got the kill, last means you died.
              </p>
            </div>

            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                className="checkbox checkbox-sm checkbox-primary"
                checked={includeDeaths}
                onChange={(e) => setIncludeDeaths(e.target.checked)}
              />
              <Skull size={16} className="text-gray-400" />
              Also mark deaths
            </label>
          </>
        )}

        {step === 'scanning' && (
          <div className="py-8 space-y-4 text-center">
            <Loader2 className="animate-spin mx-auto text-primary" size={36} />
            <p className="text-gray-300">Reading the recording…</p>
            <progress className="progress progress-primary w-full" value={percent} max={100} />
            <p className="text-sm text-gray-500">
              {percent}% — this reads the whole file, so expect a couple of minutes for a long
              session.
            </p>
          </div>
        )}

        {step === 'review' && candidates === null && (
          <div className="space-y-3">
            <div className="rounded-lg bg-base-200 p-3 text-sm space-y-1">
              <div className="flex items-center gap-2">
                <Crosshair size={16} className="text-primary" />
                <span className="font-medium">Saved setup for {content.game}</span>
              </div>
              <p className="text-gray-400">
                Playing as <span className="text-gray-200">{playerName || '—'}</span>
                {includeDeaths ? ', kills and deaths' : ', kills only'}
              </p>
              {profilePath && (
                <p className="text-xs text-gray-500 break-all">
                  Saved to {profilePath} — this file can be shared with anyone playing the same game.
                </p>
              )}
            </div>
            <Button variant="ghost" onClick={() => setStep('calibrate')}>
              Adjust the region or name
            </Button>
          </div>
        )}

        {step === 'review' && candidates !== null && (
          <div className="space-y-3">
            {candidates.length === 0 ? (
              <p className="text-sm text-warning">
                No events found. If that is wrong, recalibrate on a frame that has a kill showing —
                the region is the usual cause.
              </p>
            ) : (
              <>
                <p className="text-sm text-gray-300">
                  {candidates.length} found. Uncheck anything that looks wrong before adding.
                </p>
                <div className="max-h-72 overflow-y-auto space-y-1">
                  {candidates.map((candidate, index) => (
                    <label
                      key={index}
                      className="flex items-center gap-3 p-2 rounded hover:bg-base-200 cursor-pointer"
                    >
                      <input
                        type="checkbox"
                        className="checkbox checkbox-sm checkbox-primary"
                        checked={selected.has(index)}
                        onChange={() => toggle(index)}
                      />
                      <span className="tabular-nums text-sm text-gray-400">
                        {candidate.time.substring(0, 8)}
                      </span>
                      <span
                        className={`badge badge-sm ${
                          candidate.role === 'Kill'
                            ? 'badge-primary'
                            : candidate.role === 'Death'
                              ? 'badge-error'
                              : 'badge-ghost'
                        }`}
                      >
                        {candidate.role === 'Ambiguous' ? 'unclear' : candidate.role.toLowerCase()}
                      </span>
                      <span className="text-sm text-gray-300 truncate">
                        {candidate.opponent || '—'}
                      </span>
                    </label>
                  ))}
                </div>
              </>
            )}
          </div>
        )}
      </div>

      <div className="modal-action border-t border-gray-700 pt-4">
        {step === 'calibrate' && (
          <>
            <Button variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button variant="primary" disabled={!canScan} onClick={startScan}>
              Scan recording
            </Button>
          </>
        )}

        {step === 'scanning' && (
          <Button
            variant="ghost"
            onClick={() => {
              sendMessageToBackend('CancelKillFeedScan');
              setStep('review');
            }}
          >
            Stop
          </Button>
        )}

        {step === 'review' && (
          <>
            <Button variant="ghost" onClick={onClose}>
              Close
            </Button>
            {candidates === null ? (
              <Button variant="primary" disabled={!canScan} onClick={startScan}>
                Scan recording
              </Button>
            ) : (
              <Button variant="primary" disabled={selected.size === 0} onClick={applyBookmarks}>
                Add {selected.size} bookmark{selected.size === 1 ? '' : 's'}
              </Button>
            )}
          </>
        )}
      </div>
    </>
  );
}
