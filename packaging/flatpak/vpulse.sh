#!/bin/sh
# VPULSE Flatpak launcher (installed as /app/bin/vpulse, the manifest `command`).
# Points the app at the bundled OBS runtime, which skips LinuxObsRuntime's download/re-exec.
export VPULSE_OBS_MODULE_PATH=/app/vpulse/obs-plugins
export VPULSE_OBS_MODULE_DATA_PATH=/app/vpulse/data/obs-plugins/%module%
export VPULSE_OBS_DATA_PATH=/app/vpulse/data/libobs

# /app/vpulse/lib first, so libobs's own FFmpeg 6 wins over the runtime's FFmpeg 7.
export LD_LIBRARY_PATH="/app/vpulse/lib:/app/vpulse${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# WebKit's DMA-BUF renderer fails under NVIDIA + the sandbox ("Failed to create GBM buffer"); force software.
export WEBKIT_DISABLE_DMABUF_RENDERER=1

exec /app/vpulse/VPULSE "$@"
