# syntax=docker/dockerfile:1@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
#
# Private, reproducible MarkItDown MCP HTTP wrapper container.
#
# See README.md in this directory for:
#   * why this is a narrow HTTP/JSON-RPC wrapper around the official
#     `markitdown` package rather than the alpha `markitdown-mcp` package,
#   * how the base image digest below was verified,
#   * how to regenerate requirements.lock.txt.
#
# Base image pin (linux/amd64):
#   Verified live against the Docker Hub Registry v2 API (registry-1.docker.io)
#   and cross-checked against the Docker Hub v2 tags API
#   (hub.docker.com/v2/repositories/library/python/tags/3.13-slim-bookworm):
#     tag:    python:3.13-slim-bookworm
#     digest: sha256:2f2e5a876c71a6757f55ec57f2add0225ddaf01c802a33fcc29073943f94d907
#     pushed: 2026-09-01T02:14:04Z, status: active (confirmed via two independent queries)
#   NOTE: this tag is mutable and is periodically repushed (e.g. for base OS
#   security patches), so this digest WILL become stale over time - that is
#   expected and is exactly why the digest, not just the tag, is pinned
#   below. Re-run the verification steps in README.md and update this digest
#   deliberately when you want to move to a newer base image.
FROM python:3.13-slim-bookworm@sha256:2f2e5a876c71a6757f55ec57f2add0225ddaf01c802a33fcc29073943f94d907

LABEL org.opencontainers.image.title="markitdown-mcp-http-wrapper" \
      org.opencontainers.image.description="Narrow HTTP/JSON-RPC wrapper around the official markitdown package for PDF/EPUB-to-Markdown conversion"

# Do not run as root.
RUN groupadd --gid 10001 markitdown \
    && useradd --uid 10001 --gid markitdown --shell /usr/sbin/nologin --no-create-home markitdown

WORKDIR /app

# Install hash-pinned dependencies only. --require-hashes causes pip to
# refuse installation of anything not covered by a matching --hash entry in
# requirements.lock.txt, so this build fails closed if the lock file is
# incomplete or tampered with.
COPY requirements.lock.txt /app/requirements.lock.txt
RUN python -m pip install --no-cache-dir --require-hashes -r /app/requirements.lock.txt

COPY server.py /app/server.py
COPY THIRD-PARTY-NOTICES.md /licenses/THIRD-PARTY-NOTICES.md

ENV HOST=0.0.0.0 \
    PORT=8080 \
    HTTP_PROXY= \
    HTTPS_PROXY= \
    ALL_PROXY= \
    NO_PROXY=* \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

USER markitdown

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/health', timeout=3).status == 200 else 1)"

ENTRYPOINT ["python", "/app/server.py"]
