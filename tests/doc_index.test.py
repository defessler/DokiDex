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

except Exception as e:
    import traceback
    traceback.print_exc()
    check(False, f"unexpected exception: {e}")

print(f"\ndoc_index: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
