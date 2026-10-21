# Compatibility and troubleshooting evidence

## Jellyfin 12 plugin loading

This plugin targets `net10.0` and the exact `Jellyfin.Controller`,
`Jellyfin.Model`, and `Jellyfin.Database.Implementations` version `12.0.0`.
Jellyfin 12 discovers exported concrete provider and scheduled-task types, then
constructs them from the root service provider. The plugin therefore keeps those
types public and uses `IPluginServiceRegistrator` only for their singleton
dependencies.

The first container smoke exposed a Jellyfin 12 plugin-loading constraint:
putting private managed dependencies beside the plugin DLL is insufficient.
`PluginLoadContext` is collectible, but its `AssemblyDependencyResolver` is
created from the plugin directory. The dependencies were not resolved and
service registration failed on `Microsoft.ML.OnnxRuntime`.

The verified fix is:

- list every plugin-private managed dependency in `meta.json` `assemblies`;
- do not list or ship host-owned Jellyfin, Microsoft.Extensions host
  infrastructure, System.Text.Json, or Entity Framework assemblies;
- copy `libonnxruntime.so` and `libonnxruntime_providers_shared.so` flat beside
  the plugin DLL for the Linux Jellyfin container (and equivalent Windows DLLs
  for local smoke runs);
- install a `NativeLibrary.SetDllImportResolver` before the first ORT call.

The resolver accepts only the expected ORT import basenames and throws when a
plugin-local native is missing. It does not fall back to an ambient/global
native installation.

`scripts/Smoke-Plugin.ps1` builds the complete graph, checks the pinned Python
reference vector and pairwise similarities, boots the digest-pinned Jellyfin 12
image twice with fresh then persisted configuration, and requires an in-process
Linux ONNX success marker. It also runs a missing-`libonnxruntime.so` negative
control and requires the corresponding failure marker, so a host-only Windows
inference cannot mask a broken plugin deployment.

The pinned tokenizer uses BERT basic tokenization with lower-casing and accent
stripping enabled. Its effective options are part of the tokenizer identity hash,
so changing normalization invalidates old snapshots.

## API limitations kept explicit

- Internal search results are discarded by Jellyfin when any external search
  provider returns results. Semantic Discovery remains an internal provider so
  it does not suppress built-in SQL results.
- The plugin uses its own deterministic SRT/WebVTT parser because Jellyfin's
  `ISubtitleParser` lives in the non-packaged `MediaBrowser.MediaEncoding`
  assembly.
- MarkItDown publishes an MCP package and source Dockerfile, but no official
  versioned Microsoft container. This repository builds a private pinned image.
- Similar-item support for `Book` is server-native in Jellyfin 12. Stock
  `jellyfin-web` v12 hard-codes its More Like This item-type allowlist without
  `Book` and returns before requesting `/Items/{id}/Similar`. Book is therefore
  API-only in this demo; Movie and Series are the native UI acceptance surface.
- Jellyfin does not apply `ISimilarItemsProvider.CacheDuration` to local
  providers. Movie, Series, and Book recommendations are recomputed against the
  active in-memory snapshot; the plugin does not claim host-side caching.

Primary source pins and links are maintained in the root README manifest.

## Mount and conversion boundaries

Jellyfin 12 opens and rewrites the installed plugin `meta.json` while loading
the plugin. A read-only plugin-directory bind caused a verified startup failure,
so the AppHost grants write access only to that scoped plugin directory. Model
and media binds remain read-only.

MarkItDown is internal plaintext HTTP on the private Aspire network. Redirects
and ambient proxies are disabled, and only bounded PDF/EPUB data URIs are
accepted. Socket settings are idle timeouts, not total elapsed deadlines, and a
synchronous parser invocation cannot be killed by `HttpClient.Timeout`; see the
root README for the complete privacy/resource-limit statement.
