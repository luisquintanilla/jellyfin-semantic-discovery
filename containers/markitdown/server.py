#!/usr/bin/env python3
"""Narrow HTTP/JSON-RPC wrapper around the official ``markitdown`` package.

This module intentionally uses ONLY the Python standard library for the HTTP
transport (``http.server``) so the container's runtime surface is limited to:

  * the standard library, and
  * the hash-pinned third-party packages listed in ``requirements.lock.txt``
    (``markitdown[pdf]`` and its transitive dependencies).

Why a hand-rolled wrapper instead of the official ``markitdown-mcp`` package?
See ``README.md`` in this directory for the full rationale. In short:
``markitdown-mcp`` was published as an alpha release (0.0.1a4) at the time
this container was built, and pulls in the (also fast-moving) ``mcp`` SDK and
its dependency tree. To keep this container reproducible and narrowly scoped,
this server instead exposes:

  * ``GET  /health`` - a trivial liveness/readiness probe.
  * ``POST /mcp``     - a minimal JSON-RPC 2.0 endpoint that implements just
                        enough of the Model Context Protocol (MCP) request
                        shape (``initialize``, ``tools/list``,
                        ``tools/call``) for a client to discover and invoke a
                        single tool, ``convert_to_markdown``, which converts
                        a PDF/EPUB (or any other MarkItDown-supported input)
                        to Markdown text using ``markitdown.MarkItDown``.

This is a narrow, purpose-built shim - not a general-purpose MCP server
implementation - and is documented as such in the accompanying README.
"""

from __future__ import annotations

import json
import logging
import os
import resource
import socket
import sys
import base64
import binascii
import hashlib
import io
import posixpath
import stat
import tempfile
import threading
import urllib.parse
import xml.parsers.expat
import xml.etree.ElementTree
import zipfile
import zlib
from defusedxml import minidom as safe_minidom
from defusedxml.common import DefusedXmlException
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

class _MaximumLevelFilter(logging.Filter):
    """Allow records up to and including one level."""

    def __init__(self, maximum_level: int) -> None:
        super().__init__()
        self._maximum_level = maximum_level

    def filter(self, record: logging.LogRecord) -> bool:
        return record.levelno <= self._maximum_level


_log_formatter = logging.Formatter(
    "%(asctime)s %(levelname)s %(name)s: %(message)s"
)
_stdout_handler = logging.StreamHandler(sys.stdout)
_stdout_handler.setLevel(os.environ.get("LOG_LEVEL", "INFO").upper())
_stdout_handler.addFilter(_MaximumLevelFilter(logging.INFO))
_stdout_handler.setFormatter(_log_formatter)
_stderr_handler = logging.StreamHandler(sys.stderr)
_stderr_handler.setLevel(logging.WARNING)
_stderr_handler.setFormatter(_log_formatter)
logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO").upper(),
    handlers=[_stdout_handler, _stderr_handler],
)
logger = logging.getLogger("markitdown-mcp-wrapper")

SERVER_NAME = "markitdown-http-mcp-wrapper"
SERVER_VERSION = "0.1.0"
TOOL_NAME = "convert_to_markdown"
ABSOLUTE_MAX_DOCUMENT_BYTES = 32 * 1024 * 1024
ABSOLUTE_MAX_REQUEST_BYTES = 48 * 1024 * 1024
EPUB_CONTAINER_PATH = "META-INF/container.xml"
EPUB_CONTAINER_NAMESPACE = "urn:oasis:names:tc:opendocument:xmlns:container"
MAX_EPUB_CONTAINER_BYTES = 64 * 1024
MAX_DOCUMENT_BYTES = int(os.environ.get("MAX_DOCUMENT_BYTES", str(8 * 1024 * 1024)))
MAX_REQUEST_BYTES = int(os.environ.get("MAX_REQUEST_BYTES", str(12 * 1024 * 1024)))
MAX_CONCURRENT_CONVERSIONS = int(os.environ.get("MAX_CONCURRENT_CONVERSIONS", "2"))
BODY_READ_TIMEOUT_SECONDS = float(os.environ.get("BODY_READ_TIMEOUT_SECONDS", "5"))
HEADER_READ_TIMEOUT_SECONDS = float(os.environ.get("HEADER_READ_TIMEOUT_SECONDS", "5"))
if not 0 < MAX_DOCUMENT_BYTES <= ABSOLUTE_MAX_DOCUMENT_BYTES:
    raise RuntimeError("MAX_DOCUMENT_BYTES is outside the hard safety limit")
if not MAX_DOCUMENT_BYTES < MAX_REQUEST_BYTES <= ABSOLUTE_MAX_REQUEST_BYTES:
    raise RuntimeError("MAX_REQUEST_BYTES is outside the hard safety limit")
_conversion_slots = threading.BoundedSemaphore(MAX_CONCURRENT_CONVERSIONS)
_process_start_peak_rss_kib = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss

# JSON-RPC 2.0 standard error codes.
PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603
CAPACITY_UNAVAILABLE = -32001
CONVERSION_FAILED = -32002

# Lazily constructed on first use so `--help`/import-time syntax checks never
# require the (potentially heavy, native-extension-backed) MarkItDown import
# to succeed.
_markitdown_lock = threading.Lock()
_markitdown_instance: Any = None
_original_create_connection = socket.create_connection
_original_getaddrinfo = socket.getaddrinfo
_original_gethostbyname = socket.gethostbyname
_original_gethostbyname_ex = socket.gethostbyname_ex
_original_gethostbyaddr = socket.gethostbyaddr
_original_getnameinfo = socket.getnameinfo
_original_socket_connect = socket.socket.connect
_original_socket_connect_ex = socket.socket.connect_ex
_original_socket_sendto = socket.socket.sendto
_original_socket_sendmsg = socket.socket.sendmsg


class SourceValidationError(Exception):
    """Caller-controlled source validation failed before conversion."""


class DeterministicConversionError(Exception):
    """The pinned converter rejected these exact document bytes."""


class ConversionInfrastructureError(Exception):
    """Conversion failed for an environmental or server-side reason."""


def _is_loopback_host(host: Any) -> bool:
    return str(host).lower() in {"localhost", "127.0.0.1", "::1"}


def _install_egress_guard() -> None:
    """Deny Python-level outbound resolution and socket use except loopback."""

    def require_loopback_address(address: Any) -> None:
        if not isinstance(address, tuple) or not _is_loopback_host(address[0]):
            raise OSError("outbound network access is disabled")

    def guarded_getaddrinfo(host: Any, *args: Any, **kwargs: Any) -> Any:
        if not _is_loopback_host(host):
            raise OSError("outbound network access is disabled")
        return _original_getaddrinfo(host, *args, **kwargs)

    def guarded_create_connection(address: Any, *args: Any, **kwargs: Any) -> Any:
        require_loopback_address(address)
        return _original_create_connection(address, *args, **kwargs)

    def guarded_gethostbyname(host: Any) -> Any:
        if not _is_loopback_host(host):
            raise OSError("outbound network access is disabled")
        return _original_gethostbyname(host)

    def guarded_gethostbyname_ex(host: Any) -> Any:
        if not _is_loopback_host(host):
            raise OSError("outbound network access is disabled")
        return _original_gethostbyname_ex(host)

    def guarded_gethostbyaddr(host: Any) -> Any:
        if not _is_loopback_host(host):
            raise OSError("outbound network access is disabled")
        return _original_gethostbyaddr(host)

    def guarded_getnameinfo(address: Any, flags: int) -> Any:
        require_loopback_address(address)
        return _original_getnameinfo(address, flags)

    def guarded_socket_connect(
        sock: socket.socket,
        address: Any,
    ) -> Any:
        require_loopback_address(address)
        return _original_socket_connect(sock, address)

    def guarded_socket_connect_ex(
        sock: socket.socket,
        address: Any,
    ) -> Any:
        require_loopback_address(address)
        return _original_socket_connect_ex(sock, address)

    def guarded_socket_sendto(
        sock: socket.socket,
        data: Any,
        *args: Any,
    ) -> Any:
        if not args:
            raise TypeError("sendto requires a destination address")
        require_loopback_address(args[-1])
        return _original_socket_sendto(sock, data, *args)

    def guarded_socket_sendmsg(
        sock: socket.socket,
        buffers: Any,
        *args: Any,
    ) -> Any:
        if not args or not isinstance(args[-1], tuple):
            raise OSError(
                "unconnected sendmsg without a loopback destination is disabled"
            )
        require_loopback_address(args[-1])
        return _original_socket_sendmsg(sock, buffers, *args)

    socket.getaddrinfo = guarded_getaddrinfo
    socket.create_connection = guarded_create_connection
    socket.gethostbyname = guarded_gethostbyname
    socket.gethostbyname_ex = guarded_gethostbyname_ex
    socket.gethostbyaddr = guarded_gethostbyaddr
    socket.getnameinfo = guarded_getnameinfo
    socket.socket.connect = guarded_socket_connect
    socket.socket.connect_ex = guarded_socket_connect_ex
    socket.socket.sendto = guarded_socket_sendto
    socket.socket.sendmsg = guarded_socket_sendmsg


def _verify_egress_guard() -> dict[str, bool]:
    """Exercise every guarded outbound primitive against a public address."""
    target = ("93.184.216.34", 443)
    blocked: dict[str, bool] = {}

    probes = {
        "getaddrinfo": lambda: socket.getaddrinfo("example.com", 443),
        "gethostbyname": lambda: socket.gethostbyname("example.com"),
        "gethostbyname_ex": lambda: socket.gethostbyname_ex("example.com"),
        "gethostbyaddr": lambda: socket.gethostbyaddr("93.184.216.34"),
        "getnameinfo": lambda: socket.getnameinfo(target, 0),
        "create_connection": lambda: socket.create_connection(target, timeout=0.1),
    }
    for name, probe in probes.items():
        try:
            probe()
        except OSError:
            blocked[name] = True
        else:
            blocked[name] = False

    for name, operation, socket_type in (
        ("socket_connect", lambda sock: sock.connect(target), socket.SOCK_STREAM),
        ("socket_connect_ex", lambda sock: sock.connect_ex(target), socket.SOCK_STREAM),
        ("socket_sendto", lambda sock: sock.sendto(b"x", target), socket.SOCK_DGRAM),
        (
            "socket_sendmsg",
            lambda sock: sock.sendmsg([b"x"], [], 0, target),
            socket.SOCK_DGRAM,
        ),
    ):
        candidate = socket.socket(socket.AF_INET, socket_type)
        try:
            try:
                operation(candidate)
            except OSError:
                blocked[name] = True
            else:
                blocked[name] = False
        finally:
            candidate.close()

    return blocked


def _get_markitdown() -> Any:
    """Return a process-wide ``MarkItDown`` instance, creating it on first use."""
    global _markitdown_instance
    with _markitdown_lock:
        if _markitdown_instance is None:
            from markitdown import MarkItDown  # imported lazily, see docstring above

            # enable_plugins=False keeps behavior deterministic and avoids
            # pulling in optional third-party MarkItDown plugins that are not
            # part of the hash-locked dependency set.
            _markitdown_instance = MarkItDown(enable_plugins=False)
        return _markitdown_instance


def _decode_source(source: str) -> tuple[bytes, str, str, str]:
    """Validate and decode a bounded PDF/EPUB data URI."""
    if not isinstance(source, str) or not source.strip():
        raise SourceValidationError("'source' must be a non-empty base64 data URI")

    parsed = urllib.parse.urlparse(source)
    if parsed.scheme != "data":
        raise SourceValidationError("only base64 data URI inputs are allowed")

    header, separator, payload = source.partition(",")
    if not separator or ";base64" not in header.lower():
        raise SourceValidationError("data URI must contain a base64 payload")
    try:
        document = base64.b64decode(payload, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise SourceValidationError("data URI contains invalid base64") from exc
    if len(document) > MAX_DOCUMENT_BYTES:
        raise SourceValidationError(
            f"document exceeds the {MAX_DOCUMENT_BYTES}-byte conversion limit"
        )

    media_type = header[5:].split(";", 1)[0].lower()
    suffix = {
        "application/pdf": ".pdf",
        "application/epub+zip": ".epub",
    }.get(media_type)
    if suffix is None:
        raise SourceValidationError(f"unsupported data URI media type: {media_type!r}")

    digest = hashlib.sha256(document).hexdigest()
    return document, suffix, media_type, digest


def _convert_source(source: str) -> str:
    """Convert a bounded PDF/EPUB data URI to Markdown.

    Returns the Markdown text produced by MarkItDown. Raises ``ValueError``
    for obviously invalid input and lets MarkItDown's own exceptions
    propagate for conversion failures (the caller maps these to a JSON-RPC
    error response).
    """
    document, suffix, media_type, digest = _decode_source(source)
    before_peak_rss_kib = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    before_current_rss_kib = _current_rss_kib()
    try:
        if suffix == ".epub":
            _validate_epub_structure(document)
        with tempfile.NamedTemporaryFile(suffix=suffix) as temporary:
            temporary.write(document)
            temporary.flush()
            md = _get_markitdown()
            result = md.convert(temporary.name)
    except DeterministicConversionError:
        logger.error(
            "conversion failed source_kind=data media_type=%s decoded_size=%d "
            "sha256=%s exception_type=DeterministicConversionError",
            media_type,
            len(document),
            digest,
        )
        raise
    except Exception as exc:
        logger.error(
            "conversion failed source_kind=data media_type=%s decoded_size=%d sha256=%s exception_type=%s",
            media_type,
            len(document),
            digest,
            type(exc).__name__,
        )
        if _is_deterministic_conversion_exception(exc):
            raise DeterministicConversionError(
                "the converter rejected the document"
            ) from exc
        raise ConversionInfrastructureError(
            "conversion infrastructure failed"
        ) from exc

    after_peak_rss_kib = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    logger.info(
        "conversion completed source_kind=data media_type=%s decoded_size=%d "
        "sha256=%s process=markitdown-python rss_units=kib "
        "rss_metric=process_lifetime_high_water "
        "process_start_peak_rss_kib=%d before_peak_rss_kib=%d "
        "before_current_rss_kib=%d after_peak_rss_kib=%d "
        "after_current_rss_kib=%d",
        media_type,
        len(document),
        digest,
        _process_start_peak_rss_kib,
        before_peak_rss_kib,
        before_current_rss_kib,
        after_peak_rss_kib,
        _current_rss_kib(),
    )
    return result.text_content


def _validate_epub_structure(document: bytes) -> None:
    """Validate the bounded EPUB container and rootfile before conversion."""
    try:
        with zipfile.ZipFile(io.BytesIO(document), "r") as archive:
            container_entries = [
                entry
                for entry in archive.infolist()
                if entry.filename == EPUB_CONTAINER_PATH
            ]
            if len(container_entries) != 1:
                raise DeterministicConversionError(
                    "EPUB must contain exactly one META-INF/container.xml"
                )
            container = container_entries[0]
            if (
                not _is_regular_zip_entry(container)
                or container.flag_bits & 0x1
                or container.file_size <= 0
                or container.file_size > MAX_EPUB_CONTAINER_BYTES
                or container.compress_size <= 0
                or container.file_size > container.compress_size * 100
            ):
                raise DeterministicConversionError(
                    "EPUB container.xml is not a regular bounded entry"
                )

            with archive.open(container, "r") as source:
                container_xml = source.read(MAX_EPUB_CONTAINER_BYTES + 1)
                if (
                    len(container_xml) != container.file_size
                    or len(container_xml) > MAX_EPUB_CONTAINER_BYTES
                    or source.read(1)
                ):
                    raise DeterministicConversionError(
                        "EPUB container.xml exceeded its declared bound"
                    )

            try:
                parsed = safe_minidom.parseString(container_xml)
            except (
                DefusedXmlException,
                xml.parsers.expat.ExpatError,
            ) as exc:
                raise DeterministicConversionError(
                    "EPUB container.xml is malformed"
                ) from exc
            try:
                rootfiles = parsed.getElementsByTagNameNS(
                    EPUB_CONTAINER_NAMESPACE,
                    "rootfile",
                )
                if not rootfiles:
                    raise DeterministicConversionError(
                        "EPUB container.xml has no rootfile"
                    )
                entries_by_name: dict[str, list[zipfile.ZipInfo]] = {}
                for entry in archive.infolist():
                    entries_by_name.setdefault(entry.filename, []).append(entry)
                for rootfile in rootfiles:
                    full_path = rootfile.getAttribute("full-path")
                    if not _is_safe_epub_entry_path(full_path):
                        raise DeterministicConversionError(
                            "EPUB rootfile path is unsafe"
                        )
                    matches = entries_by_name.get(full_path, [])
                    if (
                        len(matches) != 1
                        or not _is_regular_zip_entry(matches[0])
                        or matches[0].flag_bits & 0x1
                    ):
                        raise DeterministicConversionError(
                            "EPUB rootfile entry is missing or unsafe"
                        )
            finally:
                parsed.unlink()
    except DeterministicConversionError:
        raise
    except (
        zipfile.BadZipFile,
        zipfile.LargeZipFile,
        NotImplementedError,
        EOFError,
        zlib.error,
    ) as exc:
        raise DeterministicConversionError(
            "EPUB ZIP structure is invalid"
        ) from exc
    except (OSError, MemoryError) as exc:
        raise ConversionInfrastructureError(
            "EPUB structural validation infrastructure failed"
        ) from exc


def _is_regular_zip_entry(entry: zipfile.ZipInfo) -> bool:
    mode = (entry.external_attr >> 16) & 0xFFFF
    file_type = stat.S_IFMT(mode)
    return not entry.is_dir() and file_type in (0, stat.S_IFREG)


def _is_safe_epub_entry_path(value: str) -> bool:
    if (
        not isinstance(value, str)
        or not value
        or value.startswith("/")
        or value.startswith("\\")
        or "\\" in value
        or any(ord(character) < 32 for character in value)
    ):
        return False
    parts = value.split("/")
    return (
        all(part not in ("", ".", "..") for part in parts)
        and posixpath.normpath(value) == value
    )


def _is_deterministic_conversion_exception(error: Exception) -> bool:
    """Require every wrapped converter failure to be a known parser rejection."""
    try:
        from markitdown import FileConversionException
        from pdfminer.pdfparser import PDFSyntaxError
    except Exception:
        return False
    if not isinstance(error, FileConversionException):
        return False

    attempts = getattr(error, "attempts", None)
    if not isinstance(attempts, list) or not attempts:
        return False
    deterministic_types = (
        PDFSyntaxError,
        zipfile.BadZipFile,
        xml.etree.ElementTree.ParseError,
        KeyError,
        UnicodeError,
    )
    for attempt in attempts:
        exc_info = getattr(attempt, "exc_info", None)
        if (
            not isinstance(exc_info, tuple)
            or len(exc_info) < 2
            or not isinstance(exc_info[1], deterministic_types)
        ):
            return False
    return True


def _current_rss_kib() -> int:
    """Read current Linux process RSS without logging procfs contents."""
    try:
        with open("/proc/self/status", encoding="ascii") as status:
            for line in status:
                if line.startswith("VmRSS:"):
                    return int(line.split()[1])
    except (OSError, ValueError, IndexError):
        return -1
    return -1


def _jsonrpc_result(request_id: Any, result: Any) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def _jsonrpc_error(request_id: Any, code: int, message: str, data: Any = None) -> dict[str, Any]:
    error: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        error["data"] = data
    return {"jsonrpc": "2.0", "id": request_id, "error": error}


def _tool_definitions() -> list[dict[str, Any]]:
    return [
        {
            "name": TOOL_NAME,
            "description": (
                "Convert a bounded PDF or EPUB supplied as a base64 data URI "
                "to Markdown text."
            ),
            "inputSchema": {
                "type": "object",
                "properties": {
                    "source": {
                        "type": "string",
                        "pattern": "^data:(application/pdf|application/epub\\+zip);base64,",
                        "description": "A bounded PDF or EPUB base64 data URI.",
                    }
                },
                "required": ["source"],
            },
        }
    ]


def _handle_initialize(request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
    # Echo back whatever protocolVersion the client requested rather than
    # asserting a single hard-coded MCP protocol version: this is a narrow
    # shim, not a full MCP server implementation, and the exact conversion
    # contract (tools/list + tools/call) is stable across recent protocol
    # revisions. See README.md for details on this simplification.
    requested_version = None
    if isinstance(params, dict):
        requested_version = params.get("protocolVersion")

    result = {
        "protocolVersion": requested_version or "2024-11-05",
        "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        "capabilities": {"tools": {}},
    }
    return _jsonrpc_result(request_id, result)


def _handle_tools_list(request_id: Any) -> dict[str, Any]:
    return _jsonrpc_result(request_id, {"tools": _tool_definitions()})


def _handle_tools_call(request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(params, dict):
        return _jsonrpc_error(request_id, INVALID_PARAMS, "params must be an object")

    name = params.get("name")
    arguments = params.get("arguments") or {}

    if name != TOOL_NAME:
        return _jsonrpc_error(request_id, METHOD_NOT_FOUND, f"unknown tool: {name!r}")

    if not isinstance(arguments, dict):
        return _jsonrpc_error(request_id, INVALID_PARAMS, "arguments must be an object")

    source = arguments.get("source")
    if not _conversion_slots.acquire(blocking=False):
        return _jsonrpc_error(
            request_id,
            CAPACITY_UNAVAILABLE,
            "conversion capacity exhausted",
        )
    try:
        markdown_text = _convert_source(source)
    except SourceValidationError as exc:
        return _jsonrpc_error(request_id, INVALID_PARAMS, str(exc))
    except DeterministicConversionError:
        return _jsonrpc_error(request_id, CONVERSION_FAILED, "conversion failed")
    except ConversionInfrastructureError:
        return _jsonrpc_error(request_id, INTERNAL_ERROR, "conversion infrastructure failed")
    finally:
        _conversion_slots.release()

    result = {
        "content": [{"type": "text", "text": markdown_text}],
        "isError": False,
    }
    return _jsonrpc_result(request_id, result)


def _dispatch(payload: dict[str, Any]) -> dict[str, Any] | None:
    if not isinstance(payload, dict) or payload.get("jsonrpc") != "2.0":
        return _jsonrpc_error(payload.get("id") if isinstance(payload, dict) else None, INVALID_REQUEST, "invalid JSON-RPC 2.0 request")

    method = payload.get("method")
    request_id = payload.get("id")
    params = payload.get("params") or {}
    is_notification = "id" not in payload

    if method == "initialize":
        response = _handle_initialize(request_id, params)
    elif method == "initialized" or method == "notifications/initialized":
        return None  # notification only, no response body expected
    elif method == "tools/list":
        response = _handle_tools_list(request_id)
    elif method == "tools/call":
        response = _handle_tools_call(request_id, params)
    else:
        response = _jsonrpc_error(request_id, METHOD_NOT_FOUND, f"unknown method: {method!r}")

    return None if is_notification else response


class MarkItDownMcpHandler(BaseHTTPRequestHandler):
    server_version = f"{SERVER_NAME}/{SERVER_VERSION}"

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(HEADER_READ_TIMEOUT_SECONDS)

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - stdlib signature
        logger.info("client=%s http_event", self.client_address[0])

    def log_request(self, code: int | str = "-", size: int | str = "-") -> None:
        path = urllib.parse.urlparse(getattr(self, "path", "")).path
        logger.info(
            "client=%s method=%s path=%s status=%s size=%s",
            self.client_address[0],
            getattr(self, "command", "-"),
            path,
            code,
            size,
        )

    def _write_json(self, status: HTTPStatus, payload: dict[str, Any] | None) -> None:
        body = b"" if payload is None else json.dumps(payload).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if body:
                self.wfile.write(body)
        except OSError as exc:
            logger.warning(
                "response write failed exception_type=%s",
                type(exc).__name__,
            )

    def do_GET(self) -> None:  # noqa: N802 - stdlib method name
        path = urllib.parse.urlparse(self.path).path
        if path == "/health":
            self._write_json(HTTPStatus.OK, {"status": "ok", "service": SERVER_NAME, "version": SERVER_VERSION})
        elif path == "/egress-canary":
            blocked = _verify_egress_guard()
            status = (
                HTTPStatus.OK
                if blocked and all(blocked.values())
                else HTTPStatus.INTERNAL_SERVER_ERROR
            )
            self._write_json(
                status,
                {
                    "blocked": all(blocked.values()),
                    "probes": blocked,
                },
            )
        else:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802 - stdlib method name
        parsed_path = urllib.parse.urlparse(self.path)
        path = parsed_path.path
        if path != "/mcp":
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            return
        if parsed_path.query:
            self._write_json(
                HTTPStatus.BAD_REQUEST,
                _jsonrpc_error(None, INVALID_REQUEST, "query strings are not allowed"),
            )
            return

        transfer_encoding = self.headers.get("Transfer-Encoding")
        lengths = self.headers.get_all("Content-Length", [])
        if transfer_encoding is not None or len(lengths) != 1:
            self._write_json(
                HTTPStatus.LENGTH_REQUIRED,
                _jsonrpc_error(None, INVALID_REQUEST, "a bounded Content-Length is required"),
            )
            return
        raw_length = lengths[0]
        try:
            length = int(raw_length, 10)
        except (TypeError, ValueError):
            self._write_json(
                HTTPStatus.BAD_REQUEST,
                _jsonrpc_error(None, INVALID_REQUEST, "Content-Length must be a non-negative integer"),
            )
            return
        if length < 0:
            self._write_json(
                HTTPStatus.BAD_REQUEST,
                _jsonrpc_error(None, INVALID_REQUEST, "Content-Length must be a non-negative integer"),
            )
            return
        if length > MAX_REQUEST_BYTES:
            self._write_json(
                HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                _jsonrpc_error(None, INVALID_REQUEST, "request body exceeds configured limit"),
            )
            return
        self.connection.settimeout(BODY_READ_TIMEOUT_SECONDS)
        try:
            raw_body = self.rfile.read(length) if length > 0 else b""
        except (TimeoutError, OSError) as exc:
            logger.warning("request body read failed exception_type=%s", type(exc).__name__)
            try:
                self._write_json(
                    HTTPStatus.REQUEST_TIMEOUT,
                    _jsonrpc_error(None, INVALID_REQUEST, "request body read timed out"),
                )
            except OSError:
                pass
            return
        if len(raw_body) != length:
            self._write_json(
                HTTPStatus.BAD_REQUEST,
                _jsonrpc_error(None, INVALID_REQUEST, "request body was truncated"),
            )
            return
        logger.info("request body accepted body_bytes=%d", length)

        try:
            payload = json.loads(raw_body) if raw_body else {}
        except json.JSONDecodeError as exc:
            self._write_json(HTTPStatus.BAD_REQUEST, _jsonrpc_error(None, PARSE_ERROR, f"invalid JSON: {exc}"))
            return

        try:
            response = _dispatch(payload)
        except Exception as exc:  # noqa: BLE001 - last-resort guard, always return JSON-RPC shape
            logger.error(
                "unhandled dispatch failure exception_type=%s",
                type(exc).__name__,
            )
            response = _jsonrpc_error(
                payload.get("id") if isinstance(payload, dict) else None,
                INTERNAL_ERROR,
                "internal server error",
            )

        if response is None:
            # Notification: no content, per JSON-RPC 2.0.
            self._write_json(HTTPStatus.ACCEPTED, None)
        else:
            self._write_json(HTTPStatus.OK, response)


class SanitizedThreadingHTTPServer(ThreadingHTTPServer):
    def handle_error(self, request: Any, client_address: Any) -> None:
        exception = sys.exception()
        logger.error(
            "request handler failed client=%s exception_type=%s",
            client_address[0],
            type(exception).__name__ if exception is not None else "Unknown",
        )


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    host = os.environ.get("HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8080"))

    httpd = SanitizedThreadingHTTPServer((host, port), MarkItDownMcpHandler)
    _install_egress_guard()
    logger.info("%s v%s listening on http://%s:%d", SERVER_NAME, SERVER_VERSION, host, port)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        logger.info("shutting down")
    finally:
        httpd.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
