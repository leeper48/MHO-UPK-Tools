upk_checksums.json: CRC-32 of the 15,250 stock Marvel Heroes Omega packages (file name -> CRC32 hex), sorted by name.
Made by this project (--make-checksums, 2026-09-27) from a clean copy of the game's CookedPCConsole folder: every package
dated 2024-03-14 and compressed, as the stock files are. All 15,250 values are identical to the list MHModManager 1.0.1 shipped.
Used to verify that a package copy is an unmodified original before it is kept as one.

pck_checksums.json: CRC-32 of the 227 stock sound packages (.pck), same format. Made by this project (--make-pck-checksums,
2026-10-22) from the same clean copy: every one dated 2024-03-14 and an AKPK file. Lets a sound package restored by a Steam
verify (stock contents, new date) be kept as an original.

voice_text.tsv.gz: sound event ID (FNV hash of its name) -> the words said, one per line. The words come from
project_pck_index.json in MH_PCK_SoundSilencer by CorvaeOboro (CC0, speech-to-text), matched to sounds by Assets\make_voice_text.py
and to events by --voice-text-make (2026-10-31), both reading a clean copy of the game's .pck files.
