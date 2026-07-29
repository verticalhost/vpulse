// Generates a kill feed profile for a game by having a vision model locate the feed in sample
// frames from a real recording.
//
// This is the "AI at calibration time, plain data at runtime" split: the model runs HERE, once,
// on a maintainer's machine (or later, on a VPZONE server). What ships to users is only the JSON
// file this writes into Backend/Games/Profiles/ — no model, no API key, nothing at runtime.
//
// Two model backends:
//
//   --local [url]   A local vision model through Ollama or LM Studio (OpenAI-compatible API).
//                   Free, offline, nothing leaves the machine. Default url is Ollama's
//                   http://localhost:11434/v1. Pick the model with --model (default qwen2.5vl:7b).
//   (default)       Claude via the Anthropic API — needs ANTHROPIC_API_KEY or an `ant auth login`
//                   profile. More reliable at precise coordinates; costs a few cents per run.
//
// Usage:
//   node calibrate.mjs <video> "<Game Name>" [--local [url]] [--model <name>]
//                      [--frames N] [--ffmpeg <path>] [--mock <response.json>]
//
// --mock skips the model call and reads the response from a file — used to exercise the
// pipeline (extraction, parsing, clamping, profile writing, preview) without any model.

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
const options = { frames: 4, ffmpeg: null, mock: null, local: null, model: null };

for (let i = 0; i < args.length; i++) {
  if (args[i] === "--frames") options.frames = parseInt(args[++i], 10);
  else if (args[i] === "--ffmpeg") options.ffmpeg = args[++i];
  else if (args[i] === "--mock") options.mock = args[++i];
  else if (args[i] === "--model") options.model = args[++i];
  else if (args[i] === "--local") {
    // "--local" alone targets Ollama's default endpoint; a URL may follow to target
    // LM Studio (http://localhost:1234/v1) or a remote box.
    options.local = args[i + 1]?.startsWith("http") ? args[++i] : "http://localhost:11434/v1";
  } else positional.push(args[i]);
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

function probeVideo(video) {
  // ffmpeg prints "Duration: HH:MM:SS.cc" and the stream dimensions on stderr for any input.
  try {
    execFileSync(ffmpeg, ["-hide_banner", "-i", video], { stdio: "pipe" });
  } catch (error) {
    const text = String(error.stderr ?? "");
    const duration = text.match(/Duration: (\d+):(\d+):(\d+)\.(\d+)/);
    const dims = text.match(/, (\d{2,5})x(\d{2,5})[ ,]/);
    if (duration && dims) {
      return {
        durationSeconds:
          Number(duration[1]) * 3600 +
          Number(duration[2]) * 60 +
          Number(duration[3]) +
          Number(duration[4]) / 100,
        width: Number(dims[1]),
        height: Number(dims[2]),
      };
    }
  }
  throw new Error("could not read the video duration and dimensions");
}

function extractFrames(video, count) {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "vpulse-calibrate-"));
  const { durationSeconds: duration } = probeVideo(video);

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

// Local vision model through an OpenAI-compatible endpoint (Ollama, LM Studio). Free and
// offline — nothing leaves the machine. Small models are noticeably weaker than Claude at
// precise coordinates, which is why the preview-crop check exists; expect to re-run or nudge
// more often than with the cloud path.
async function locateFeedWithLocalModel(frames) {
  const model = options.model ?? "qwen2.5vl:7b";
  console.log(`  (modele local : ${model} via ${options.local})`);

  const { width, height } = probeVideo(videoPath);

  // Everything below plays to what a small grounding model is actually good at, learned by
  // measurement against this exact task:
  //
  //   - Full resolution. Downscaling to fit more frames per request broke the model outright —
  //     at 1344px it could no longer read the feed it reads perfectly at native size.
  //   - One frame per request. It sees and describes the feed reliably on a single frame.
  //   - Its NATIVE grounding format ("output its bbox coordinates using JSON format" →
  //     bbox_2d pixel boxes). Asked for a custom JSON with fractions, or even a custom pixel
  //     box, the same model on the same frame answered found:false; asked in its trained
  //     format it boxed both names of the feed row precisely.
  //
  // Each frame yields the boxes of the feed text it shows; the union across frames (with
  // outliers dropped) is the feed area, and the margin is arithmetic done here, not judgment
  // delegated to the model.
  const groundingPrompt =
    "Locate the kill feed text (rows of player names with a weapon icon between them, where " +
    "players eliminating each other are listed), output its bbox coordinates using JSON format.";

  const boxes = [];
  let notes = "";

  for (const frame of frames) {
    const text = await askLocal(model, groundingPrompt, [frame.path]);
    // Grounding answers are a JSON array of {bbox_2d: [x1,y1,x2,y2], label}; collect them all.
    for (const match of text.matchAll(/"bbox_2d"\s*:\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]/g)) {
      boxes.push(match.slice(1, 5).map(Number));
    }
  }

  if (boxes.length === 0) {
    return { found: false, notes: "the model boxed nothing that looks like a kill feed" };
  }

  // Drop outliers before taking the union: one stray box on unrelated HUD text would otherwise
  // stretch the region across the screen. Keep boxes whose centre sits near the median centre.
  const centers = boxes.map(([x1, y1, x2, y2]) => [(x1 + x2) / 2, (y1 + y2) / 2]);
  const median = (values) => values.slice().sort((a, b) => a - b)[Math.floor(values.length / 2)];
  const medianX = median(centers.map((c) => c[0]));
  const medianY = median(centers.map((c) => c[1]));
  const kept = boxes.filter((_, i) => {
    const [cx, cy] = centers[i];
    return Math.abs(cx - medianX) < width * 0.25 && Math.abs(cy - medianY) < height * 0.25;
  });

  const x1 = Math.min(...kept.map((b) => b[0]));
  const y1 = Math.min(...kept.map((b) => b[1]));
  const x2 = Math.max(...kept.map((b) => b[2]));
  const y2 = Math.max(...kept.map((b) => b[3]));
  console.log(`  boites retenues : ${kept.length}/${boxes.length}, union ${x1},${y1} -> ${x2},${y2}`);

  // One descriptive pass for the maintainer notes — the same question that reliably works.
  try {
    notes = await askLocal(
      model,
      "Describe the kill feed in this frame: what colour are the killer names (left) vs the " +
        "victim names (right)? Is there any row type that mimics a kill row but is not one " +
        "(revive, assist)? Is anything occluding the feed (stream overlay)? Two sentences max.",
      [frames[0].path],
    );
  } catch {
    /* notes are advisory */
  }

  // Union box → relative region with margin. Grounding boxes are tight around the text, and a
  // tight region clips names; feeds also stack downward, so most margin goes below.
  const x = x1 / width - 0.03;
  const y = y1 / height - 0.02;
  return {
    found: true,
    regionX: x,
    regionY: y,
    regionWidth: x2 / width - x + 0.015,
    regionHeight: y2 / height - y + 0.06,
    notes: notes.trim(),
  };
}

// One request to the local endpoint with one or more image files; returns the raw text answer.
async function askLocal(model, promptText, imagePaths) {
  const images = imagePaths.map((p) => fs.readFileSync(p).toString("base64"));
  const isOllama = options.local.includes("11434");

  if (isOllama) {
    // Ollama's native API, because its OpenAI-compatible endpoint cannot raise the context
    // window per request and the default 4096 cannot hold a full-res frame plus prompt.
    const base = options.local.replace(/\/v1\/?$/, "");
    const response = await fetch(`${base}/api/chat`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        model,
        stream: false,
        options: { num_ctx: 8192, temperature: 0 },
        messages: [{ role: "user", content: promptText, images }],
      }),
    });
    if (!response.ok) {
      throw new Error(`ollama returned ${response.status}: ${await response.text()}`);
    }
    const text = (await response.json()).message?.content;
    if (!text) throw new Error("the local model returned no content");
    return text;
  }

  // Anything else OpenAI-compatible (LM Studio at http://localhost:1234/v1, a remote box).
  // Context size is configured server-side there.
  const content = images.map((data) => ({
    type: "image_url",
    image_url: { url: `data:image/jpeg;base64,${data}` },
  }));
  content.push({ type: "text", text: promptText });

  const response = await fetch(`${options.local}/chat/completions`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      model,
      messages: [{ role: "user", content }],
      temperature: 0,
      max_tokens: 1500,
    }),
  });
  if (!response.ok) {
    throw new Error(`local endpoint returned ${response.status}: ${await response.text()}`);
  }
  const text = (await response.json()).choices?.[0]?.message?.content;
  if (!text) throw new Error("the local model returned no content");
  return text;
}

async function locateFeedWithModel(frames) {
  if (options.mock) {
    console.log(`  (mode simulation : reponse lue depuis ${options.mock})`);
    return JSON.parse(fs.readFileSync(options.mock, "utf-8"));
  }

  if (options.local) return locateFeedWithLocalModel(frames);

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
