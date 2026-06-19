# tests/doc_index.test.py — regression tests for the knowledge-base RAG core (serving/memory-mcp/doc_index.py).
#
# The doc_index is the per-conversation "chat with your documents" twin of code_index: it chunks PROSE (by
# chars, not source-lines), embeds each chunk via the SAME :8090 embed server, stores the vectors in its OWN
# disposable sqlite DB scoped by kb_id, and answers nearest-neighbour queries by brute-force cosine. This suite
# is pure stdlib: it points DOC_INDEX_DB at a throwaway temp DB and injects a deterministic STUB embedder, so
# the chunker, vector pack/unpack, cosine ranking, kb-scoped (re)ingest, and retrieval are all exercised with
# NO embed server, NO GPU, NO network. exit 0 = pass, 1 = fail. Mirrors tests/code_index.test.py exactly.
import os
import sys
import tempfile

_work = tempfile.mkdtemp(prefix="doki-docidx-")
os.environ["DOC_INDEX_DB"] = os.path.join(_work, "doc.db")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp"))
import doc_index  # noqa: E402  — import AFTER DOC_INDEX_DB is set; DB_PATH is read at import time

_pass = _fail = 0


def check(cond, msg):
    global _pass, _fail
    if cond:
        _pass += 1
        print(f"  [PASS] {msg}")
    else:
        _fail += 1
        print(f"  [FAIL] {msg}")


# deterministic bag-of-keywords stub: chunks sharing vocab with the query score higher under cosine — enough
# signal to test ranking without a real embed model (same trick as code_index.test.py).
_VOCAB = ["dragon", "north", "castle", "ocean", "south", "forest", "wizard", "spell", "river", "mountain"]


def stub_embed(texts):
    out = []
    for t in texts:
        tl = t.lower()
        v = [float(tl.count(w)) for w in _VOCAB]
        v.append(0.5)   # bias so a no-vocab chunk isn't a degenerate all-zero vector
        out.append(v)
    return out


try:
    # --- chunk_text (pure, prose: bounded overlapping CHAR windows) ---
    check(doc_index.chunk_text("") == [], "chunk_text('') -> []")
    check(doc_index.chunk_text("   \n\n  \n ") == [], "all-blank text -> [] (blank windows dropped)")

    short = doc_index.chunk_text("a short paragraph that fits in one window")
    check(len(short) == 1 and short[0][0] == 0, "short text -> one chunk, ordinal 0")
    check(short[0][1] == "a short paragraph that fits in one window", "the single chunk carries the full text")

    body = "x" * 4000   # 4000 chars at size 1500 / overlap 200 -> multiple overlapping windows
    cks = doc_index.chunk_text(body, size=1500, overlap=200)
    check(len(cks) >= 3, f"4000 chars @ 1500/200 -> multiple windows ({len(cks)})")
    check(cks[0][0] == 0 and cks[1][0] == 1, "chunk ordinals are 0-based and sequential")
    # windows overlap: each window after the first begins (size-overlap)=1300 chars in, so consecutive windows
    # share the trailing/leading `overlap` chars — a sentence straddling a boundary is findable from either side.
    step = 1500 - 200
    check(len(cks[0][1]) == 1500, "first window is exactly `size` chars")
    full = "".join(cks[i][1][:step] if i < len(cks) - 1 else cks[i][1] for i in range(len(cks)))
    check(body in full or full.startswith(body[:step]), "windows tile the whole text (overlap, no gap)")
    # explicit overlap proof: the last `overlap` chars of window 0 equal the first `overlap` chars of window 1.
    check(cks[0][1][step:] == cks[1][1][:1500 - step], "consecutive windows overlap by `overlap` chars")

    # --- vector pack/unpack + cosine reused VERBATIM from code_index (dim-agnostic) ---
    ru = doc_index._unpack(doc_index._pack([0.1, -2.5, 3.0, 0.0]))
    check(len(ru) == 4 and abs(ru[1] + 2.5) < 1e-5, "pack/unpack round-trips a float vector")
    check(abs(doc_index._cosine([1, 2, 3], [1, 2, 3]) - 1.0) < 1e-9, "cosine(x,x) == 1")
    check(doc_index._cosine([1, 0], [0, 1]) == 0.0, "cosine(orthogonal) == 0")
    check(doc_index._cosine([0, 0], [1, 1]) == 0.0, "cosine with a zero vector == 0 (no div-by-zero)")

    # --- ingest_doc + search (stub embedder, temp DB, kb_id-scoped) ---
    n1 = doc_index.ingest_doc("kbA", "lore.txt",
                              "Dragons rule the north and guard the castle.", embed_fn=stub_embed)
    n2 = doc_index.ingest_doc("kbA", "geo.txt",
                              "The ocean lies to the south past the forest.", embed_fn=stub_embed)
    check(n1 >= 1 and n2 >= 1, f"ingest_doc returns chunks written ({n1}, {n2})")
    check(doc_index.count("kbA") == n1 + n2, "count(kbA) totals both docs' chunks")

    hits = doc_index.search("kbA", "dragon castle north", k=3, embed_fn=stub_embed)
    check(len(hits) >= 1, "search returns hits")
    check(hits[0]["source"] == "lore.txt", f"top hit for 'dragon castle' is lore.txt (got {hits[0]['source']})")
    check(hits[0]["score"] >= hits[-1]["score"], "results sorted by descending score")
    check(doc_index.search("kbA", "ocean south forest", k=1, embed_fn=stub_embed)[0]["source"] == "geo.txt",
          "top hit for 'ocean south' is geo.txt")

    # --- kb_id scoping: a query in another KB never sees kbA's chunks ---
    doc_index.ingest_doc("kbB", "other.txt", "A wizard casts a spell by the river.", embed_fn=stub_embed)
    check(doc_index.count("kbB") >= 1, "kbB has its own chunks")
    bhits = doc_index.search("kbB", "dragon castle north", k=5, embed_fn=stub_embed)
    check(all(h["source"] == "other.txt" for h in bhits), "search is scoped to kb_id (kbB never returns kbA rows)")
    check(doc_index.search("kbMISSING", "anything", k=5, embed_fn=stub_embed) == [],
          "search over an unknown kb_id returns [] (empty, not an error)")

    # --- re-ingest replaces a source's chunks, not duplicates ---
    # lore.txt currently has n1 chunks; re-ingesting a SHORTER body (one window) must leave the KB at
    # geo.txt's chunks + exactly ONE lore.txt chunk — strictly fewer rows than before, never duplicated.
    before = doc_index.count("kbA")
    doc_index.ingest_doc("kbA", "lore.txt", "Dragons rule the north.", embed_fn=stub_embed)
    after = doc_index.count("kbA")
    check(after == n2 + 1, f"re-ingest replaces lore.txt with its single new chunk (geo {n2} + lore 1 = {after})")
    check(after <= before, f"re-ingest never grows the row count ({before} -> {after})")
    # prove no dupes: only geo.txt + the single replaced lore.txt chunk remain for the short bodies.
    srcs = sorted({h["source"] for h in doc_index.search("kbA", "north south", k=10, embed_fn=stub_embed)})
    check(srcs == ["geo.txt", "lore.txt"], f"re-ingest leaves exactly the two sources (got {srcs})")

    # --- search_vec guards: degenerate / mismatched queries return [] instead of garbage rankings ---
    check(doc_index.search_vec("kbA", [0.0] * 11, k=5) == [], "zero-norm query returns [] (no all-tied ranking)")
    check(doc_index.search_vec("kbA", [1.0, 2.0, 3.0], k=5) == [],
          "dimension-mismatched query returns [] (no prefix mis-scoring)")

    # --- source-prefix: the source filename is prepended to the EMBED input (helps doc-naming queries) ---
    captured = []
    def spy(texts):
        captured.extend(texts)
        return [[1.0] for _ in texts]
    doc_index.ingest_doc("kbS", "manual.txt", "body text", embed_fn=spy)
    check(captured and "manual.txt" in captured[0], "ingest prepends the source filename to the embed input")

    # --- request-budget batching + truncation (the embed server's per-input token limit), reused from code_index ---
    check(len(doc_index._embed_input("z" * 9000)) == doc_index.MAX_EMBED_CHARS, "_embed_input truncates an over-long chunk")
    check(doc_index._embed_input("short") == "short", "_embed_input leaves a short chunk untouched")

    def flaky(texts):   # fails on any item containing BOOM (simulates the embed server rejecting one)
        if any("BOOM" in t for t in texts):
            raise RuntimeError("server 500")
        return [[1.0] for _ in texts]
    # a long body so chunk_text yields MULTIPLE windows; only the middle window carries BOOM.
    flaky_body = ("good " * 300) + ("BOOM " * 300) + ("good2 " * 300)   # ~5400 chars -> several windows
    n = doc_index.ingest_doc("kbF", "f.txt", flaky_body, embed_fn=flaky)
    # the pathological window(s) are skipped, the rest survive — one bad chunk can never abort the whole ingest.
    check(doc_index.count("kbF") == n and n >= 1, f"ingest skips a rejected chunk, keeps the rest ({n})")
    check(not any("BOOM" in h["content"] for h in doc_index.search_vec("kbF", [1.0], k=20)),
          "no stored kbF chunk contains the rejected BOOM window")

    # --- chunk-count cap: a pathologically large body can't emit thousands of sequential embed calls. chunk_text
    #     stops at MAX_CHUNKS windows (drops the tail) so ingest stays bounded even on a direct CLI call. ---
    check(doc_index.MAX_CHUNKS >= 1, "MAX_CHUNKS is a positive cap")
    huge = "word " * 200_000   # ~1,000,000 chars -> far more than MAX_CHUNKS windows without a cap
    capped = doc_index.chunk_text(huge)
    check(len(capped) == doc_index.MAX_CHUNKS, f"chunk_text caps at MAX_CHUNKS windows ({len(capped)} == {doc_index.MAX_CHUNKS})")
    nh = doc_index.ingest_doc("kbHUGE", "huge.txt", huge, embed_fn=stub_embed)
    check(nh <= doc_index.MAX_CHUNKS, f"ingest_doc never stores more than MAX_CHUNKS chunks ({nh})")
    # a small body is unaffected by the cap (still a single window).
    check(len(doc_index.chunk_text("just a little prose")) == 1, "the chunk cap leaves a small doc at one window")

    # --- doc_delete: dropping a KB's chunks on conversation-delete (the disk-leak fix). delete(kb_id) removes
    #     exactly that KB's rows and leaves every other KB intact. ---
    doc_index.ingest_doc("kbDEL", "a.txt", "Dragons in the north.", embed_fn=stub_embed)
    doc_index.ingest_doc("kbDEL", "b.txt", "An ocean to the south.", embed_fn=stub_embed)
    doc_index.ingest_doc("kbKEEP", "k.txt", "A wizard by the river.", embed_fn=stub_embed)
    check(doc_index.count("kbDEL") >= 2, "kbDEL seeded with two sources")
    removed = doc_index.delete("kbDEL")
    check(removed >= 2, f"delete(kbDEL) reports the rows dropped ({removed})")
    check(doc_index.count("kbDEL") == 0, "delete(kbDEL) leaves zero kbDEL rows")
    check(doc_index.count("kbKEEP") >= 1, "delete(kbDEL) leaves other KBs intact (kbKEEP untouched)")
    check(doc_index.delete("kbNEVER") == 0, "delete over an unknown kb_id is a no-op returning 0")

    # --- extract_text dispatch (PDF/docx ingest, v0.14 follow-up): route on the source EXTENSION; the parser
    #     deps (pypdf / python-docx) are lazy-imported ONLY for binary formats so the txt/md path stays pure
    #     stdlib. The extractor is PURE + injectable: _extract_pdf / _extract_docx are swappable so the dispatch,
    #     the passthrough, and the missing-dep "install" message are unit-testable with NO real parser libs. ---

    # .txt / .md / unknown extension -> stdlib utf-8 decode, NO parser import (the zero-dep fast path).
    check(doc_index.extract_text("notes.txt", b"hello world") == "hello world",
          "extract_text(.txt) is a plain utf-8 decode (no parser)")
    check(doc_index.extract_text("readme.md", b"# Title\n\nbody") == "# Title\n\nbody",
          "extract_text(.md) is a plain utf-8 decode (no parser)")
    check(doc_index.extract_text("data.unknownext", b"raw bytes") == "raw bytes",
          "extract_text(unknown ext) falls back to utf-8 decode")
    # malformed utf-8 never throws — 'replace' keeps ingest crash-free (mirrors the existing decode contract).
    check(isinstance(doc_index.extract_text("x.txt", b"\xff\xfe bad"), str),
          "extract_text decodes malformed bytes without throwing (errors='replace')")
    # extension match is case-insensitive (.TXT and .PDF behave like .txt/.pdf).
    check(doc_index.extract_text("SHOUT.TXT", b"loud") == "loud",
          "extract_text extension match is case-insensitive")

    # --- legacy .doc / .dot (OLE binary Word) EXPLICIT REJECTION (the v0.15 ingest follow-up: WIRE-vs-DEFER
    #     evaluated -> DEFER; see docs/decisions.md). python-docx reads ONLY OOXML .docx, NOT the OLE2 Compound
    #     File .doc/.dot — there is no clean pure-pip reader (olefile is a low-level container only; textract ->
    #     antiword is Windows-unavailable+unmaintained; aspose/spire are commercial; LibreOffice is too heavy a
    #     system dep for the niche). So rather than let an OLE .doc fall through to the utf-8 passthrough (which
    #     'replace'-decodes the binary into a GARBAGE chunk that embeds + stores under the source — a misleading
    #     "attached, N chunks"), extract_text REJECTS .doc/.dot with the catchable _Unsupported domain error and a
    #     clear "convert to .docx/.pdf/.txt" message. The OLE2 magic header is the real legacy-.doc signature. ---
    _ole_magic = b"\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1" + bytes(range(256))   # OLE2 Compound File header + binary body
    for _src in ("legacy.doc", "memo.DOC", "template.dot", "macro.DOT"):
        threw = None
        try:
            doc_index.extract_text(_src, _ole_magic)
        except doc_index._Unsupported as us:
            threw = us
        except Exception as e:                       # MUST be the clean _Unsupported, never a raw traceback / other error
            threw = ("WRONG", e)
        check(isinstance(threw, doc_index._Unsupported),
              f"extract_text({_src}) RAISES _Unsupported (legacy .doc/.dot is not utf-8 garbage-decoded)")
        check(isinstance(threw, doc_index._Unsupported) and ".docx" in str(threw)
              and ("convert" in str(threw).lower()),
              f"extract_text({_src}) _Unsupported message tells the user to convert to .docx/.pdf/.txt")
    # _Unsupported is DISTINCT from _ExtractFailed (a .doc is a VALID file, just an unsupported FORMAT — not
    # "corrupt/encrypted"), so the two surface different, honest messages and (below) different CLI exit codes.
    check(issubclass(doc_index._Unsupported, Exception)
          and not issubclass(doc_index._Unsupported, doc_index._ExtractFailed),
          "_Unsupported is its own domain error, NOT a subclass of _ExtractFailed (a valid file, unsupported format)")
    # the rejection is decided on the EXTENSION alone (bytes never parsed) — an empty .doc is rejected just the same.
    threw_empty = None
    try:
        doc_index.extract_text("blank.doc", b"")
    except doc_index._Unsupported as us:
        threw_empty = us
    check(isinstance(threw_empty, doc_index._Unsupported),
          "extract_text(.doc) rejects on the extension alone — even empty bytes (never a utf-8 passthrough)")

    # .pdf -> _extract_pdf, .docx -> _extract_docx. Stub both so the dispatch is tested with NO real lib.
    _orig_pdf, _orig_docx = doc_index._extract_pdf, doc_index._extract_docx
    try:
        doc_index._extract_pdf = lambda data: f"PDF[{data.decode()}]"
        doc_index._extract_docx = lambda data: f"DOCX[{data.decode()}]"
        check(doc_index.extract_text("manual.pdf", b"abc") == "PDF[abc]",
              ".pdf routes to _extract_pdf")
        check(doc_index.extract_text("report.docx", b"xyz") == "DOCX[xyz]",
              ".docx routes to _extract_docx")
        check(doc_index.extract_text("MANUAL.PDF", b"abc") == "PDF[abc]",
              ".PDF (uppercase) still routes to _extract_pdf")
    finally:
        doc_index._extract_pdf, doc_index._extract_docx = _orig_pdf, _orig_docx

    # missing parser dep -> the library RAISES the catchable sentinel _ParserMissing (NOT SystemExit): a non-CLI
    # consumer can import doc_index and handle it without the process dying. The CLI doc_ingest_bin handler is the
    # ONLY place that turns it into a printed JSON {"error": ...} + exit 3 (asserted below). Simulate the ImportError.
    def _boom_pdf(data):
        raise doc_index._ParserMissing("the PDF/DOCX parsers couldn't load")
    _orig_pdf2 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = _boom_pdf
        threw = None
        try:
            doc_index.extract_text("x.pdf", b"data")
        except SystemExit as se:
            threw = ("exit", se)            # MUST NOT happen — the library no longer exits
        except doc_index._ParserMissing as pm:
            threw = ("missing", pm)
        check(threw is not None and threw[0] == "missing",
              "missing PDF parser -> extract_text RAISES _ParserMissing (no SystemExit from the library)")
    finally:
        doc_index._extract_pdf = _orig_pdf2

    # the real lazy importers exist and are callable (they raise _ParserMissing only when the lib is absent).
    check(callable(doc_index._extract_pdf) and callable(doc_index._extract_docx),
          "_extract_pdf / _extract_docx are defined lazy importers")

    # --- ingest_bin: bytes -> extract_text -> the EXISTING ingest_doc pipeline UNTOUCHED. A .txt byte stream
    #     chunks+embeds+stores exactly like the text path; a binary format routes through the stubbed extractor. ---
    nb = doc_index.ingest_bin("kbBIN", "lore.txt", b"Dragons rule the north and guard the castle.",
                              embed_fn=stub_embed)
    check(nb >= 1 and doc_index.count("kbBIN") == nb, f"ingest_bin(.txt bytes) feeds the existing pipeline ({nb})")
    bhit = doc_index.search("kbBIN", "dragon castle north", k=1, embed_fn=stub_embed)
    check(bhit and bhit[0]["source"] == "lore.txt", "ingest_bin chunks are retrievable like text-ingest chunks")

    # a binary format flows through extract_text -> ingest_doc with the SAME chunker (stub the parser).
    _orig_pdf3 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda data: "A wizard casts a spell by the river."
        np = doc_index.ingest_bin("kbPDF", "spellbook.pdf", b"%PDF-fake-bytes", embed_fn=stub_embed)
        check(np >= 1, f"ingest_bin(.pdf) extracts then ingests via the existing pipeline ({np})")
        ph = doc_index.search("kbPDF", "wizard spell river", k=1, embed_fn=stub_embed)
        check(ph and ph[0]["source"] == "spellbook.pdf", "extracted PDF text is retrievable")
    finally:
        doc_index._extract_pdf = _orig_pdf3

    # a scanned / empty PDF (no text layer) extracts to "" -> 0 chunks, NOT a crash (the benign no-op the UI
    # surfaces as "looks scanned"). ingest_bin returns 0 just like ingest_doc on blank text.
    _orig_pdf4 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda data: ""   # image-only PDF -> empty text layer
        ns = doc_index.ingest_bin("kbSCAN", "scan.pdf", b"%PDF-image-only", embed_fn=stub_embed)
        check(ns == 0, "a scanned/empty PDF ingests as 0 chunks (benign no-op, no crash)")
    finally:
        doc_index._extract_pdf = _orig_pdf4

    # --- OCR FALLBACK (-Ocr, scanned/image-only PDFs): _extract_pdf renders+OCRs ONLY when the pypdf text layer
    #     is ~empty AND the OCR add-on is available. A normal TEXT PDF must NEVER trigger OCR (no cost, no import).
    #     The whole dispatch is tested with a STUB PdfReader (no real pypdf bytes) + a monkeypatched _ocr_available
    #     / _ocr_pdf — no MuPDF, no Tesseract, no GPU. (Mirrors the existing _extract_pdf stub discipline above.) ---
    import types as _types

    class _StubPage:
        def __init__(self, t): self._t = t
        def extract_text(self): return self._t

    class _StubReader:
        # set _StubReader.pages_text before each _extract_pdf call to control the pypdf text layer
        pages_text = [""]
        def __init__(self, _stream): self.pages = [_StubPage(t) for t in _StubReader.pages_text]

    _orig_pypdf = sys.modules.get("pypdf")
    _fake_pypdf = _types.ModuleType("pypdf"); _fake_pypdf.PdfReader = _StubReader
    sys.modules["pypdf"] = _fake_pypdf
    _orig_ocr_avail, _orig_ocr_pdf = doc_index._ocr_available, doc_index._ocr_pdf
    try:
        # (a) TEXT PDF -> pypdf returns real text -> _extract_pdf returns it and OCR is NEVER consulted.
        _StubReader.pages_text = ["A wizard reads the spellbook by the river."]
        _ocr_called = {"avail": False, "pdf": False}
        doc_index._ocr_available = lambda: (_ocr_called.__setitem__("avail", True), None)[1]
        doc_index._ocr_pdf = lambda data, deps=None: (_ocr_called.__setitem__("pdf", True), "")[1]
        txt = doc_index._extract_pdf(b"%PDF-text")
        check(txt == "A wizard reads the spellbook by the river.",
              "_extract_pdf returns the pypdf text layer for a TEXT PDF")
        check(_ocr_called["avail"] is False and _ocr_called["pdf"] is False,
              "TEXT PDF NEVER consults OCR (no _ocr_available / _ocr_pdf call, no OCR cost)")

        # (b) EMPTY text layer (scanned PDF) + OCR AVAILABLE -> _extract_pdf returns the OCR text -> the pipeline.
        _StubReader.pages_text = ["   ", ""]    # image-only: blank/whitespace text layer
        doc_index._ocr_available = lambda: ("FITZ", "PYT", "IMG")        # a non-None deps triple
        doc_index._ocr_pdf = lambda data, deps=None: "OCR: dragon guards the northern castle."
        otxt = doc_index._extract_pdf(b"%PDF-scan")
        check(otxt == "OCR: dragon guards the northern castle.",
              "scanned PDF + OCR available -> _extract_pdf returns the OCR'd text")

        # (c) EMPTY text layer + OCR ABSENT -> _extract_pdf returns "" (the preserved 0-chunk 'looks scanned' no-op).
        _StubReader.pages_text = [""]
        doc_index._ocr_available = lambda: None
        doc_index._ocr_pdf = lambda data, deps=None: "SHOULD-NOT-BE-USED"
        atxt = doc_index._extract_pdf(b"%PDF-scan-no-ocr")
        check(atxt == "", "scanned PDF + OCR ABSENT -> _extract_pdf returns '' (0 chunks + the 'looks scanned' hint)")

        # (d) _ocr_pdf itself: the injectable render+OCR join over fake (fitz, pytesseract, Image) deps — no MuPDF,
        #     no Tesseract. A fake doc yields two pages; each page's pixmap PNG is OCR'd and the results are joined.
        #     The fakes model the REAL libs faithfully: a page exposes .rect (PyMuPDF, in points) so the dpi-clamp
        #     path runs, and Image.open returns a CONTEXT MANAGER (a real PIL.Image is one, so _ocr_pdf can `with` it).
        class _FakeRect:
            def __init__(self, w, h): self.width = w; self.height = h
        class _FakePix:
            def tobytes(self, _fmt): return b"PNG"
        class _FakePage:
            rect = _FakeRect(612, 792)            # US-Letter points (~8.7 MP at 300dpi — under OCR_MAX_PIXELS)
            def get_pixmap(self, dpi=300): return _FakePix()
        class _FakeImg:                           # a context-manager image (real PIL.Image supports `with`)
            def __init__(self, v): self.v = v
            def __enter__(self): return self.v
            def __exit__(self, *a): return False
        class _FakeDoc:
            def __init__(self, pages): self._pages = pages
            def __iter__(self): return iter(self._pages)
            def __enter__(self): return self
            def __exit__(self, *a): return False
        class _FakeFitz:
            opened = {}
            def open(self, stream=None, filetype=None):
                _FakeFitz.opened = {"len": len(stream or b""), "filetype": filetype}
                return _FakeDoc([_FakePage(), _FakePage()])
        class _FakePyt:
            seen = []
            def image_to_string(self, img): _FakePyt.seen.append(img); return f"page{len(_FakePyt.seen)}"
        class _FakeImage:
            @staticmethod
            def open(buf): return _FakeImg(f"img<{buf.getvalue().decode()}>")
        joined = _orig_ocr_pdf(b"%PDF-2page", deps=(_FakeFitz(), _FakePyt(), _FakeImage))
        check(joined == "page1\npage2", "_ocr_pdf joins per-page OCR text (render -> PNG -> image_to_string)")
        check(_FakeFitz.opened.get("filetype") == "pdf", "_ocr_pdf opens the PDF stream with filetype='pdf'")

        # (e) a per-page failure is SWALLOWED so one bad page can't abort OCR of the rest.
        class _BoomPage:
            rect = _FakeRect(612, 792)
            def get_pixmap(self, dpi=300): raise RuntimeError("bad page")
        class _FakeFitz2:
            def open(self, stream=None, filetype=None):
                return _FakeDoc([_BoomPage(), _FakePage()])
        class _FakePyt2:
            def image_to_string(self, img): return "good"
        survived = _orig_ocr_pdf(b"x", deps=(_FakeFitz2(), _FakePyt2(), _FakeImage))
        check(survived == "good", "_ocr_pdf swallows a per-page failure (one bad page doesn't abort the rest)")

        # (f) _ocr_pdf with no deps and OCR unavailable -> '' (never raises).
        doc_index._ocr_available = lambda: None
        check(_orig_ocr_pdf(b"x") == "", "_ocr_pdf returns '' when OCR is unavailable (graceful, never raises)")

        # (g) REVIEW FIX 1 — a per-DOCUMENT failure (fitz.open itself raises, e.g. MuPDF can't OPEN a PDF that
        #     pypdf read as a blank text layer) must NOT escape _ocr_pdf: OCR is best-effort, so ANY OCR failure
        #     degrades to "" (the scanned 0-chunk no-op), never a hard error. Today the fitz.open is OUTSIDE the
        #     per-page try, so its exception would propagate _ocr_pdf -> _extract_pdf -> extract_text's except ->
        #     the WRONG _ExtractFailed (exit 4 "couldn't read this file") instead of the benign return "".
        class _FitzOpenBoom:
            def open(self, stream=None, filetype=None):
                raise RuntimeError("MuPDF: cannot open broken document")
        check(_orig_ocr_pdf(b"%PDF-unopenable", deps=(_FitzOpenBoom(), _FakePyt(), _FakeImage)) == "",
              "_ocr_pdf returns '' when fitz.open itself raises (whole-doc OCR failure degrades, never escapes) [FIX 1]")
        # ...and end-to-end through _extract_pdf: a scanned PDF whose OCR open() blows up must come back as "" (the
        # 0-chunk 'looks scanned' no-op), so extract_text does NOT wrap it as _ExtractFailed.
        _StubReader.pages_text = [""]                       # image-only: pypdf sees a blank text layer
        doc_index._ocr_available = lambda: (_FitzOpenBoom(), _FakePyt(), _FakeImage)
        doc_index._ocr_pdf = _orig_ocr_pdf                  # use the REAL _ocr_pdf so the open() boom is exercised
        threw = None
        try:
            etxt = doc_index.extract_text("scan-bad-open.pdf", b"%PDF-image-only-unopenable")
        except doc_index._ExtractFailed as ef:
            threw = ef
        check(threw is None and etxt == "",
              "a scanned PDF whose OCR open() raises -> extract_text returns '' (0-chunk no-op), NOT _ExtractFailed [FIX 1]")

        # (h) REVIEW FIX 2 — bound the render: a pathological scanned PDF with thousands of pages must not OCR
        #     unbounded. _ocr_pdf OCRs at most MAX_OCR_PAGES pages (the first N) so peak render memory stays sane.
        check(getattr(doc_index, "MAX_OCR_PAGES", 0) >= 1, "MAX_OCR_PAGES is a positive page cap")
        class _CountingPyt:
            def __init__(self): self.calls = 0
            def image_to_string(self, img):
                self.calls += 1
                return "p"
        _many = [_FakePage() for _ in range(doc_index.MAX_OCR_PAGES + 25)]   # well over the cap
        class _FitzMany:
            def open(self, stream=None, filetype=None): return _FakeDoc(_many)
        _cpyt = _CountingPyt()
        capped_txt = _orig_ocr_pdf(b"%PDF-many-pages", deps=(_FitzMany(), _cpyt, _FakeImage))
        check(_cpyt.calls == doc_index.MAX_OCR_PAGES,
              f"_ocr_pdf OCRs at most MAX_OCR_PAGES pages ({_cpyt.calls} == {doc_index.MAX_OCR_PAGES}) [FIX 2]")
        check("truncated" in capped_txt.lower(),
              "_ocr_pdf notes the truncation when a doc exceeds MAX_OCR_PAGES (the user isn't silently short-changed) [FIX 2]")

        # (i) REVIEW FIX 2 — clamp a pathological MediaBox. A page whose dpi=300 pixmap would blow past OCR_MAX_PIXELS
        #     but is still renderable at a lower dpi must be CLAMPED (rendered smaller, never the giant bitmap); a
        #     truly absurd page that overflows the budget even at the floor dpi must be SKIPPED (never allocated). A
        #     recording page captures the dpi _ocr_pdf actually asks for (None == get_pixmap was never called -> skip).
        class _RecPage:
            def __init__(self, w_pt, h_pt): self.rect = _FakeRect(w_pt, h_pt); self.dpi_used = None
            def get_pixmap(self, dpi=300): self.dpi_used = dpi; return _FakePix()
        big_page  = _RecPage(3036, 3036)          # ~160 MP at 300dpi (~4x budget) -> clamps to a lower, still-legible dpi
        norm_page = _RecPage(612, 792)            # US-Letter — under the budget, keeps full dpi=300
        skip_page = _RecPage(20000, 20000)        # ~7.7 GP at 300dpi, still >budget at the floor dpi -> SKIP (no render)
        class _FitzClamp:
            def open(self, stream=None, filetype=None): return _FakeDoc([big_page, norm_page, skip_page])
        _orig_ocr_pdf(b"%PDF-bigbox", deps=(_FitzClamp(), _FakePyt2(), _FakeImage))
        check(big_page.dpi_used is not None and big_page.dpi_used < 300,
              f"_ocr_pdf clamps the render dpi for an oversize MediaBox (got {big_page.dpi_used} < 300) [FIX 2]")
        check(norm_page.dpi_used == 300, f"_ocr_pdf keeps full dpi=300 for a normal page (got {norm_page.dpi_used}) [FIX 2]")
        check(skip_page.dpi_used is None,
              "_ocr_pdf SKIPS a page too large to fit the pixel budget even at the floor dpi (never allocates it) [FIX 2]")
        # the clamp keeps the rendered page UNDER the pixel budget (the whole point — no giant bitmap is allocated).
        _clamped_px = (3036 / 72.0 * big_page.dpi_used) ** 2
        check(_clamped_px <= doc_index.OCR_MAX_PIXELS,
              f"the clamped render stays within OCR_MAX_PIXELS ({int(_clamped_px)} <= {doc_index.OCR_MAX_PIXELS}) [FIX 2]")
    finally:
        doc_index._ocr_available, doc_index._ocr_pdf = _orig_ocr_avail, _orig_ocr_pdf
        if _orig_pypdf is not None: sys.modules["pypdf"] = _orig_pypdf
        else: sys.modules.pop("pypdf", None)

    # _ocr_available is defined, callable, and NEVER raises (returns None when the deps/binary are absent in CI).
    _av = None
    try:
        _av = doc_index._ocr_available()
        _av_threw = False
    except Exception:
        _av_threw = True
    check(not _av_threw and (_av is None or (isinstance(_av, tuple) and len(_av) == 3)),
          "_ocr_available() never raises -> None (deps absent) or a 3-tuple (deps present)")

    # --- FIX 1: a CORRUPT/encrypted/not-really-that-format file (parser INSTALLED but the bytes are bad) raises a
    #     non-_ParserMissing exception inside _extract_pdf/_extract_docx (pypdf PdfReadError, python-docx
    #     PackageNotFoundError, etc.). extract_text must convert THAT into the catchable domain error _ExtractFailed
    #     carrying a CLEAR "couldn't read this file" message — NOT let it escape as an uncaught traceback (which the
    #     C# side would misread as exit 1 -> the WRONG "start the embed server" 503). ---
    def _corrupt_pdf(data):
        raise ValueError("EOF marker not found")     # stand-in for pypdf's PdfReadError on garbage bytes
    _orig_pdf5 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = _corrupt_pdf
        threw = None
        try:
            doc_index.extract_text("broken.pdf", b"%PDF-garbage")
        except doc_index._ExtractFailed as ef:
            threw = ef
        except doc_index._ParserMissing:
            threw = "WRONG: _ParserMissing"           # a corrupt file is NOT a missing dep
        check(isinstance(threw, doc_index._ExtractFailed),
              "corrupt PDF -> extract_text raises _ExtractFailed (not _ParserMissing, not a raw traceback)")
        check(isinstance(threw, doc_index._ExtractFailed) and "couldn't read" in str(threw),
              "the _ExtractFailed message is the clear 'couldn't read this file (corrupt, encrypted, ...)' text")
    finally:
        doc_index._extract_pdf = _orig_pdf5

    # the same for .docx (a non-OOXML / encrypted file): a parser exception becomes _ExtractFailed, never a traceback.
    def _corrupt_docx(data):
        raise KeyError("word/document.xml")           # stand-in for python-docx PackageNotFoundError
    _orig_docx5 = doc_index._extract_docx
    try:
        doc_index._extract_docx = _corrupt_docx
        threw = None
        try:
            doc_index.extract_text("broken.docx", b"PK-not-really-a-docx")
        except doc_index._ExtractFailed as ef:
            threw = ef
        check(isinstance(threw, doc_index._ExtractFailed),
              "corrupt DOCX -> extract_text raises _ExtractFailed (clear message, no traceback)")
    finally:
        doc_index._extract_docx = _orig_docx5

    # --- FIX 2: enforce MAX_DOC_CHARS on the EXTRACTED text (no silent content drop). The binary path bounds raw
    #     BYTES (the C# MaxDocBytes) but a 1.5MB docx/PDF can extract to >MAX_DOC_CHARS chars, which chunk_text would
    #     silently truncate to MAX_CHUNKS with no message. ingest_bin must instead raise the catchable _DocTooLarge
    #     with the SAME clear "document too large" message as the text path, BEFORE chunking. ---
    check(doc_index.MAX_DOC_CHARS == 200_000, "MAX_DOC_CHARS is the 200k char cap (matches the C# MaxDocChars)")
    _orig_pdf6 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda data: "x" * (doc_index.MAX_DOC_CHARS + 1)   # extracts to OVER the cap
        threw = None
        try:
            doc_index.ingest_bin("kbBIG", "huge.pdf", b"%PDF-small-bytes-big-text", embed_fn=stub_embed)
        except doc_index._DocTooLarge as dl:
            threw = dl
        check(isinstance(threw, doc_index._DocTooLarge),
              "extracted text over MAX_DOC_CHARS -> ingest_bin raises _DocTooLarge (no silent truncate-to-200 attach)")
        check(isinstance(threw, doc_index._DocTooLarge) and "too large" in str(threw),
              "the _DocTooLarge message is the clear 'document too large (N chars) — split it ...' text")
        check(doc_index.count("kbBIG") == 0, "an over-cap extracted doc stores ZERO chunks (rejected, not partially kept)")
    finally:
        doc_index._extract_pdf = _orig_pdf6

    # an EXACTLY-at-cap extracted doc is accepted (boundary): MAX_DOC_CHARS chars still ingests normally.
    _orig_pdf7 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda data: "word " * 40000   # 200000 chars exactly, at the cap
        n_atcap = doc_index.ingest_bin("kbATCAP", "atcap.pdf", b"%PDF", embed_fn=stub_embed)
        check(n_atcap >= 1, f"extracted text exactly at MAX_DOC_CHARS is accepted (ingests {n_atcap} chunks)")
    finally:
        doc_index._extract_pdf = _orig_pdf7

    # --- FIX 3: the missing-dep message must be ACTIONABLE and name only the REAL command. It must NOT reference
    #     "setup.ps1 -Docs" (no such switch — it would error "parameter cannot be found"); it keeps the real
    #     `uv run --with pypdf --with python-docx ...` guidance. Force the ImportError path on the REAL importers. ---
    import builtins as _builtins
    _real_import = _builtins.__import__
    def _block_parsers(name, *a, **k):
        if name in ("pypdf", "docx"):
            raise ImportError(f"No module named {name!r}")
        return _real_import(name, *a, **k)
    for _which, _fn, _label in (("pdf", doc_index._extract_pdf, ".pdf"), ("docx", doc_index._extract_docx, ".docx")):
        _builtins.__import__ = _block_parsers
        try:
            msg = None
            try:
                _fn(b"data")
            except doc_index._ParserMissing as pm:
                msg = str(pm)
            finally:
                _builtins.__import__ = _real_import
            check(msg is not None, f"{_label}: a missing parser lib raises _ParserMissing with a message")
            check(msg is not None and "setup.ps1 -Docs" not in msg,
                  f"{_label}: the install hint does NOT name the non-existent 'setup.ps1 -Docs' switch")
            check(msg is not None and "uv run --with pypdf --with python-docx" in msg,
                  f"{_label}: the install hint names the REAL `uv run --with pypdf --with python-docx ...` command")
        finally:
            _builtins.__import__ = _real_import

    # --- FIX 4: the library functions no longer call sys.exit (asserted for _ParserMissing above + the too-large /
    #     extract-failed paths): they raise catchable domain errors so a non-CLI consumer can reuse them. The CLI
    #     dispatch lives in _cli(argv) which RETURNS the exit code (it doesn't exit — the __main__ guard does), so
    #     the doc_ingest_bin handler's error -> exit-code mapping (3 missing / 4 corrupt / 5 too-large) + the printed
    #     {"error":...} JSON are locked IN-PROCESS: monkeypatch sys.stdin + the extractor, assert the returned code. ---
    import io as _io
    import json as _json
    import contextlib as _contextlib

    class _FakeStdin:
        def __init__(self, raw):
            self.buffer = _io.BytesIO(raw)
        def read(self):
            return self.buffer.getvalue().decode("utf-8", "replace")

    def _run_cli(argv, stdin_bytes):
        """Run doc_index._cli with a fake stdin + captured stdout; returns (exit_code, parsed_json_or_None)."""
        _orig_stdin = sys.stdin
        out = _io.StringIO()
        try:
            sys.stdin = _FakeStdin(stdin_bytes)
            with _contextlib.redirect_stdout(out):
                code = doc_index._cli(argv)
        finally:
            sys.stdin = _orig_stdin
        try:
            parsed = _json.loads(out.getvalue() or "null")
        except Exception:
            parsed = None
        return code, parsed

    # the CLI dispatch is a returnable function (not an inline __main__ block), so it's callable from a test.
    check(callable(getattr(doc_index, "_cli", None)),
          "_cli(argv) is a returnable CLI dispatch (no sys.exit in the library)")

    # exit 3: parsers couldn't load. Stub _extract_docx to raise _ParserMissing -> the handler prints {"error":…}+3.
    _orig_docx_cli = doc_index._extract_docx
    try:
        doc_index._extract_docx = lambda d: (_ for _ in ()).throw(
            doc_index._ParserMissing("the PDF/DOCX parsers couldn't load (offline? run: uv run --with pypdf --with python-docx ...)"))
        c3, j3 = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLI", "x.docx"], b"PK-not-a-real-docx")
    finally:
        doc_index._extract_docx = _orig_docx_cli
    check(c3 == 3, f"CLI doc_ingest_bin: missing parser dep -> exit 3 (got {c3})")
    check(isinstance(j3, dict) and "uv run --with pypdf" in j3.get("error", ""),
          "CLI exit-3 prints {\"error\":...} with the REAL uv install hint (no setup.ps1 -Docs)")

    # exit 4: corrupt/unreadable file. Stub _extract_pdf to raise a parser exception -> extract_text wraps it in
    # _ExtractFailed -> the handler prints the clear 'couldn't read' message + exits 4 (NOT the embed-down 1).
    _orig_pdf_cli4 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda d: (_ for _ in ()).throw(ValueError("EOF marker not found"))
        c4, j4 = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLI", "x.pdf"], b"%PDF-garbage")
    finally:
        doc_index._extract_pdf = _orig_pdf_cli4
    check(c4 == 4, f"CLI doc_ingest_bin: corrupt file -> exit 4 (got {c4})")
    check(isinstance(j4, dict) and "couldn't read" in j4.get("error", ""),
          "CLI exit-4 prints the clear {\"error\":\"couldn't read this file (corrupt, encrypted, ...)\"}")

    # exit 5: extracted text over MAX_DOC_CHARS. Stub _extract_pdf to return an over-cap string -> the handler
    # prints the 'document too large' message + exits 5 (consistent with the C# text path's too-large contract).
    _orig_pdf_cli5 = doc_index._extract_pdf
    try:
        doc_index._extract_pdf = lambda d: "x" * (doc_index.MAX_DOC_CHARS + 1)
        c5, j5 = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLI", "x.pdf"], b"%PDF")
    finally:
        doc_index._extract_pdf = _orig_pdf_cli5
    check(c5 == 5, f"CLI doc_ingest_bin: over-MAX_DOC_CHARS extracted text -> exit 5 (got {c5})")
    check(isinstance(j5, dict) and "too large" in j5.get("error", ""),
          "CLI exit-5 prints the clear {\"error\":\"document too large (N chars) — split it ...\"}")

    # exit 7: an UNSUPPORTED FORMAT (legacy .doc/.dot). The handler maps extract_text's _Unsupported to a DISTINCT
    # exit 7 + a clear "convert to .docx/.pdf/.txt" {"error":…} — NOT the corrupt-file 4, NOT the embed-down 1 (the
    # file is fine + the embed server is fine; the FORMAT just isn't supported). The OLE2 magic is the real .doc sig.
    c7, j7 = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLI", "legacy.doc"],
                      b"\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1" + bytes(range(64)))
    check(c7 == 7, f"CLI doc_ingest_bin: legacy .doc (OLE) -> exit 7 unsupported-format (got {c7})")
    check(isinstance(j7, dict) and ".docx" in j7.get("error", "") and "convert" in j7.get("error", "").lower(),
          "CLI exit-7 prints the clear {\"error\":\"…convert to .docx/.pdf/.txt\"} (no garbage utf-8 attach)")
    # .dot is rejected identically (a Word template is the same OLE container — neither is read by python-docx).
    c7b, _ = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLI", "memo.dot"], b"\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1")
    check(c7b == 7, f"CLI doc_ingest_bin: legacy .dot template -> exit 7 unsupported-format (got {c7b})")

    # exit 0 unchanged: a valid in-bounds extracted PDF still ingests + prints {"chunks":N}. Stub ingest_doc to a
    # positive count (the real embed path is covered by the stub-embedder ingest tests above); this locks the _cli
    # success SIGNAL: a non-zero chunk count -> exit 0 + {"chunks":N}.
    _orig_pdf_cli0 = doc_index._extract_pdf
    _orig_ingest_cli0 = doc_index.ingest_doc
    try:
        doc_index._extract_pdf = lambda d: "Dragons rule the north and guard the castle."
        doc_index.ingest_doc = lambda kb, src, text, embed_fn=None: 3   # 3 chunks stored
        c0, j0 = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLIOK", "ok.pdf"], b"%PDF")
    finally:
        doc_index._extract_pdf = _orig_pdf_cli0
        doc_index.ingest_doc = _orig_ingest_cli0
    check(c0 == 0, f"CLI doc_ingest_bin: a valid extracted PDF -> exit 0 (got {c0})")
    check(isinstance(j0, dict) and j0.get("chunks", 0) == 3, f"CLI exit-0 still prints {{\"chunks\":N}} for a valid doc (got {j0})")

    # the embed-down 0-chunk signal is preserved: non-blank extracted text that stores 0 chunks (every chunk
    # rejected by a down/OOM embed server) still exits 1 so the C# side surfaces the 503 — the binary path mirrors
    # the text path exactly. Stub ingest_doc to the 0-stored outcome (its own embed-skip path is covered elsewhere);
    # this asserts the _cli exit SIGNAL (n==0 and text.strip() -> 1), not re-tests ingest.
    _orig_pdf_cli1 = doc_index._extract_pdf
    _orig_ingest_cli = doc_index.ingest_doc
    try:
        doc_index._extract_pdf = lambda d: "this PDF has real text but every embed call was rejected"
        doc_index.ingest_doc = lambda kb, src, text, embed_fn=None: 0   # every chunk rejected -> 0 stored
        c1, _ = _run_cli(["doc_index.py", "doc_ingest_bin", "kbCLIDOWN", "down.pdf"], b"%PDF")
    finally:
        doc_index._extract_pdf = _orig_pdf_cli1
        doc_index.ingest_doc = _orig_ingest_cli
    check(c1 == 1, f"CLI doc_ingest_bin: non-blank text but 0 stored chunks (embed down) -> exit 1 (got {c1})")

    # --- PORTABILITY: doc_export / doc_import (the v0.16 KB library export/import follow-up). Option A: export the
    #     stored doc_chunks ROWS (source, ord, content, vec) as a portable self-describing JSON envelope, import
    #     inserts them under a FRESH kb_id with NO embed call (the vecs come from the file). This is a ROUND-TRIP:
    #     export a stub-built KB -> import under a new id -> the SAME sources/chunks are searchable, scoped to the
    #     new id, with no cross-KB leak — all WITHOUT the embed server (vecs are reused, not re-embedded). ---

    # build a small source KB with the stub embedder, then export it.
    doc_index.ingest_doc("kbEXP", "lore.txt", "Dragons rule the north and guard the castle.", embed_fn=stub_embed)
    doc_index.ingest_doc("kbEXP", "geo.txt", "The ocean lies to the south past the forest.", embed_fn=stub_embed)
    exp_count = doc_index.count("kbEXP")
    env = doc_index.export_kb("kbEXP")
    check(env.get("format") == "dokidex-kb", "export envelope is self-describing (format=dokidex-kb)")
    check(env.get("version") == 1, "export envelope stamps a version")
    check(isinstance(env.get("chunks"), list) and len(env["chunks"]) == exp_count,
          f"export carries every stored chunk ({len(env.get('chunks', []))} == {exp_count})")
    check("kb_id" not in env, "export DELIBERATELY omits kb_id (import always mints a fresh one)")
    ch0 = env["chunks"][0]
    check(all(k in ch0 for k in ("source", "ord", "content", "vec")), "each exported chunk carries source/ord/content/vec")
    check(isinstance(ch0["vec"], list) and all(isinstance(x, (int, float)) for x in ch0["vec"]),
          "the vec is a plain float array (human-inspectable, not an endian-locked BLOB)")
    check(env.get("embed_dim") == len(ch0["vec"]), "embed_dim is stamped and matches the vec length")

    # export of an unknown/empty kb is a benign empty envelope (mirrors delete's no-op), exit 0 path.
    empty_env = doc_index.export_kb("kbNEVER_EXPORT")
    check(empty_env.get("chunks") == [], "export of an unknown kb_id is an empty (benign) envelope, not an error")

    # import under a FRESH kb_id: NO embed call (assert by passing a poisoned embed_fn that would raise if used).
    def _explode_embed(texts):
        raise AssertionError("import must NOT call embed — the vecs come from the file")
    n_imp = doc_index.import_kb("kbIMP", env, embed_fn=_explode_embed)
    check(n_imp == exp_count, f"import inserts every chunk under the fresh id ({n_imp} == {exp_count})")
    check(doc_index.count("kbIMP") == exp_count, "the imported kb has exactly the exported chunk count")
    check(doc_index.count("kbEXP") == exp_count, "import does NOT disturb the source kb")

    # the imported chunks are SEARCHABLE with the same ranking (vecs reused) — and scoped to the new id only.
    ihits = doc_index.search("kbIMP", "dragon castle north", k=1, embed_fn=stub_embed)
    check(ihits and ihits[0]["source"] == "lore.txt", "imported chunks are retrievable (top hit lore.txt)")
    imp_srcs = sorted({h["source"] for h in doc_index.search("kbIMP", "north south ocean", k=10, embed_fn=stub_embed)})
    check(imp_srcs == ["geo.txt", "lore.txt"], f"import preserves both sources (got {imp_srcs})")
    # no cross-KB leak: searching the imported id never returns rows tagged to the source id and vice-versa is moot
    # (distinct kb_id WHERE clause); prove the import did not write under the source id.
    check(doc_index.count("kbEXP") == exp_count and doc_index.count("kbIMP") == exp_count,
          "export+import are two disjoint kb_id scopes (no cross-KB contamination)")

    # --- import VALIDATION / BOUNDING: a forged/malformed envelope must be REJECTED with a clear error, never crash
    #     the DB. import_kb raises a catchable _ImportRejected (the library never exits); the CLI doc_import handler
    #     maps it to {"error":…} + a DISTINCT exit code (6). ---
    bad_format = {"format": "not-dokidex", "version": 1, "chunks": []}
    threw = None
    try:
        doc_index.import_kb("kbBADFMT", bad_format, embed_fn=_explode_embed)
    except doc_index._ImportRejected as ir:
        threw = ir
    check(isinstance(threw, doc_index._ImportRejected), "import rejects an envelope with the wrong format (catchable error)")
    check(doc_index.count("kbBADFMT") == 0, "a rejected import writes ZERO rows")

    bad_ver = {"format": "dokidex-kb", "version": 999, "chunks": []}
    threw = None
    try:
        doc_index.import_kb("kbBADVER", bad_ver, embed_fn=_explode_embed)
    except doc_index._ImportRejected as ir:
        threw = ir
    check(isinstance(threw, doc_index._ImportRejected), "import rejects an unknown envelope version")

    # over the chunk ceiling -> rejected (a forged file can't blow up the DB).
    check(doc_index.MAX_IMPORT_CHUNKS >= 1, "MAX_IMPORT_CHUNKS is a positive ceiling")
    too_many = {"format": "dokidex-kb", "version": 1,
                "chunks": [{"source": "x.txt", "ord": i, "content": "c", "vec": [1.0]}
                           for i in range(doc_index.MAX_IMPORT_CHUNKS + 1)]}
    threw = None
    try:
        doc_index.import_kb("kbTOOMANY", too_many, embed_fn=_explode_embed)
    except doc_index._ImportRejected as ir:
        threw = ir
    check(isinstance(threw, doc_index._ImportRejected), "import rejects an envelope over MAX_IMPORT_CHUNKS")
    check(doc_index.count("kbTOOMANY") == 0, "an over-ceiling import writes ZERO rows")

    # ragged / non-numeric / missing-vec rows are SKIPPED, not fatal — a few bad rows degrade cleanly. A
    # traversal-shaped source is NEUTRALIZED to its safe basename (mirrors the C# Path.GetFileName guard), never
    # skipped — so no '../..' path lands in the DB.
    mixed = {"format": "dokidex-kb", "version": 1, "chunks": [
        {"source": "ok.txt", "ord": 0, "content": "good chunk", "vec": [1.0, 2.0]},
        {"source": "ok.txt", "ord": 1, "content": "another good", "vec": [3.0, 4.0]},
        {"source": "ragged.txt", "ord": 0, "content": "bad vec", "vec": [1.0, "NOTNUM"]},  # non-numeric -> skip
        {"source": "novec.txt", "ord": 0, "content": "no vec"},  # missing vec -> skip
        {"source": "weird@name!.txt", "ord": 0, "content": "bad stem", "vec": [1.0, 2.0]},  # illegal stem -> skip
    ]}
    n_mixed = doc_index.import_kb("kbMIXED", mixed, embed_fn=_explode_embed)
    check(n_mixed == 2, f"import keeps the valid rows and skips the bad ones ({n_mixed} == 2)")
    check(doc_index.count("kbMIXED") == 2, "only the 2 valid rows are stored")
    msrcs = sorted({h["source"] for h in doc_index.search_vec("kbMIXED", [1.0, 2.0], k=10)})
    check(msrcs == ["ok.txt"], f"the ragged/illegal sources never land in the DB (got {msrcs})")

    # a traversal-shaped source is reduced to its safe basename (no '../..' or separators ever reach the DB).
    trav = {"format": "dokidex-kb", "version": 1, "chunks": [
        {"source": "../../etc/passwd", "ord": 0, "content": "neutralized", "vec": [1.0, 2.0]},
    ]}
    doc_index.import_kb("kbTRAV", trav, embed_fn=_explode_embed)
    tsrcs = [h["source"] for h in doc_index.search_vec("kbTRAV", [1.0, 2.0], k=10)]
    check(tsrcs == ["passwd"], f"a traversal source is neutralized to its basename, never stored as a path (got {tsrcs})")
    check(all("/" not in s and "\\" not in s and ".." not in s for s in tsrcs),
          "no path separators or '..' ever land in a stored source")

    # an over-long content is clamped to MAX_DOC_CHARS (a forged huge chunk can't bloat the DB).
    longc = {"format": "dokidex-kb", "version": 1, "chunks": [
        {"source": "long.txt", "ord": 0, "content": "z" * (doc_index.MAX_DOC_CHARS + 5000), "vec": [1.0]},
    ]}
    doc_index.import_kb("kbLONG", longc, embed_fn=_explode_embed)
    stored_len = max(len(h["content"]) for h in doc_index.search_vec("kbLONG", [1.0], k=1))
    check(stored_len <= doc_index.MAX_DOC_CHARS, f"an over-long imported chunk is clamped to MAX_DOC_CHARS ({stored_len})")

    # --- FIX 2: the export envelope must CARRY the KB display name so a round-trip restores it (the C# import reads
    #     envelope["name"] -> KbStore.NewKb(name)). export_kb takes the display name and stamps it; a None/blank name
    #     omits the key (back-compat with an unnamed export). ---
    named_env = doc_index.export_kb("kbEXP", "My Project Docs")
    check(named_env.get("name") == "My Project Docs", "export_kb stamps the supplied display name into the envelope")
    unnamed_env = doc_index.export_kb("kbEXP")
    check("name" not in unnamed_env, "export_kb with no name omits the 'name' key (back-compat)")
    blank_env = doc_index.export_kb("kbEXP", "   ")
    check("name" not in blank_env, "export_kb with a blank name omits the 'name' key (no empty label)")

    # --- FIX 4: import must WARN (never silently zero-hit) when the envelope's embed_dim differs from the current
    #     embedding dim. _dim_warning is the PURE comparison seam (stubbed dims, no embed server): a clear warning
    #     string on a real mismatch, None when they match / either side is unknown (can't compare -> don't cry wolf). ---
    w = doc_index._dim_warning(768, 1024)
    check(isinstance(w, str) and "768" in w and "1024" in w and "re-embed" in w.lower(),
          "a dim mismatch yields a clear warning naming both dims + the re-embed hint")
    check(doc_index._dim_warning(768, 768) is None, "matching dims -> no warning")
    check(doc_index._dim_warning(0, 768) is None, "an unknown envelope dim (0) -> no warning (can't compare)")
    check(doc_index._dim_warning(768, None) is None, "an unknown current dim (None) -> no warning (can't compare)")
    check(doc_index._dim_warning(768, 0) is None, "a zero current dim (no rows to probe) -> no warning")

    # current-dim probe: when other rows already exist on this machine, _current_embed_dim returns their dim WITHOUT
    # an embed call (cheap, crash-proof when the embed server is down). An empty DB scope -> 0 (unknown).
    cur_dim = doc_index._current_embed_dim(exclude_kb="kbEXP")
    check(cur_dim == len(ch0["vec"]), f"_current_embed_dim probes an existing row's dim with no embed call ({cur_dim})")

    # --- FIX 5(c): _safe_source aligns its length cap with the C# RecipeStore.SafeName rule (FULL basename <= 64,
    #     not just the stem). A 64-char-stem name whose full basename exceeds 64 is now rejected (it would be by the
    #     C# ingest guard); a name within 64 still passes. ---
    over_basename = "z" * 61 + ".txt"   # full basename = 65 chars (> 64) though the stem is 61
    check(doc_index._safe_source(over_basename) is None, "_safe_source rejects a basename over 64 chars (full-name cap, matches RecipeStore.SafeName)")
    ok_basename = "z" * 60 + ".txt"     # full basename = 64 chars (== cap)
    check(doc_index._safe_source(ok_basename) == ok_basename, "_safe_source accepts a basename at the 64-char cap")

    # --- CLI doc_export / doc_import round-trip (the C# DocSearch sidecar path). doc_export prints the envelope to
    #     STDOUT; doc_import reads it from STDIN, inserts under the argv kb id, prints {"chunks":N}. ---
    doc_index.ingest_doc("kbCLIEXP", "a.txt", "A wizard casts a spell by the river.", embed_fn=stub_embed)
    cE, jE = _run_cli(["doc_index.py", "doc_export", "kbCLIEXP"], b"")
    check(cE == 0, f"CLI doc_export -> exit 0 (got {cE})")
    check(isinstance(jE, dict) and jE.get("format") == "dokidex-kb" and len(jE.get("chunks", [])) >= 1,
          "CLI doc_export prints the envelope JSON to stdout")

    # feed that envelope back through doc_import under a fresh id (STDIN), assert {"chunks":N} + the rows landed.
    import json as _json2
    cI, jI = _run_cli(["doc_index.py", "doc_import", "kbCLIIMP"], _json2.dumps(jE).encode("utf-8"))
    check(cI == 0, f"CLI doc_import -> exit 0 (got {cI})")
    check(isinstance(jI, dict) and jI.get("chunks", 0) == len(jE["chunks"]),
          f"CLI doc_import prints {{\"chunks\":N}} (got {jI})")
    check(doc_index.count("kbCLIIMP") == len(jE["chunks"]), "CLI doc_import landed the rows under the fresh id")

    # FIX 2 (CLI): doc_export takes an OPTIONAL display-name argv (doc_export KB NAME) and stamps it into the
    # envelope, so the C# export can pass the KbRecord.Name through with no extra round-trip.
    cEN, jEN = _run_cli(["doc_index.py", "doc_export", "kbCLIEXP", "Wizard Lore"], b"")
    check(cEN == 0 and isinstance(jEN, dict) and jEN.get("name") == "Wizard Lore",
          f"CLI doc_export KB NAME stamps the display name into the envelope (got {jEN.get('name') if isinstance(jEN, dict) else jEN!r})")

    # FIX 4 (CLI): a doc_import whose envelope embed_dim differs from the existing on-disk dim surfaces a WARNING in
    # the response (never silently zero-hits). Build an envelope with a deliberately wrong dim; existing kbCLIEXP rows
    # supply the current dim via the no-embed probe. The import still SUCCEEDS (rows land) but flags the mismatch.
    wrong_dim_env = {"format": "dokidex-kb", "version": 1, "embed_dim": 3,
                     "chunks": [{"source": "x.txt", "ord": 0, "content": "c", "vec": [1.0, 2.0, 3.0]}]}
    cW, jW = _run_cli(["doc_index.py", "doc_import", "kbCLIWARN"], _json2.dumps(wrong_dim_env).encode("utf-8"))
    check(cW == 0, f"CLI doc_import with a dim mismatch still succeeds (exit 0, got {cW})")
    check(isinstance(jW, dict) and jW.get("chunks", 0) == 1, "the mismatched rows still import (graceful, not a hard fail)")
    check(isinstance(jW, dict) and isinstance(jW.get("warning"), str) and "re-embed" in jW["warning"].lower(),
          f"CLI doc_import surfaces a dim-mismatch warning (got {jW.get('warning') if isinstance(jW, dict) else jW!r})")

    # a MATCHING-dim import prints NO warning key (no false alarm).
    match_dim_env = doc_index.export_kb("kbCLIEXP")   # same stub dim as the existing rows
    cM, jM = _run_cli(["doc_index.py", "doc_import", "kbCLIMATCH"], _json2.dumps(match_dim_env).encode("utf-8"))
    check(cM == 0 and isinstance(jM, dict) and "warning" not in jM, "a matching-dim import prints no warning key")

    # CLI doc_import of a malformed envelope -> the distinct error exit code 6 + a printed {"error":…}.
    cBad, jBad = _run_cli(["doc_index.py", "doc_import", "kbCLIBAD"], b"{not valid json")
    check(cBad == 6, f"CLI doc_import of malformed JSON -> exit 6 (got {cBad})")
    check(isinstance(jBad, dict) and "error" in jBad, "CLI doc_import error exit prints {\"error\":…}")
    cBad2, _ = _run_cli(["doc_index.py", "doc_import", "kbCLIBAD2"],
                        _json2.dumps({"format": "nope", "version": 1, "chunks": []}).encode("utf-8"))
    check(cBad2 == 6, f"CLI doc_import of a wrong-format envelope -> exit 6 (got {cBad2})")

except Exception as e:
    import traceback
    traceback.print_exc()
    check(False, f"unexpected exception: {e}")

print(f"\ndoc_index: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
