# Release guide

Current release state (2026-11-26): the public repository has **no tags, no
GitHub Releases, and no successful `release` or `driver` workflow run**. Packaging
scripts and workflows exist; they are not evidence of a published release.

## Release gates

Do not tag a release until all applicable gates are green and the evidence is
attached to the release issue/PR:

- [ ] `dotnet test RasaMic.slnx -c Release` — 184/184 on the release commit.
- [ ] Native builds pass for every shipped server RID and mobile ABI.
- [ ] `perf` workflow passes: zero-allocation gate, p95 <50 ms, benchmarks, enforced
      per-runner baseline, and published step summary.
- [ ] `native.yml` Android app job builds the Release APK with zero warnings/errors,
      and the candidate APK is smoke-tested on a real device.
- [ ] iOS unsigned compile passes on macOS; device signing is separately verified
      when an Apple account is available.
- [ ] macOS HAL compiles and the enumeration/ring spikes pass on a supported runner.
- [ ] Windows driver workflow runs for x64 and ARM64; Driver Verifier/install test
      is recorded for the chosen signing path.
- [ ] README, user guide, HANDOFF, third-party notices, and platform status match
      the release.
- [ ] `main` branch protection requires review and applicable CI/native/perf checks.
- [ ] No secrets, certificates, model archives, build output, or `.tools/` files
      are staged.
- [ ] Release artifacts are downloaded and inspected, not only workflow-green.

The macOS rerun, driver, and branch-protection gates are currently open.

## Version and tag

Versions use `vMAJOR.MINOR.PATCH` tags. The packaging workflows strip the leading
`v`; a dispatch without a tag falls back to `0.1.0`.

```sh
git status
git log -1 --oneline
git tag v0.1.0
git push origin v0.1.0
```

Only create/push a tag when the user explicitly authorizes a release. Never
force-push `main` or move a published tag without a documented recovery plan.

## CI artifact matrix

`.github/workflows/release.yml` packages six server RIDs:

| RID | Native artifact | Managed payload |
|---|---|---|
| win-x64 | `rasamic_native.dll` | Self-contained `rasamicd` zip + Velopack GUI installer |
| win-arm64 | `rasamic_native.dll` | Self-contained `rasamicd` zip + Velopack GUI installer |
| linux-x64 | `librasamic_native.so` | Self-contained `rasamicd` zip |
| linux-arm64 | `librasamic_native.so` | Self-contained `rasamicd` zip |
| osx-x64 | `librasamic_native.dylib` | Self-contained `rasamicd` zip |
| osx-arm64 | `librasamic_native.dylib` | Self-contained `rasamicd` zip |

The workflow uses `macos-26` for arm64 and `macos-15-intel` for x64. The retired
`macos-14` jobs were cancelled before executing any steps.

## Local packaging

Build the matching native library first, then:

```sh
tools/scripts/package-release.sh win-x64 native/rasamic-native/build-win-x64/rasamic_native.dll 0.1.0
```

The zip contains the self-contained CLI, native library, MIT license,
third-party notices, and README.

Windows GUI installer:

```sh
dotnet tool install -g vpk
tools/scripts/package-velopack.sh win-x64 native/rasamic-native/build-win-x64/rasamic_native.dll 0.1.0
```

Output is under `dist/velopack-out-win-x64/`: Setup, full/delta packages, and a
`RELEASES` feed. `RASA_UPDATE_FEED` points installed builds at the feed; updates
download in the background and apply on the next clean exit.

## Platform signing

### Android

The project currently has no committed release-signing configuration. Create and
protect a release keystore outside the repository, configure CI secrets, and
record the signing certificate fingerprint. Never commit the keystore or passwords.

### iOS

An Apple Developer account, provisioning profiles, and a macOS runner are required.
The existing workflow proves only an unsigned build.

### Windows driver

CI can test-sign for a test-signing machine. Production users need a valid
production signature, normally WHQL/WHCP through the Hardware Dev Center. A
self-signed certificate trusted on one development PC is not a production
signing path and does not bypass Secure Boot.

## Post-release verification

1. Download every attached artifact and compute SHA-256 checksums.
2. Inspect archive paths and confirm the native library and licenses are present.
3. Install the Windows GUI package on a clean VM; verify update feed and uninstall.
4. Run the CLI from each server zip on its target OS/architecture.
5. Smoke-test pairing, stereo, reconnect, mute, and the advertised backend.
6. Verify the driver package separately on a test-signing VM and with Driver Verifier.
7. Publish release notes that distinguish verified platforms from compile-only or
   pending platforms.

## Rollback

- Keep the previous release and Velopack feed available.
- Do not delete old tags or artifacts as part of a normal release.
- If a release is bad, stop promoting the feed, publish a fixed patch release, and
  document the rollback/upgrade path. Driver rollback may require removing the
  device/driver package and reinstalling the previous signed package.
