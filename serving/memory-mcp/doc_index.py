# doc_index.py — DokiDex knowledge-base RAG: "chat with your documents" over a per-conversation doc set.
#
# The prose twin of code_index.py. A conversation ATTACHES plain-text docs (.txt/.md, first slice); each doc is
# chunked into bounded overlapping CHAR windows (prose, not source-lines), embedded via the SAME local embed
# server (llama.cpp --embedding, OpenAI /v1/embeddings on :8090), and stored in its OWN disposable sqlite DB
# (doc_index.db) — separate from the precious memory.db AND from code_index.db, so a doc reindex can never harm
# either. Rows are SCOPED BY kb_id (the conversation id), so retrieval only ever sees the thread's own docs.
# Nearest-neighbour queries are answered by brute-force cosine — at single-doc scale (a few hundred chunks)
# sub-100ms, pure stdlib, NO native vector extension (sqlite-vec).
#
# Pure + injectable: chunk_text / _cosine / _pack are dependency-free and the embed call is a single injectable
# function, so the whole pipeline is unit-tested with a stub embedder (no GPU, no network) by
# tests/doc_index.test.py. The live embed HTTP call (code_index.embed, reused verbatim) is the only part that
# needs the embed server running — when it's down, the C# DocSearch sidecar degrades to "no context injected"
# and plain chat proceeds unchanged (the same contract code_search has).
#
# _pack / _unpack / _cosine / embed / _embed_input are imported VERBATIM from code_index — they are already pure
# and dim-agnostic (struct-pack <Nf at whatever length comes back; search_vec skips dim-mismatched rows), so a
# model change can't silently mis-score, and the KB inherits that for free.

import io
import json
import math
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from code_index import (  # noqa: E402  — reuse the proven, pure, dim-agnostic primitives verbatim
    MAX_EMBED_CHARS,
    EMBED_BATCH_CHARS,
    _cosine,
    _pack,
    _unpack,
    _embed_input,
    embed,
)

DB_PATH = os.environ.get("DOC_INDEX_DB", os.path.join(os.path.dirname(os.path.abspath(__file__)), "doc_index.db"))

# Prose chunking: bounded overlapping CHAR windows. ~1500 chars (~375 tokens, well under nomic's 2048 cap even
# after the source-name prefix) with ~200 overlap so a sentence straddling a boundary is findable from either
# side. CHUNK_SIZE stays <= MAX_EMBED_CHARS so a single window never trips the per-input truncation.
CHUNK_SIZE = 1500
CHUNK_OVERLAP = 200

# Hard caps so a pathological paste/upload can't emit thousands of sequential :8090 embed calls (each ~tens of
# ms) and blow past the C# sidecar's 30s timeout (which would surface a MISLEADING "start the embed server"
# 503 — the server is fine, the doc is just too big). The C# DocSearch.ValidateIngest is the primary gate
# (it returns a clear "document too large" message to the UI BEFORE spawning python); these are the defensive
# backstop so even a direct `uv run python doc_index.py doc_ingest ...` call stays bounded. MAX_CHUNKS at the
# default 1500/200 stride covers ~260k chars — comfortably above MAX_DOC_CHARS, so the C# cap trips first.
MAX_DOC_CHARS = 200_000
MAX_CHUNKS = 200

# OCR render bounds (the -Ocr scanned-PDF path only — a normal text PDF never reaches _ocr_pdf). A crafted small
# (<1.5MB) scanned PDF can declare thousands of pages OR a huge MediaBox, so rendering EVERY page at dpi=300 with no
# cap could spike peak memory. MAX_OCR_PAGES bounds the page FAN-OUT (OCR the first N; a truncation is noted), and
# OCR_MAX_PIXELS bounds a SINGLE page's bitmap: a page whose dpi=300 pixmap would exceed this budget is re-rendered
# at a clamped (lower) dpi so a pathological MediaBox can't allocate a giant bitmap. Both are best-effort safety
# rails — OCR stays graceful (a page that still can't render is simply skipped per the per-page try/except).
MAX_OCR_PAGES = 50
OCR_MAX_PIXELS = 40_000_000   # ~40 MP — comfortably covers an A4/Letter page at 300dpi (~8.7 MP), caps the rest

# Portability (the v0.16 KB export/import follow-up): a generous CEILING on the number of chunks a single
# imported envelope may insert, so a FORGED .ddkb file can't fan out into an unbounded number of DB rows. Well
# above MAX_CHUNKS=200 (the per-SOURCE cap) since a KB has many sources — a few hundred docs at 200 chunks each
# still fits comfortably under this. The envelope format/version is checked first; this is the size backstop.
MAX_IMPORT_CHUNKS = 50_000

# The self-describing export envelope's format tag + the only version this build can import. A mismatch on
# either is REJECTED (a clear error, never a partial insert) — so an export from a future format can't silently
# corrupt the DB by being read under the wrong schema assumptions.
EXPORT_FORMAT = "dokidex-kb"
EXPORT_VERSION = 1


# --- chunking (pure) ------------------------------------------------------------------------------------
def chunk_text(text, size=CHUNK_SIZE, overlap=CHUNK_OVERLAP, max_chunks=MAX_CHUNKS):
    """Split prose into bounded overlapping char windows: [(ord, chunk), ...] (0-based ordinal).
    Overlap keeps a sentence that straddles a window boundary findable from either side. All-blank
    windows are dropped. A text shorter than `size` yields a single chunk. At most `max_chunks` windows are
    emitted (the tail of an over-large body is dropped) so ingest can never fan out into an unbounded number
    of embed calls — the defensive backstop to the C# size gate."""
    if not text or not text.strip():
        return []
    step = max(1, size - overlap)
    out = []
    i = 0
    ordinal = 0
    n = len(text)
    while i < n:
        if ordinal >= max_chunks:
            break
        window = text[i:i + size]
        if window.strip():
            out.append((ordinal, window))
            ordinal += 1
        i += step
    return out


# --- binary extraction (PDF/docx, v0.14 follow-up) ------------------------------------------------------
# The ONLY part of doc_index that ever touches a non-stdlib dependency, and it does so LAZILY: the parser
# libs (pypdf for .pdf, python-docx for .docx) are imported INSIDE the per-format helper, so the txt/md fast
# path never imports them and `uv run python doc_index.py doc_ingest ...` stays pure-stdlib / zero-dep. The
# parser deps are made available ONLY on the binary path via the C# side's `uv run --with pypdf --with
# python-docx ...` overlay (see DocSearch.BuildIngestBinArgs).
#
# The library functions (extract_text / ingest_bin) NEVER call sys.exit and NEVER let a parser traceback
# escape — they raise CATCHABLE domain errors so a non-CLI consumer can import + reuse them, and the CLI
# doc_ingest_bin HANDLER is the single place that maps each to a printed JSON {"error": …} + a DISTINCT exit
# code: _ParserMissing -> exit 3 (parsers couldn't load), _ExtractFailed -> exit 4 (corrupt/encrypted/wrong
# format), _DocTooLarge -> exit 5 (extracted text over MAX_DOC_CHARS, consistent with the C# text path).
class _ParserMissing(Exception):
    """A binary-format parser dep (pypdf / python-docx) could not be imported (offline / not resolved). Carries
    the user-facing 'parsers couldn't load' message; the CLI handler maps it to {"error": …} + exit 3."""


class _ExtractFailed(Exception):
    """The parser IS installed but the FILE is unreadable — corrupt, encrypted, or not really that format (pypdf
    PdfReadError, python-docx PackageNotFoundError, etc.). Distinct from _ParserMissing (the embed server is
    fine, the dep is present); the CLI handler maps it to a clear {"error": …} + exit 4 (NOT the embed-down 1)."""


class _DocTooLarge(Exception):
    """The EXTRACTED text exceeds MAX_DOC_CHARS. Raised BEFORE chunking so a 1.5MB docx/PDF that extracts to
    >200k chars is rejected with a clear message rather than silently truncated to MAX_CHUNKS. The CLI handler
    maps it to {"error": …} + exit 5 — the same 'document too large' contract as the C# text path."""


class _Unsupported(Exception):
    """A KNOWN-but-UNSUPPORTED document FORMAT (legacy OLE binary Word: .doc / .dot). DISTINCT from _ExtractFailed
    (the file is VALID — it is just a format this build can't read) so it surfaces an HONEST "convert to .docx/.pdf/
    .txt" message instead of a misleading "corrupt/encrypted" one OR — worse — falling through to the utf-8
    passthrough that 'replace'-decodes the OLE binary into a GARBAGE chunk (a misleading "attached, N chunks").
    The CLI handler maps it to {"error": …} + exit 7 (distinct from 3/4/5). See the v0.15 ingest follow-up in
    docs/decisions.md: there is no clean pure-pip legacy-.doc reader (python-docx is OOXML-only; textract->antiword
    is Windows-unavailable+unmaintained; olefile is a low-level container only; LibreOffice is too heavy a system
    dep for the niche), so .doc is cleanly REJECTED rather than half-supported."""


def _ocr_available():
    """Return the OCR deps triple (fitz, pytesseract, Image) ONLY when BOTH the pip parsers import AND the
    Tesseract binary is locatable; else None. NEVER raises (a missing piece on the txt/md/text-PDF path must be
    a clean degrade, not a crash). The three libs are imported INSIDE this function so the fast paths never load
    them. Wires pytesseract at the UB-Mannheim default install dir — the installer does NOT add tesseract.exe to
    PATH (the PATH checkbox was removed to avoid truncating a long PATH), so without this `pytesseract` can't find
    the binary on a fresh box. TESSERACT_CMD overrides for a custom install; if neither the env path nor the
    default exists we trust PATH and let pytesseract degrade per-page (-> '' -> the existing 0-chunk no-op). This
    is the same shape as the -Kokoro launcher hardcoding the espeak-ng DLL absolute path rather than a PATH lookup."""
    try:
        import fitz            # noqa: F401 — pymupdf; renders PDF pages with bundled MuPDF, NO poppler/ghostscript
        import pytesseract
        from PIL import Image  # noqa: F401 — hands the rendered PNG to pytesseract.image_to_string
    except ImportError:
        return None
    cmd = os.environ.get("TESSERACT_CMD") or r"C:\Program Files\Tesseract-OCR\tesseract.exe"
    if os.path.exists(cmd):
        pytesseract.pytesseract.tesseract_cmd = cmd
    # else: trust PATH (a custom install); pytesseract errors per-page and we degrade to 0 chunks (still no crash).
    return (fitz, pytesseract, Image)


OCR_MIN_DPI = 72   # don't render below this (sub-72dpi OCR is unreadable); a page that STILL won't fit is skipped


def _ocr_dpi_for_page(page, dpi=300):
    """Pick a render dpi that keeps a SINGLE page's pixmap under OCR_MAX_PIXELS so a pathological MediaBox can't
    allocate a giant bitmap (FIX 2). Returns the dpi to render at, or None to SKIP the page (it can't fit the pixel
    budget even at OCR_MIN_DPI — never allocate the giant bitmap). page.rect is in POINTS (1/72in); pixels at `dpi`
    = w/72*dpi * h/72*dpi. If that exceeds the budget, scale dpi down by sqrt(budget/pixels), floored at OCR_MIN_DPI;
    if even OCR_MIN_DPI overflows, return None. Defensive: a page without a usable .rect (or any error) just keeps the
    default dpi — the per-page try/except is the final backstop, so this never raises."""
    try:
        rect = page.rect
        w_pt, h_pt = float(rect.width), float(rect.height)
        px = (w_pt / 72.0 * dpi) * (h_pt / 72.0 * dpi)
        if px > OCR_MAX_PIXELS and px > 0:
            scaled = int(dpi * math.sqrt(OCR_MAX_PIXELS / px))
            if scaled < OCR_MIN_DPI:
                # even the floor dpi would exceed the budget -> SKIP (don't allocate a multi-hundred-MP bitmap)
                floor_px = (w_pt / 72.0 * OCR_MIN_DPI) * (h_pt / 72.0 * OCR_MIN_DPI)
                return None if floor_px > OCR_MAX_PIXELS else OCR_MIN_DPI
            return scaled
    except Exception:
        pass
    return dpi


def _ocr_pdf(data, deps=None):
    """Render each PDF page (pymupdf) -> PNG bytes -> pytesseract.image_to_string, joined. Lazy + INJECTABLE: `deps`
    is the (fitz, pytesseract, Image) triple so the render+OCR join is unit-tested with fakes (no MuPDF, no Tesseract,
    no GPU). BEST-EFFORT + BOUNDED + GRACEFUL:
      • FIX 1 — the WHOLE attempt (incl. fitz.open) is guarded: ANY OCR failure (MuPDF can't open the doc, etc.)
        returns "" (the scanned 0-chunk no-op), it NEVER escapes to extract_text's _ExtractFailed (exit 4);
      • FIX 2 — OCR at most MAX_OCR_PAGES pages (a crafted thousand-page scan can't fan out), and clamp each page's
        render dpi so a huge MediaBox can't allocate a giant bitmap;
      • per-PAGE failures are still swallowed so one bad page can't abort the rest.
    Returns "" when nothing was recognized (a blank scan) — the caller keeps the existing 0-chunk no-op."""
    deps = deps or _ocr_available()
    if not deps:
        return ""
    fitz, pytesseract, Image = deps
    out = []
    try:
        with fitz.open(stream=data, filetype="pdf") as doc:
            truncated = False
            for n, page in enumerate(doc):
                if n >= MAX_OCR_PAGES:            # bound the page fan-out (FIX 2) — OCR the first N, note the rest
                    truncated = True
                    break
                try:
                    dpi = _ocr_dpi_for_page(page)          # clamp a pathological MediaBox (FIX 2)
                    if dpi is None:
                        continue                  # page too large to fit the pixel budget even at the floor -> skip
                    png = page.get_pixmap(dpi=dpi).tobytes("png")
                    with Image.open(io.BytesIO(png)) as im:   # close the decoder/handle per page (FIX 5a)
                        out.append(pytesseract.image_to_string(im) or "")
                except Exception:
                    pass                          # one unreadable page must not abort OCR of the rest
            if truncated:
                out.append(f"\n[OCR truncated at the first {MAX_OCR_PAGES} pages]")
    except Exception:
        return ""                                 # ANY whole-document OCR failure degrades to the 0-chunk no-op (FIX 1)
    return "\n".join(out)


def _extract_pdf(data):
    """Plain text of a TEXT-based PDF via pypdf (lazy import). A scanned / image-only PDF has no text layer, so
    pypdf returns ~empty; when the OCR add-on (-Ocr: pymupdf/pytesseract/Pillow + the Tesseract binary) is
    AVAILABLE we then render+OCR the pages and feed THAT into the same pipeline. When OCR is ABSENT (or a blank
    scan) we return the empty text unchanged -> 0 chunks + the existing 'looks scanned' hint, byte-for-byte."""
    try:
        from pypdf import PdfReader            # lazy: NEVER imported on the txt/md path
    except ImportError:
        raise _ParserMissing("the PDF/DOCX parsers couldn't load (offline? run: uv run --with pypdf --with python-docx ... "
                             "— the -Ocr scanned-PDF wheels pymupdf/pytesseract/Pillow ride the SAME overlay)")
    text = "\n".join((pg.extract_text() or "") for pg in PdfReader(io.BytesIO(data)).pages)
    if text.strip():
        return text                           # TEXT PDF — UNCHANGED: OCR never runs, its deps never import
    deps = _ocr_available()                   # scanned/image-only: try OCR ONLY if the add-on is installed
    if deps:
        return _ocr_pdf(data, deps)           # may still be '' (blank scan) -> 0 chunks (the existing no-op)
    return text                               # OCR absent -> '' -> 0 chunks + the current "looks scanned" hint


def _extract_docx(data):
    """Plain text of an OOXML .docx via python-docx (lazy import). Reads paragraph text only; legacy .doc/.dot
    (OLE binary Word) is NOT readable by python-docx and is REJECTED upstream in extract_text (_Unsupported, exit
    7) — the v0.15 follow-up DEFERred wiring a reader (no clean light pure-pip path; docs/decisions.md)."""
    try:
        from docx import Document               # pip dist is 'python-docx'; import package is 'docx'
    except ImportError:
        raise _ParserMissing("the PDF/DOCX parsers couldn't load (offline? run: uv run --with pypdf --with python-docx ... "
                             "— the -Ocr scanned-PDF wheels pymupdf/pytesseract/Pillow ride the SAME overlay)")
    return "\n".join(p.text for p in Document(io.BytesIO(data)).paragraphs)


# Legacy OLE binary Word formats (.doc Word 97-2003 documents + .dot templates) that python-docx (OOXML-only)
# CANNOT read. They are EXPLICITLY REJECTED (raise _Unsupported) rather than allowed to fall through to the utf-8
# passthrough below — an OLE2 Compound File 'replace'-decodes into ~half U+FFFD garbage that chunk_text would still
# emit as a noise chunk + store under the source (a misleading "attached, N chunks"). The v0.15 ingest follow-up
# evaluated wiring a reader and chose DEFER (no clean light pure-pip path — see docs/decisions.md); a clean
# rejection with an actionable "convert to .docx/.pdf/.txt" message is the real user-facing win. Modern Word docs
# are .docx, which IS supported. (.dot/.doc only — .docx/.dotx/.docm OOXML are NOT here; .docx routes to python-docx.)
_REJECTED_EXTS = {".doc", ".dot"}


def extract_text(source, data):
    """Route raw bytes -> plain text on the source EXTENSION: .pdf -> pypdf, .docx -> python-docx, legacy OLE
    .doc/.dot -> REJECTED (_Unsupported, a clear convert-to message), else utf-8 decode (txt/md and any other
    extension — the stdlib zero-dep path, mangled bytes 'replace'd, never thrown). The extracted text drops
    straight into the EXISTING chunk_text -> embed -> store pipeline unchanged.

    NEVER exits and NEVER lets a parser traceback escape: a missing parser dep re-raises _ParserMissing; a legacy
    .doc/.dot raises _Unsupported (a VALID file, just an unsupported FORMAT — NOT corrupt); ANY other exception
    from the parser (a corrupt / encrypted / not-really-that-format file) is wrapped in _ExtractFailed with a clear
    message. The CLI handler is the one place that turns each into a printed {"error": …} + the right exit code —
    so this stays importable + reusable by a non-CLI consumer."""
    ext = os.path.splitext(source)[1].lower()
    if ext in _REJECTED_EXTS:                    # legacy OLE binary Word: reject on the EXTENSION (bytes never parsed)
        raise _Unsupported(
            "legacy .doc/.dot (Word 97-2003) isn't supported — convert it to .docx, .pdf, or .txt and attach that.")
    if ext == ".pdf" or ext == ".docx":
        try:
            return _extract_pdf(data) if ext == ".pdf" else _extract_docx(data)
        except _ParserMissing:
            raise                                # missing dep -> exit 3 (handled by the CLI), distinct from below
        except Exception as e:                   # parser IS present but the FILE is bad -> a CLEAR, distinct error
            raise _ExtractFailed(
                "couldn't read this file (corrupt, encrypted, or not a valid PDF/DOCX)") from e
    return data.decode("utf-8", "replace")


def ingest_bin(kb_id, source, data, embed_fn=embed):
    """Binary-format ingest: extract plain text from `data` by `source`'s extension, ENFORCE the MAX_DOC_CHARS
    cap on the extracted text (so a small-byte / huge-text file is rejected with a clear message rather than
    silently truncated to MAX_CHUNKS), then feed the EXISTING ingest_doc (chunk_text -> embed -> store)
    byte-for-byte unchanged. Returns the number of chunks written (0 for a scanned/empty PDF whose text layer
    is blank — a benign no-op, not an error). Raises the catchable _ParserMissing / _ExtractFailed / _DocTooLarge
    domain errors (NEVER exits) — the CLI handler maps them to exit codes."""
    text = extract_text(source, data)
    if len(text) > MAX_DOC_CHARS:
        raise _DocTooLarge(
            f"document too large ({len(text)} chars) — split it or attach a smaller file (max {MAX_DOC_CHARS} chars).")
    return ingest_doc(kb_id, source, text, embed_fn=embed_fn)


# --- store ----------------------------------------------------------------------------------------------
def _conn():
    c = sqlite3.connect(DB_PATH)
    c.execute("CREATE TABLE IF NOT EXISTS doc_chunks "
              "(id INTEGER PRIMARY KEY, kb_id TEXT, source TEXT, ord INT, content TEXT, vec BLOB, ts REAL)")
    c.execute("CREATE INDEX IF NOT EXISTS ix_doc_kb ON doc_chunks(kb_id)")
    c.commit()
    return c


def reset(kb_id=None):
    """Drop a single KB's chunks (kb_id given) or the whole index (None) — a clean re-ingest."""
    c = _conn()
    try:
        if kb_id is None:
            c.execute("DELETE FROM doc_chunks")
        else:
            c.execute("DELETE FROM doc_chunks WHERE kb_id=?", (kb_id,))
        c.commit()
    finally:
        c.close()


def count(kb_id=None):
    c = _conn()
    try:
        if kb_id is None:
            return c.execute("SELECT COUNT(*) FROM doc_chunks").fetchone()[0]
        return c.execute("SELECT COUNT(*) FROM doc_chunks WHERE kb_id=?", (kb_id,)).fetchone()[0]
    finally:
        c.close()


def _batched_by_chars(chunks, budget=EMBED_BATCH_CHARS):
    """Group (ord, content) chunks so each embed request stays under the server's token budget (chars proxy).
    Same rule as code_index._batched_by_chars, adapted to the (ord, content) chunk shape."""
    batch, size = [], 0
    for ch in chunks:
        clen = min(len(ch[1]), MAX_EMBED_CHARS)
        if batch and size + clen > budget:
            yield batch
            batch, size = [], 0
        batch.append(ch)
        size += clen
    if batch:
        yield batch


def _embed_batch(chunks, embed_fn, prefix=""):
    """[((ord, content), vec), ...] for one batch. The embed input is `prefix + content` — the source filename
    is prepended so a query naming the doc matches (exactly as code_index prefixes the path). On a server error,
    retry PER ITEM and skip any chunk the server still rejects — one pathological chunk can never abort ingest."""
    def inp(ch):
        return _embed_input(prefix + ch[1])
    try:
        vecs = embed_fn([inp(ch) for ch in chunks])
        return list(zip(chunks, vecs))
    except Exception:
        out = []
        for ch in chunks:
            try:
                out.append((ch, embed_fn([inp(ch)])[0]))
            except Exception:
                pass
        return out


def ingest_doc(kb_id, source, text, embed_fn=embed):
    """Chunk + embed + (re)store one document's text under (kb_id, source). embed_fn is injectable (stubbed in
    tests). Re-ingesting the same (kb_id, source) REPLACES that source's old chunks (no duplicate accumulation).
    Returns the number of chunks written."""
    c = _conn()
    nchunks = 0
    try:
        chunks = chunk_text(text)
        c.execute("DELETE FROM doc_chunks WHERE kb_id=? AND source=?", (kb_id, source))   # replace this source
        if not chunks:
            c.commit()
            return 0
        for batch in _batched_by_chars(chunks):
            for (ordinal, content), vec in _embed_batch(batch, embed_fn, prefix=f"{source}\n"):
                c.execute("INSERT INTO doc_chunks(kb_id,source,ord,content,vec,ts) VALUES(?,?,?,?,?,?)",
                          (kb_id, source, ordinal, content, _pack(vec), time.time()))
                nchunks += 1
        c.commit()
    finally:
        c.close()
    return nchunks


def sources(kb_id):
    """The distinct source filenames attached to a KB (for the list endpoint)."""
    c = _conn()
    try:
        rows = c.execute("SELECT source, COUNT(*) FROM doc_chunks WHERE kb_id=? GROUP BY source ORDER BY source",
                         (kb_id,)).fetchall()
    finally:
        c.close()
    return [{"source": r[0], "chunks": r[1]} for r in rows]


def remove_source(kb_id, source):
    """Detach one source from a KB. Returns the number of chunks removed."""
    c = _conn()
    try:
        cur = c.execute("DELETE FROM doc_chunks WHERE kb_id=? AND source=?", (kb_id, source))
        c.commit()
        return cur.rowcount
    finally:
        c.close()


def delete(kb_id):
    """Drop ALL of one KB's chunks (every source under kb_id) — called when the owning conversation is deleted
    so a deleted-with-docs thread's vectors don't accumulate in doc_index.db forever. Returns the number of
    rows removed (0 for an unknown kb_id — a clean no-op). Only this kb_id's rows are touched; other KBs are
    untouched. (`reset(kb_id)` exists for a clean re-ingest; `delete` is the cleanup-on-delete name + count.)"""
    c = _conn()
    try:
        cur = c.execute("DELETE FROM doc_chunks WHERE kb_id=?", (kb_id,))
        c.commit()
        return cur.rowcount
    finally:
        c.close()


# --- portability: export / import a KB (the v0.16 KB-library follow-up) ---------------------------------
# Option A (the Source recommendation): export the stored doc_chunks ROWS (source, ord, content, vec) as a
# portable self-describing JSON envelope; import inserts them under a FRESH kb_id with NO embed call. The
# embeddings ARE already stored per-row (vec BLOB), so this is lossless AND needs no embed server up at import
# time. The `content` is the OVERLAPPING chunk window (not recoverable full source text) — that's exactly why we
# carry the vecs rather than re-ingesting concatenated chunks (which would re-embed duplicated overlap and
# silently corrupt scoring). kb_id is DELIBERATELY omitted from the envelope so an import can't collide with /
# overwrite an existing scope — import always mints a fresh id (supplied by the caller).
class _ImportRejected(Exception):
    """A FORGED / malformed import envelope (wrong format tag, unknown version, over the chunk ceiling). Raised
    by import_kb BEFORE any DB write (so a rejected import leaves ZERO rows). The library never exits; the CLI
    doc_import handler is the single place that maps this to a printed {"error":…} + a DISTINCT exit code (6),
    which the C# DocSearch maps to a clear 400/422 — never the embed-down 503."""


def _safe_source(source):
    """A SafeName-equivalent guard on one imported chunk's `source` basename, mirroring the C# RecipeStore.SafeName
    rule the ingest endpoints use (letters/digits/space/-/_ only, no '..', stem <= 64 chars). Returns the safe
    basename (with its original extension preserved) or None for an unsafe/empty source — the import then SKIPS
    that row rather than letting a traversal-shaped `source` ('../../etc/passwd') land in the DB."""
    if not isinstance(source, str):
        return None
    base = os.path.basename(source.strip())
    if not base or ".." in base:
        return None
    # Cap the FULL basename at 64 chars to match the C# RecipeStore.SafeName rule the ingest endpoints enforce
    # (SafeName rejects name.Length > 64 — the whole name, not just the stem); FIX 5(c).
    if len(base) > 64:
        return None
    stem, ext = os.path.splitext(base)
    if not stem:
        return None
    if not all(c.isalnum() or c in " -_" for c in stem):
        return None
    return base


def export_kb(kb_id, name=None):
    """Export one kb_id's chunks as a self-describing JSON envelope (dict). Each vec is _unpack'd back to a plain
    float LIST so the file is human-inspectable and not endian/struct-locked; embed_dim is stamped (from the first
    chunk) so an import can warn on a dim mismatch. kb_id is OMITTED — import always mints a fresh one. The KB display
    `name` is CARRIED (when supplied) so a round-trip restores the exact name (the C# import reads envelope["name"]
    -> KbStore.NewKb(name)); a None/blank name omits the key (back-compat with an unnamed export). An unknown / empty
    kb yields an envelope with chunks=[] (benign, mirrors delete's no-op)."""
    c = _conn()
    try:
        rows = c.execute("SELECT source,ord,content,vec FROM doc_chunks WHERE kb_id=? ORDER BY source,ord",
                         (kb_id,)).fetchall()
    finally:
        c.close()
    chunks = [{"source": r[0], "ord": r[1], "content": r[2], "vec": _unpack(r[3])} for r in rows]
    env = {
        "format": EXPORT_FORMAT,
        "version": EXPORT_VERSION,
        "embed_model": os.environ.get("EMBED_MODEL", "embed"),
        "embed_dim": len(chunks[0]["vec"]) if chunks else 0,
        "exported": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "chunks": chunks,
    }
    # Carry the display name so an import reproduces the exact KB name (FIX 2). A blank/None name is omitted so an
    # unnamed export stays byte-compatible with the v0.16 envelope (the import then falls back to the filename stem).
    if isinstance(name, str) and name.strip():
        env["name"] = name.strip()
    return env


def _dim_warning(envelope_dim, current_dim):
    """PURE dim-mismatch decision (no DB / no embed call, unit-tested with stubbed dims): a CLEAR warning string when
    the imported envelope's embed_dim differs from the current embedding dim, else None. Returns None (no false alarm)
    whenever EITHER side is unknown — an envelope_dim of 0 (an empty/legacy export) or a current_dim of 0/None (no
    rows to probe AND the embed server is unreachable). The warning is surfaced (not fatal): a cross-model KB still
    imports, but its rows won't retrieve (search_vec skips dim-mismatched vectors) until re-embedded under the
    current model — so a SILENT zero-hit becomes a visible, actionable message."""
    try:
        ed = int(envelope_dim or 0)
        cd = int(current_dim or 0)
    except (TypeError, ValueError):
        return None
    if ed <= 0 or cd <= 0 or ed == cd:
        return None
    return (f"imported under a different embedding dim ({ed}) than the current model ({cd}) — "
            f"this KB won't retrieve until it is re-embedded under the current model.")


def _current_embed_dim(exclude_kb=None):
    """The current on-disk embedding dimension, probed CHEAPLY with NO embed call: the vec length of any existing
    doc_chunks row (optionally excluding the just-imported kb_id so the import's own rows don't self-satisfy the
    probe). Returns 0 when the DB has no other rows to learn the dim from — the caller then treats the current dim as
    unknown and emits no warning (it never blocks, never spawns the embed server). This keeps the dim check working
    offline; a from-empty DB simply can't compare (and a same-model re-import is the common case anyway)."""
    c = _conn()
    try:
        if exclude_kb is None:
            row = c.execute("SELECT vec FROM doc_chunks LIMIT 1").fetchone()
        else:
            row = c.execute("SELECT vec FROM doc_chunks WHERE kb_id<>? LIMIT 1", (exclude_kb,)).fetchone()
    finally:
        c.close()
    return len(_unpack(row[0])) if row else 0


def import_kb(kb_id, envelope, embed_fn=embed):
    """Insert an exported envelope's chunks under the (fresh, caller-supplied) kb_id. NO embed call — the vecs
    come from the file (so import works with the embed server DOWN); embed_fn is accepted only for signature
    symmetry and is NEVER invoked. VALIDATES + BOUNDS so a forged file can't blow up the DB:
      • wrong format tag / unknown version / over MAX_IMPORT_CHUNKS -> raise _ImportRejected (ZERO rows written);
      • per-row: SafeName-guard the `source` basename, require a numeric vec list, clamp `content` to MAX_DOC_CHARS
        — any row that fails is SKIPPED (the rest still import), so a few bad rows degrade cleanly.
    Returns the number of rows actually inserted. Reuses the EXACT INSERT shape of ingest_doc."""
    if not isinstance(envelope, dict) or envelope.get("format") != EXPORT_FORMAT:
        raise _ImportRejected("not a DokiDex KB export (missing or wrong 'format').")
    if envelope.get("version") != EXPORT_VERSION:
        raise _ImportRejected(f"unsupported export version {envelope.get('version')!r} (this build imports v{EXPORT_VERSION}).")
    chunks = envelope.get("chunks")
    if not isinstance(chunks, list):
        raise _ImportRejected("malformed export: 'chunks' is not a list.")
    if len(chunks) > MAX_IMPORT_CHUNKS:
        raise _ImportRejected(f"export too large ({len(chunks)} chunks) — over the {MAX_IMPORT_CHUNKS} import ceiling.")
    c = _conn()
    n = 0
    try:
        # SAFE only because the caller ALWAYS supplies a FRESHLY-minted kb_id: the HTTP import path (StudioHost
        # /kbs/import) calls KbStore.NewKb -> a brand-new server id BEFORE this insert, so this DELETE can only ever
        # clear rows under that not-yet-used scope (never an existing KB's). A direct CLI call must likewise pass a
        # fresh id; reusing an existing kb_id here would wipe it (the FIX 5(b) caveat). (FIX 5)
        c.execute("DELETE FROM doc_chunks WHERE kb_id=?", (kb_id,))   # always-fresh id: start clean
        for ch in chunks:
            if not isinstance(ch, dict):
                continue
            src = _safe_source(ch.get("source"))
            if src is None:
                continue
            vec = ch.get("vec")
            if not isinstance(vec, list) or not vec or not all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in vec):
                continue
            content = ch.get("content")
            if not isinstance(content, str):
                continue
            content = content[:MAX_DOC_CHARS]   # clamp a forged huge chunk so it can't bloat the DB
            ordinal = ch.get("ord")
            if not isinstance(ordinal, int) or isinstance(ordinal, bool):
                ordinal = 0
            c.execute("INSERT INTO doc_chunks(kb_id,source,ord,content,vec,ts) VALUES(?,?,?,?,?,?)",
                      (kb_id, src, ordinal, content, _pack([float(x) for x in vec]), time.time()))
            n += 1
        c.commit()
    finally:
        c.close()
    return n


# --- retrieval (pure cosine, kb-scoped) -----------------------------------------------------------------
def search_vec(kb_id, query_vec, k=5):
    """Brute-force cosine nearest neighbours over the kb_id-scoped chunks, top-K. Returns [] for a degenerate
    (zero-norm) query, and skips any stored vector whose dimension differs from the query (a mismatch from a
    different embed model would otherwise silently mis-score over a shared prefix). No code-vs-docs re-rank —
    that bias is meaningless for a pure-doc corpus."""
    qdim = len(query_vec)
    if math.sqrt(sum(x * x for x in query_vec)) == 0:
        return []
    c = _conn()
    try:
        rows = c.execute("SELECT source,ord,content,vec FROM doc_chunks WHERE kb_id=?", (kb_id,)).fetchall()
    finally:
        c.close()
    scored = []
    for r in rows:
        v = _unpack(r[3])
        if len(v) == qdim:
            scored.append((_cosine(query_vec, v), r))
    scored.sort(key=lambda t: t[0], reverse=True)
    return [{"source": r[0], "ord": r[1], "content": r[2], "score": round(s, 4)}
            for s, r in scored[:k]]


def search(kb_id, query, k=5, embed_fn=embed):
    """Embed the query, then cosine-rank the kb_id-scoped chunks."""
    qv = embed_fn([query])[0]
    return search_vec(kb_id, qv, k)


def _cli(argv):
    """The CLI dispatch the C# DocSearch sidecar shells via `uv run python doc_index.py ...` (stdlib-only, no temp
    file). Returns the PROCESS EXIT CODE (it does NOT call sys.exit — the __main__ guard does that), so the dispatch
    + its exit-code mapping is unit-testable IN-PROCESS (monkeypatch sys.stdin + the extractor, assert the return).
    The library funcs (ingest_doc / extract_text / ingest_bin) raise catchable domain errors; this is the single
    place that maps each to a printed {"error":…} + the right exit code.

    Subcommands:
      doc_ingest KB SOURCE     — read the document TEXT from STDIN, chunk+embed+store it; prints {"chunks":N}.
      doc_ingest_bin KB SOURCE — read the raw FILE BYTES from STDIN, extract text by extension (.pdf->pypdf,
                                 .docx->python-docx, legacy .doc/.dot->rejected, else utf-8), then the SAME
                                 chunk+embed+store; {"chunks":N}. Distinct error exits (NOT the embed-down 1):
                                 3 = parsers couldn't load, 4 = corrupt/encrypted/wrong-format file, 5 = extracted
                                 text over MAX_DOC_CHARS, 7 = unsupported FORMAT (legacy OLE .doc/.dot).
      doc_search KB QUERY [K]  — prints a JSON array of {source,ord,content,score}.
      doc_sources KB           — prints a JSON array of {source,chunks}.
      doc_remove KB SOURCE     — prints {"removed":N}.
      doc_delete KB            — drop the WHOLE KB (conversation-delete cleanup); prints {"removed":N}.
    A connection error (embed server down) / missing index propagates as a non-zero exit, which the C# caller
    degrades to "no context injected" (plain chat proceeds unchanged) — the same contract as code_search."""
    cmd = argv[1] if len(argv) > 1 else ""
    if cmd == "doc_ingest":
        kb = argv[2] if len(argv) > 2 else ""
        src = argv[3] if len(argv) > 3 else ""
        text = sys.stdin.read()
        n = ingest_doc(kb, src, text)
        print(json.dumps({"chunks": n}))
        # A non-blank doc that produced ZERO stored chunks means the embed server rejected EVERY chunk (it's down /
        # OOM): exit non-zero so the C# IngestAsync surfaces a 503 ("start the embed server") instead of a
        # misleading "attached, 0 chunks". A genuinely empty/blank doc (no chunks to embed) exits 0.
        return 1 if (n == 0 and text.strip()) else 0
    if cmd == "doc_ingest_bin":
        kb = argv[2] if len(argv) > 2 else ""
        src = argv[3] if len(argv) > 3 else ""
        data = sys.stdin.buffer.read()            # raw FILE BYTES (no text decode) — extract_text routes on ext
        # extract_text RAISES the domain errors (never exits); this is the single place that maps each to a printed
        # {"error":…} + a DISTINCT exit code so the C# IngestBinAsync surfaces the RIGHT message (and never the
        # misleading embed-down 503). Extract ONCE here so the 0-chunk signal below reuses the same text (no re-parse).
        try:
            text = extract_text(src, data)        # parse ONCE (reused for the 0-chunk signal below)
        except _ParserMissing as e:               # parsers couldn't load (offline / unresolved) -> exit 3
            print(json.dumps({"error": str(e)})); return 3
        except _Unsupported as e:                 # legacy .doc/.dot (valid file, unsupported FORMAT) -> exit 7
            print(json.dumps({"error": str(e)})); return 7
        except _ExtractFailed as e:               # corrupt / encrypted / not-really-that-format file -> exit 4
            print(json.dumps({"error": str(e)})); return 4
        # enforce the same MAX_DOC_CHARS cap ingest_bin does, here on the already-extracted text (exit 5) — so a
        # small-byte / huge-text file is rejected with a clear message rather than silently truncated to MAX_CHUNKS.
        if len(text) > MAX_DOC_CHARS:
            print(json.dumps({"error": f"document too large ({len(text)} chars) — split it or attach a "
                                       f"smaller file (max {MAX_DOC_CHARS} chars)."})); return 5
        n = ingest_doc(kb, src, text)
        print(json.dumps({"chunks": n}))
        # A scanned/empty PDF (no text layer) extracts to "" -> 0 chunks: exit 0 (benign, the UI hints "looks
        # scanned"). Non-blank extracted text that produced 0 chunks means the embed server rejected every
        # chunk (down/OOM) -> exit 1 so the C# side surfaces the 503, exactly as the text doc_ingest path does.
        return 1 if (n == 0 and text.strip()) else 0
    if cmd == "doc_search":
        kb = argv[2] if len(argv) > 2 else ""
        q = argv[3] if len(argv) > 3 else ""
        k = int(argv[4]) if len(argv) > 4 else 5
        print(json.dumps(search(kb, q, k)))
        return 0
    if cmd == "doc_sources":
        kb = argv[2] if len(argv) > 2 else ""
        print(json.dumps(sources(kb)))
        return 0
    if cmd == "doc_remove":
        kb = argv[2] if len(argv) > 2 else ""
        src = argv[3] if len(argv) > 3 else ""
        print(json.dumps({"removed": remove_source(kb, src)}))
        return 0
    if cmd == "doc_delete":
        kb = argv[2] if len(argv) > 2 else ""
        print(json.dumps({"removed": delete(kb)}))
        return 0
    if cmd == "doc_export":
        # Print the portable self-describing envelope (the stored rows + their reusable vecs) to STDOUT. An
        # unknown/empty kb yields an empty-chunks envelope (benign, exit 0 — mirrors delete's no-op). An OPTIONAL
        # 4th argv is the KB display NAME, stamped into the envelope so a round-trip restores it (FIX 2).
        kb = argv[2] if len(argv) > 2 else ""
        name = argv[3] if len(argv) > 3 else None
        print(json.dumps(export_kb(kb, name)))
        return 0
    if cmd == "doc_import":
        # Read the envelope from STDIN, insert under the (fresh, argv-supplied) kb id with NO embed call; print
        # {"chunks":N}. A malformed/forged envelope -> {"error":…} + the DISTINCT exit code 6 (the C# side maps it
        # to a clear 400/422, NEVER the embed-down 503). Same error-mapping style as the binary path's 3/4/5.
        kb = argv[2] if len(argv) > 2 else ""
        raw = sys.stdin.read()
        try:
            envelope = json.loads(raw)
        except Exception:
            print(json.dumps({"error": "malformed import file (not valid JSON)."})); return 6
        # Probe the current on-disk dim BEFORE the insert (excluding this fresh kb so its own rows don't satisfy the
        # probe), so a cross-model import surfaces a WARNING instead of silently zero-hitting (FIX 4). No embed call;
        # an empty DB -> unknown -> no warning. The import still SUCCEEDS (graceful) — the warning is advisory.
        env_dim = envelope.get("embed_dim") if isinstance(envelope, dict) else 0
        cur_dim = _current_embed_dim(exclude_kb=kb)
        try:
            n = import_kb(kb, envelope)
        except _ImportRejected as e:
            print(json.dumps({"error": str(e)})); return 6
        out = {"chunks": n}
        warning = _dim_warning(env_dim, cur_dim)
        if warning:
            out["warning"] = warning
        print(json.dumps(out))
        return 0
    print("usage: doc_index.py {doc_ingest KB SOURCE <stdin| doc_ingest_bin KB SOURCE <stdin| doc_search KB Q [K] | doc_sources KB | doc_remove KB SOURCE | doc_delete KB | doc_export KB [NAME] | doc_import KB <stdin}")
    return 2


if __name__ == "__main__":
    sys.exit(_cli(sys.argv))
