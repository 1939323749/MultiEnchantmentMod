# CI/CD Setup Guide

## Overview

The release workflow automates:

1. Build `.dll` from source
2. Package `.dll` + `.json` into a zip
3. Publish to GitHub Releases
4. Upload to NexusMods on tagged releases

## Prerequisites

### 1. Game DLL Cache

The build needs `sts2.dll` and `0Harmony.dll` to compile against. These are not committed to the repo.

**First-time setup:**

- Option A: Run the `Seed Game DLL Cache` workflow on a self-hosted runner with the game installed.
- Option B: Manually upload DLLs to the cache:
  1. Copy `sts2.dll` and `0Harmony.dll` from your game's `data_sts2_windows_x86_64/` directory.
  2. Place them in `.ci/gamedlls/` locally.
  3. Use `gh cache set` or push and run the seed workflow.

**When the game updates:**

1. Update `.ci/dll-version.txt` with the new game version. This busts the cache key.
2. Re-run the seed workflow or manually update the cache.

### 2. NexusMods Upload

The workflow uses the official `Nexus-Mods/upload-action` GitHub Action. This replaces the old `unex` cookie-based upload path, which was unreliable in CI because NexusMods is protected by Cloudflare.

**Repository secrets to configure:**

| Secret | Description |
|--------|-------------|
| `NEXUSMODS_API_KEY` | NexusMods API key from https://www.nexusmods.com/settings/api-keys. |
| `NEXUS_FILE_GROUP_ID` | NexusMods file group ID for this mod's main file. |

**Getting the file group ID:**

1. Create the NexusMods mod page.
2. Upload at least one file manually once, so NexusMods creates a file group.
3. Open the mod page's Files tab and choose API Info, or open the Manage Files page.
4. Store the group ID as the `NEXUS_FILE_GROUP_ID` secret.

## Usage

### Tagged Release

```bash
git tag v2.3.1
git push origin v2.3.1
```

This triggers: build -> package -> GitHub Release -> NexusMods upload.

### Nightly Build

Go to Actions -> "Build & Release" -> Run workflow -> check "nightly" -> Run.

This creates a GitHub pre-release and does not upload to NexusMods.

### Local Release Fallback

```bash
dotnet publish
# DLL + JSON are copied to the game mods folder.
# Zip them manually only if the release workflow is unavailable.
```
