# Container third-party notices

The HTTP wrapper source is MIT licensed by this repository. The built image as
a whole is **not** represented as MIT-only: it contains Debian, Python, and
Python packages under their respective licenses.

- Base image: `python:3.13-slim-bookworm` at the digest in `Dockerfile`.
  Debian package copyright files remain available under `/usr/share/doc`.
- MarkItDown `0.1.7`: MIT, <https://github.com/microsoft/markitdown>.
- Every installed Python package and version is recorded in
  `requirements.lock.txt`. Package-provided metadata and license files remain
  installed under Python's `site-packages/*-dist-info` directories.

Review the upstream license metadata before redistributing the built image.
