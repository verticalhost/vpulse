# calibrate-game — AI-generated kill feed profiles

Maintainer tool. Give it a recording and a game name; a Claude vision model reads sample frames,
locates the kill feed, and the tool writes a ready-to-ship profile into
`Backend/Games/Profiles/`.

This is the "AI at calibration time, plain data at runtime" split: the model runs **here**, once,
on a maintainer's machine. What ships to users is only the JSON profile — no model, no API key,
nothing heavier at runtime than the OCR the app already does. The same script is the reference
implementation for a future VPZONE service where users submit a clip and get a profile back.

## Run

```bash
cd tools/calibrate-game
npm install
ANTHROPIC_API_KEY=sk-ant-... node calibrate.mjs "D:/clips/delta-force-match.mp4" "Delta Force"
```

Auth comes from `ANTHROPIC_API_KEY` or an `ant auth login` profile — either works; nothing is
stored in the repo.

Output:

- `Backend/Games/Profiles/<slug>.json` — the profile, with `playerName` left empty (the one field
  that never transfers between people; the app asks each user for it).
- `preview-<slug>.png` — the calibrated region cropped from a real frame. **Check it**: every feed
  row must show BOTH names in full. A clipped name destroys the kill/death distinction.
- The model's notes — name colours per column, revive-like rows, overlay occlusion. Anything
  actionable goes into `Backend/Games/Profiles/README.md` alongside the profile.

Then validate against a recording with known kills (in the app via **Find Kills**, or the proto
harness) before committing the profile.

## Flags

| Flag | Meaning |
|---|---|
| `--frames N` | Sample N frames instead of 4, spread through the middle of the recording |
| `--ffmpeg <path>` | ffmpeg binary (defaults to the one in `publish/`) |
| `--mock <file.json>` | Skip the API call; read the model response from a file. For testing the pipeline without credentials |
