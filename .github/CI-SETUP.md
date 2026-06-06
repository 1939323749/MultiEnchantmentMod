# CI/CD Setup Guide

## Overview

The release workflow automates:
1. Build `.dll` from source
2. Package `.dll` + `.json` + `.pck` into a zip
3. Publish to GitHub Releases
4. Upload to NexusMods (tagged releases only)

## Prerequisites

### 1. Game DLL Cache

The build needs `sts2.dll` and `0Harmony.dll` to compile against. These are NOT committed to the repo.

**First-time setup:**
- Option A: Run the `Seed Game DLL Cache` workflow on a self-hosted runner with the game installed
- Option B: Manually upload DLLs to the cache:
  1. Copy `sts2.dll` and `0Harmony.dll` from your game's `data_sts2_windows_x86_64/` directory
  2. Place them in `.ci/gamedlls/` locally
  3. Use `gh cache set` or push + run the seed workflow

**When the game updates:**
1. Update `.ci/dll-version.txt` with the new game version (this busts the cache key)
2. Re-run the seed workflow or manually update the cache

### 2. Pre-built .pck

The `.pck` file requires MegaDot (Godot fork) to export. CI does not install MegaDot.

**Workflow:**
1. Build the `.pck` locally: `dotnet publish` (requires MegaDot configured in `GodotPath`)
2. Copy the exported `.pck` to `.ci/MultiEnchantmentMod.pck`
3. Commit and push — the release workflow will include it in the zip

### 3. NexusMods Upload (unex)

**Repository Secrets to configure:**

| Secret | Description |
|--------|-------------|
| `NEXUS_COOKIES` | Your NexusMods auth cookies for unex. See below. |

**Getting cookies for unex:**
1. Log in to NexusMods in your browser
2. Open DevTools → Application → Cookies → nexusmods.com
3. Copy the values for `nexusmodsSIWEAuth` (or the session cookie unex requires)
4. Store as the `NEXUS_COOKIES` secret in the format unex expects

**Update `NEXUS_MOD_ID`:**
Edit `.github/workflows/release.yml` and set `NEXUS_MOD_ID` to your mod's numeric ID on NexusMods.

## Usage

### Tagged Release (full publish)
```bash
git tag v2.3.1
git push origin v2.3.1
```
This triggers: build → package → GitHub Release → NexusMods upload.

### Nightly Build (manual)
Go to Actions → "Build & Release" → Run workflow → check "nightly" → Run.
Creates a pre-release on GitHub. Does NOT upload to NexusMods.

### Local Release (fallback)
```bash
dotnet publish
# DLL + PCK + JSON are copied to game mods folder
# Zip them manually for NexusMods upload
```
