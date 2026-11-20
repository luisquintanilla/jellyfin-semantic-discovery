# Demo library

`manifest.json` lists the demo library that Setup creates in Jellyfin: three
Movies, three Audio tracks, and four Books. Every file is checked in under
`library/` with its hash, metadata, authorship, and CC BY 4.0 terms.

- Movies and Audio keep their MP4/FLAC, VTT, and NFO sidecars. Artwork sits
  beside the media (`poster.jpg`, `backdrop.jpg`, `folder.jpg`), and each FLAC
  also embeds its `folder.jpg` cover so Jellyfin finds it during a scan.
- Books are PDFs with OPF metadata and a `<slug>.jpg` cover. Three have a
  normal text layer. **Retrieval Field Notes** is a scanned edition with images
  only, built by `scripts/New-ScannedBook.cs`, so the semantic service has to
  read it with a vision model.

`SHA256SUMS.txt` covers the book PDFs and `MEDIA-SHA256SUMS.txt` covers the
movie and audio files.