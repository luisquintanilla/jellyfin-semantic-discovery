# ServiceDefaults

The standard Aspire service defaults: OpenTelemetry (logs, metrics, traces), health
checks at `/health` and `/alive`, service discovery, and HTTP resilience.
[Extensions.cs](Extensions.cs) is the stock template, trimmed. The one change: HTTP
timeouts are raised to 2 minutes per attempt, because loading a model or reading a page
with a vision model takes longer than the 10-second web default.

The semantic service calls `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`.
The chat and embedding clients emit OpenTelemetry, so their calls show up in the Aspire
dashboard.
