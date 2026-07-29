// Generates a kill feed profile for a game by having a Claude vision model locate the feed in
// sample frames from a real recording.
//
// This is the "AI at calibration time, plain data at runtime" split: the model runs HERE, once,
// on a maintainer's machine (or later, on a VPZONE server). What ships to users is only the JSON
// file this writes into Backend/Games/Profiles/ — no model, no API key, nothing at runtime.
//
// Usage:
//   node calibrate.mjs <video> "<Game Name>" [--frames N] [--ffmpeg <path>] [--mock <response.json>]
//
// Auth: reads ANTHROPIC_API_KEY from the environment, or an `ant auth login` profile.
// --mock skips the API call and reads the model response from a file — used to exercise the
// pipeline (extraction, parsing, clamping, profile writing, preview) without credentials.

import Anthropic from "@anthropic-ai/sdk";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
const profilesDir = path.join(repoRoot, "Backend", "Games", "Profiles");

// ---------------------------------------------------------------------------
// Arguments

const args = process.argv.slice(2);
const positional = [];
const options = { frames: 4, ffmpeg: null, mock: null };

for (let i = 0; i < args.length; i++) {
  if (args[i] === "--frames") options.frames = parseInt(args[++i], 10);
  else if (args[i] === "--ffmpeg") options.ffmpeg = args[++i];
  else if (args[i] === "--mock") options.mock = args[++i];
  else positional.push(args[i]);
}

const [videoPath, gameName] = positional;
if (!videoPath || !gameName) {
  console.error('usage: node calibrate.mjs <video> "<Game Name>" [--frames N] [--ffmpeg <path>] [--mock <response.json>]');
  process.exit(2);
}
if (!fs.existsSync(videoPath)) {
  console.error(`video not found: ${videoPath}`);
  process.exit(2);
}

// Prefer the ffmpeg VPULSE already ships, so the tool has no extra dependency on dev machines.
const ffmpeg =
  options.ffmpeg ??
  [path.join(repoRoot, "publish", "ffmpeg.exe"), "ffmpeg"].find(
    (candidate) => candidate === "ffmpeg" || fs.existsSync(candidate),
  );

// Must match KillFeedProfileStore.ToSlug — the file name is how a recording's game resolves to
// a profile, so a divergence here would ship a profile the app never finds.
function toSlug(name) {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slug.length > 0 ? slug : "unknown";
}

// ---------------------------------------------------------------------------
// Frame extraction

function probeDurationSeconds(video) {
  // ffmpeg prints "Duration: HH:MM:SS.cc" on stderr for any input.
  try {
    execFileSync(ffmpeg, ["-hide_banner", "-i", video], { stdio: "pipe" });
  } catch (error) {
    const text = String(error.stderr ?? "");
    const match = text.match(/Duration: (\d+):(\d+):(\d+)\.(\d+)/);
    if (match) {
      return (
        Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]) + Number(match[4]) / 100
      );
    }
  }
  throw new Error("could not read the video duration");
}

function extractFrames(video, count) {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "vpulse-calibrate-"));
  const duration = probeDurationSeconds(video);

  // Spread through the middle of the recording. The opening is usually a lobby or loading
  // screen with no feed, and the very end is often a scoreboard.
  const frames = [];
  for (let i = 0; i < count; i++) {
    const t = duration * (0.25 + (0.6 * i) / Math.max(1, count - 1));
    const out = path.join(workDir, `frame_${i}.jpg`);
    execFileSync(ffmpeg, [
      "-hide_banner", "-loglevel", "error",
      "-ss", t.toFixed(2),
      "-i", video,
      "-frames:v", "1",
      "-q:v", "2",
      "-y", out,
    ]);
    if (fs.existsSync(out)) frames.push({ path: out, atSeconds: t });
  }

  if (frames.length === 0) throw new Error("no frames could be extracted");
  return { workDir, frames };
}

// ---------------------------------------------------------------------------
// The model call

const RESPONSE_SCHEMA = {
  type: "object",
  additionalProperties: false,
  required: ["found", "regionX", "regionY", "regionWidth", "regionHeight", "notes"],
  properties: {
    found: {
      type: "boolean",
      description: "Whether a kill feed is visible in at least one frame.",
    },
    regionX: { type: "number", description: "Left edge of the region, relative 0-1." },
    regionY: { type: "number", description: "Top edge of the region, relative 0-1." },
    regionWidth: { type: "number", description: "Width of the region, relative 0-1." },
    regionHeight: { type: "number", description: "Height of the region, relative 0-1." },
    notes: {
      type: "string",
      description:
        "Observations a maintainer needs: name colours per column, revive/assist rows that share the kill-row shape, overlays occluding the feed, anything unusual.",
    },
  },
};

const PROMPT = `These are frames from a gameplay recording of "${gameName}". Locate the kill feed: the stack of rows shaped "killerName [weapon icon] victimName" that appears when players are eliminated (most shooters draw it in the top-right corner). Do not confuse it with squad lists, chat, objective text, or scoreboards — those lack the two-names-around-an-icon row shape.

Return ONE region, in coordinates relative to the frame (0-1), that covers the feed across ALL frames where it appears:
- Include the complete rows: the full killer name on the left AND the full victim name on the right. A region that clips either name destroys the kill/death distinction downstream.
- Include vertical room for the maximum number of stacked rows you see, plus one extra row of margin below — feeds grow downward.
- Add horizontal margin on both sides. Too wide costs nothing; too narrow breaks detection.
- If a streamer overlay covers part of the feed area, extend the region below the overlay so scrolled rows are still caught.

In "notes", record what a maintainer must know: the colour of killer names vs victim names (dark-red killer names matter — they need a second OCR pass), any row type that mimics a kill row but is not one (revives, assists), and anything occluding the feed.

If no kill feed is visible in any frame, set found=false and say what you saw instead.`;

async function locateFeedWithModel(frames) {
  if (options.mock) {
    console.log(`  (mode simulation : reponse lue depuis ${options.mock})`);
    return JSON.parse(fs.readFileSync(options.mock, "utf-8"));
  }

  const client = new Anthropic();

  const content = frames.map((frame) => ({
    type: "image",
    source: {
      type: "base64",
      media_type: "image/jpeg",
      data: fs.readFileSync(frame.path).toString("base64"),
    },
  }));
  content.push({ type: "text", text: PROMPT });

  // Server-side fallback: if a safety classifier declines (games full of combat imagery make
  // that conceivable if unlikely), the API retries on the recommended fallback model in the
  // same call instead of failing the run.
  const response = await client.beta.messages.create({
    model: "claude-opus-5",
    max_tokens: 16000,
    betas: ["server-side-fallback-2026-07-01"],
    fallbacks: "default",
    output_config: { format: { type: "json_schema", schema: RESPONSE_SCHEMA } },
    messages: [{ role: "user", content }],
  });

  if (response.stop_reason === "refusal") {
    throw new Error(
      `the model declined the request (category: ${response.stop_details?.category ?? "unknown"})`,
    );
  }

  const text = response.content.find((block) => block.type === "text")?.text;
  if (!text) throw new Error("the model returned no text content");
  return JSON.parse(text);
}

// ---------------------------------------------------------------------------
// Profile writing + preview

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function writeProfile(result) {
  const slug = toSlug(gameName);
  const profilePath = path.join(profilesDir, `${slug}.json`);

  const x = clamp(result.regionX, 0, 0.95);
  const y = clamp(result.regionY, 0, 0.95);
  const profile = {
    gameName,
    regionX: round4(x),
    regionY: round4(y),
    regionWidth: round4(clamp(result.regionWidth, 0.03, 1 - x)),
    regionHeight: round4(clamp(result.regionHeight, 0.02, 1 - y)),
    // The one field that never transfers between people — the app asks each user for it.
    playerName: "",
    scanFramesPerSecond: 1.0,
    includeDeaths: true,
  };

  fs.mkdirSync(profilesDir, { recursive: true });
  fs.writeFileSync(profilePath, JSON.stringify(profile, null, 2) + "\n");
  return { profilePath, profile };
}

const round4 = (v) => Math.round(v * 10000) / 10000;

function writePreview(profile, frame) {
  // A crop of the calibrated region over a real frame, so the maintainer can eyeball the
  // result before committing it — the same check that caught a mis-set region by hand.
  const preview = path.join(here, `preview-${toSlug(gameName)}.png`);
  const n = (v) => String(v);
  execFileSync(ffmpeg, [
    "-hide_banner", "-loglevel", "error",
    "-i", frame.path,
    "-vf",
    `crop=iw*${n(profile.regionWidth)}:ih*${n(profile.regionHeight)}:iw*${n(profile.regionX)}:ih*${n(profile.regionY)},scale=iw*2:ih*2:flags=lanczos`,
    "-y", preview,
  ]);
  return preview;
}

// ---------------------------------------------------------------------------
// Main

console.log(`jeu    : ${gameName}`);
console.log(`video  : ${videoPath}`);

const { workDir, frames } = extractFrames(videoPath, options.frames);
console.log(`images : ${frames.length} extraites (${frames.map((f) => Math.round(f.atSeconds) + "s").join(", ")})`);

try {
  const result = await locateFeedWithModel(frames);

  if (!result.found) {
    console.error(`\nAucun kill feed trouve. Notes du modele :\n  ${result.notes}`);
    process.exit(1);
  }

  const { profilePath, profile } = writeProfile(result);
  const preview = writePreview(profile, frames[frames.length - 1]);

  console.log(`\nprofil : ${path.relative(repoRoot, profilePath)}`);
  console.log(JSON.stringify(profile, null, 2));
  console.log(`\nnotes du modele :\n  ${result.notes}`);
  console.log(`\napercu de la region : ${preview}`);
  console.log(
    "\nVerifie l'apercu (les DEUX noms de chaque ligne doivent etre entiers), puis valide sur un" +
      "\nenregistrement aux kills connus avant de committer le profil.",
  );
} finally {
  fs.rmSync(workDir, { recursive: true, force: true });
}
