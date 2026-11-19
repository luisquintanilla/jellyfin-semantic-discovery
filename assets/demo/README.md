# Demo asset delivery contract

`manifest.json` reserves exactly three Movie, three Audio, and three Book
delivery slots. The three eight-page PDF books are sealed and checked in with
their hashes, metadata, authorship, and CC BY 4.0 terms. Movie and Audio remain
pending.

Final Movie/Audio delivery replaces every remaining
`__DELIVERY_PENDING_*__` value, adds the declared MP4/FLAC, VTT, metadata, and
artwork files, and fills exact hashes, byte counts, authorship, and license
fields.

The complete demo is not release-ready while `deliveryStatus` is
`pending-final-assets`, any required hash is `null` or empty, or any declared
file is missing. Do not add prompts, storyboards, editable sources, media/PDF
generators, download caches, or production receipts here.
