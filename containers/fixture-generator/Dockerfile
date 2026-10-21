# syntax=docker/dockerfile:1@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04@sha256:042240d57ec9e47e511033b92625a8d95875ee5860af3015992c248b58a8be81

ARG FFMPEG_VERSION=7:6.1.1-3ubuntu5
ARG UBUNTU_SNAPSHOT=20250201T000000Z

RUN printf '%s\n' \
        'Types: deb' \
        "URIs: https://snapshot.ubuntu.com/ubuntu/${UBUNTU_SNAPSHOT}/" \
        'Suites: noble noble-updates noble-security' \
        'Components: main universe restricted multiverse' \
        'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg' \
        > /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get -o Acquire::Check-Valid-Until=false update \
    && apt-get install --yes --no-install-recommends "ffmpeg=${FFMPEG_VERSION}" \
    && rm -rf /var/lib/apt/lists/*

COPY containers/fixture-generator/toolchain-lock.json /opt/fixture-generator/toolchain-lock.json
COPY scripts/Generate-Fixtures.ps1 /opt/fixture-generator/Generate-Fixtures.ps1
COPY assets/fixtures/manifest.json /opt/fixture-generator/manifest.json
COPY assets/fixtures/SHA256SUMS /opt/fixture-generator/SHA256SUMS

WORKDIR /work

ENTRYPOINT ["pwsh", "-NoProfile", "-File", "/opt/fixture-generator/Generate-Fixtures.ps1"]
