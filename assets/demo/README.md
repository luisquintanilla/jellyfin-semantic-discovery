# Demo asset delivery contract

`manifest.json` contains exactly three Movie, three Audio, and three Book
items. All nine are sealed and checked in with hashes, metadata, authorship,
and CC BY 4.0 terms.

The Movie and Audio release retains its byte-for-byte MP4/FLAC, VTT, and NFO
sidecars under the approved folder layout. The Book release contains three
eight-page PDFs plus OPF metadata. Approved artwork adds three Movie posters,
three Movie backdrops, three square Audio covers, and three Book covers,
byte-for-byte and hash-sealed in `manifest.json`.

Do not add prompts, storyboards, editable sources, media/PDF generators,
download caches, or production receipts here.
