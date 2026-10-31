"""The first step of StockData/voice_text.tsv.gz: sound ID -> what's said. The app's --voice-text-make then turns it into
event ID -> what's said, the table the Voice tab reads.

The text comes from project_pck_index.json in MH_PCK_SoundSilencer by CorvaeOboro (CC0, speech-to-text by Vosk). Its files
are named <hex>_<n>.ogg, where the hex is the RIFF's position in the .pck (a made-up id), so each one is looked up in a clean
copy of the game's .pck files to get the sound's real ID: a stream table entry, or a bank's DIDX entry.

    python -I make_voice_text.py <project_pck_index.json> <clean CookedPCConsole> <sounds.tsv.gz>

Reads the game's files only.
"""
import gzip
import json
import os
import struct
import sys
from collections import Counter


def tables(data):
    """The .pck's bank and stream entries: (id, block size, size, start block, language)."""
    assert data[:4] == b"AKPK"
    lang, banks, streams = struct.unpack_from("<III", data, 12)

    def table(at):
        n = struct.unpack_from("<I", data, at)[0]
        return [struct.unpack_from("<5I", data, at + 4 + 20 * i) for i in range(n)]

    return table(28 + lang), table(28 + lang + banks)


def positions(data):
    """RIFF position in the .pck -> sound ID."""
    bank_entries, stream_entries = tables(data)
    out = {}
    for sid, block, _, start, _ in stream_entries:
        out[start * block] = sid
    for _, block, size, start, _ in bank_entries:
        at, end, didx, base = start * block, start * block + size, None, None
        while at + 8 <= end:
            tag, length = data[at:at + 4], struct.unpack_from("<I", data, at + 4)[0]
            if tag == b"DIDX":
                didx = (at + 8, length)
            elif tag == b"DATA":
                base = at + 8
            at += 8 + length
        if didx and base is not None:
            for i in range(didx[1] // 12):
                sid, off, _ = struct.unpack_from("<III", data, didx[0] + 12 * i)
                out[base + off] = sid
    return out


def main(index, cooked, out_path):
    entries = json.load(open(index, encoding="utf-8"))["entries"]
    by_pck = {}
    for e in entries:
        text = " ".join(e["transcription"].split())
        # (English only: the German Angela pack is the index's one other language)
        if text and not e["_pck"].endswith("_DEU"):
            by_pck.setdefault(e["_pck"], []).append((int(e["filename"].split("_")[0], 16), text))
    found, stats = {}, Counter()
    for pck, rows in sorted(by_pck.items()):
        path = os.path.join(cooked, pck + ".pck")
        if not os.path.exists(path):
            stats["no .pck"] += len(rows)
            continue
        data = open(path, "rb").read()
        where = positions(data)
        ids = set(where.values())
        hit = 0
        for key, text in rows:
            if key in where and data[key:key + 4] == b"RIFF":
                found.setdefault(where[key], text)
                hit += 1
            elif key in ids:  # (the first bank's real DIDX IDs)
                found.setdefault(key, text)
                hit += 1
        stats["matched"] += hit
        stats["not matched"] += len(rows) - hit
        if hit < len(rows):
            print(f"{pck}: {hit} of {len(rows)}")
    with gzip.GzipFile(out_path, "wb", mtime=0) as z:
        z.write("".join(f"{k:08x}\t{found[k]}\n" for k in sorted(found)).encode("utf-8"))
    print(dict(stats), len(found), "sounds", os.path.getsize(out_path), "bytes")


if __name__ == "__main__":
    main(*sys.argv[1:4])
