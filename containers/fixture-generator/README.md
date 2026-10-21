# Fixture generator environment

The canonical fixture corpus is generated only in this container. It pins the
Dockerfile frontend, the PowerShell base image digest, and an immutable Ubuntu
snapshot (`20250201T000000Z`) with an exact FFmpeg package version. Snapshot
index refreshes and package downloads use strict error handling and bounded
retries; the build fails closed rather than accepting a different live-archive
package closure. This is a locally built final image, not a pre-published
immutable image.
`scripts/Generate-Fixtures.ps1` verifies the complete installed package closure,
the complete `ldd` closure for ffmpeg/ffprobe, critical codec libraries
including `libx264.so.164`, and executable versions/SHA-256 values before any
generation or checksum update. The measured closure is recorded in
`toolchain-lock.json`.

The approved runtime identity is recorded in `assets/fixtures/manifest.json`:
PowerShell 7.5.0, FFmpeg/ffprobe 6.1.1-3ubuntu5, `ffmpeg` SHA-256
`ed16af623947494a72e284b6eb8ff225f2da22b38b5d5069c2fd4b4ba3384e41`,
and `ffprobe` SHA-256
`272f6ebc634a63d9c8b4ca68e964119d980f25154e5aa2c35e5487da48e9a58f`.
`-Mode UpdateChecksums` fails before writing when any value differs.

The GitHub-hosted runner remains a moving environment. CI records the runner,
Docker, .NET, and PowerShell versions in the job summary, then builds the final
generator from pinned inputs and rejects it unless its measured closure matches
the lock. Ambient runner FFmpeg is never part of the generator identity.
