# Setup

A one-shot console app that Aspire runs after Jellyfin is healthy. It's demo plumbing,
not part of the AI story.

[JellyfinBootstrapper.cs](JellyfinBootstrapper.cs) uses Jellyfin's HTTP API to:

1. Finish the startup wizard and create the admin user (or sign in if it exists).
2. Create the three libraries from [assets/demo/manifest.json](../../assets/demo/manifest.json),
   with internet metadata off and the plugin as the "More Like This" provider.
3. Scan the libraries and wait until every demo item is there.
4. Run the plugin's **Sync semantic index** task and wait for it to finish.

Then [Program.cs](Program.cs) writes the login to `.data/jellyfin-login.json`.

## Artwork

All artwork ships beside the media, so Jellyfin finds it during the scan and Setup
doesn't upload any images. The songs carry their cover inside the FLAC, which Jellyfin
reads with its "Image Extractor" image fetcher, so Setup turns that fetcher on.

| Environment variable | |
| --- | --- |
| `JELLYFIN_URL`, `JELLYFIN_ADMIN_PASSWORD`, `JELLYFIN_LIBRARY_MANIFEST` | Required; Aspire sets them |
| `JELLYFIN_ADMIN_USER` | Default `semantic-admin` |
| `JELLYFIN_CREDENTIALS_PATH` | Where to write the login |
| `JELLYFIN_READY_TIMEOUT_SECONDS` | Default `600` |
