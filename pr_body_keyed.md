### What

Follow-up to the review notes on #1030 and #1031: the keyed gap-fill recovery now lives in **one shared helper**, and a re-asked run is sent back **in slot order**.

### 1. The ordering fix (#1031 note 1)

When a run came back empty, `DocumentTranslator.ApplyRuns` removed its index from `missing` and added it again **at the end of the list**, so the next keyed request listed the runs out of slot order (`<s0>…<s3>…<s1>…`) while `Keyed`'s comment promised "in slot order". The model reads the runs as one text, so the sentence it is asked to translate was scrambled.

The shared helper asks for the missing texts **sorted by slot index on every round**, and a test pins the exact request (`<s1>Beta.</s1>\n<s3>Delta.</s3>`) for a first reply that answers `s1` with an empty marker and drops `s3` entirely — the case that produced the scrambled order before.

### 2. The shared helper (#1031 note 2)

`SubtitleTranslator.TranslateTextsAsync` and `DocumentTranslator`'s parse/re-ask/fallback block were the same algorithm drifting apart in two files. `KeyedTranslation.TranslateAsync` (new, ~90 lines replacing ~160 across the two callers) now owns all of it:

- one keyed request (`<sN>…</sN>`, one per line),
- answers that came back are kept; a later duplicate never overwrites an earlier one,
- re-ask **only the missing ones**, sorted, one request per round, up to `MaxRecoveryRounds = 4`,
- stop the moment a round recovers nothing,
- an answer with no visible text counts as a miss, so a run can never be blanked,
- markers echoed inside the content are stripped, and returned texts are trimmed,
- a text that never comes back stays `null`, and each caller applies its own fallback as before: `DocumentTranslator` translates that run on its own (so no text stays in the source language), `SubtitleTranslator` keeps the source cue.

Net effect on behaviour beyond the ordering fix: subtitle cues now also get the **empty-answer retry** the document path had (previously an empty cue answer went straight to "keep source text" without being re-asked), and the document path's keyed requests now separate runs with a newline — the same shape the merged subtitle PR shipped and was reviewed with. Everything else, including the strictly-local Ollama path, is untouched.

### 3. Wording (#1030 note)

The one remaining "byte-for-byte" in the test header (the writer rewrites the timestamps, so "unchanged" is the accurate word) is reworded.

### Tests

`TranslationTests` gains the slot-order test above and its flaky mock can now answer a specific slot with an empty marker while omitting another (the mixed case the ordering bug needs). `TranslationTests` 17/17, `SubtitleTranslationTests` 6/6, `TranslationIntegrationTests` green — 28/28 across the three.

Also removes the `CS1734` paramref warning on the subtitle test mock's doc comment.

Full `Filee.Engines.Tests`: same as main apart from the translations — the failures are the pre-existing FFmpeg/LibreOffice environment ones (`Unknown encoder 'libspeex'`, LibreOffice paths), all in Media/Spreadsheet tests, none in translation.

Refs #1030, #1031, #1023.
