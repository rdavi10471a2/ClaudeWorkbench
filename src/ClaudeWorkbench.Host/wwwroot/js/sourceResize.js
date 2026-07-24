// ---------------------------------------------------------------------------------------------
// Column splitters
//
// ONE rule, and every past bug here came from breaking it: SIZE THE LEADING PANE ONLY.
//
// The trailing pane must stay `flex: 1 1 auto; min-width: 0` so it consumes exactly what is left.
// The moment you also pin the trailing pane to a pixel width, the two panes stop being related to
// the container: drag the divider right, or shrink the window, and leading + trailing exceed the
// layout width. The overflow goes off the right edge — and because the layout is `overflow: hidden`,
// it is silently clipped rather than scrolled. That took the transcript's vertical scrollbar
// off-screen with it, which is why "the chat lost its scrollbar" and "the right side runs off the
// screen" were one bug, not two.
//
// So the leading width is clamped against what the container can actually give:
//     max leading = layout width - splitter width - minTrailing
// which means the divider stops before it can push the trailing pane out, instead of being free to
// run past the edge.
//
// The same clamp is re-applied when the layout resizes (ResizeObserver, not a window listener —
// it is owned by the element, so it dies with the element instead of accumulating one dead global
// handler per tab switch). Shrinking the window therefore pulls the divider back in rather than
// shoving the trailing pane off-screen.
function attachColumnSplitter(layout, leading, trailing, splitter, minLeading, minTrailing) {
    // Every element must be a REAL DOM element, not just truthy. Radzen renders every tab's content
    // (TabRenderMode.Client), so a hidden tab's OnAfterRender can fire with a ref that marshals to a
    // truthy-but-not-Element value while the panel isn't realized yet — `splitter.dataset` is then
    // undefined and the old `!splitter` check let it through, throwing into the shared circuit and
    // taking the whole app down. instanceof Element rejects those; the splitter attaches on a later,
    // real render instead.
    if (!(layout instanceof Element) || !(leading instanceof Element)
        || !(trailing instanceof Element) || !(splitter instanceof Element)) {
        return;
    }

    // Keyed on the element, not on a C# bool in the caller: the element is what actually carries
    // the listener, so a re-created layout gets a fresh splitter and re-attaches correctly, while
    // a re-render of the same element does not stack a second pointerdown handler.
    if (splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startX = 0;
    let startLeadingWidth = 0;

    function clamp(width) {
        const splitterWidth = splitter.getBoundingClientRect().width || 12;
        const available = layout.clientWidth - splitterWidth;

        // A container too small to honour both minimums cannot be satisfied; keep the leading pane
        // at its minimum and let the trailing pane take the remainder. Panes declare their own
        // internal overflow, so the content scrolls instead of the layout overflowing.
        const maxLeading = Math.max(minLeading, available - minTrailing);
        return Math.min(maxLeading, Math.max(minLeading, width));
    }

    function apply(width) {
        const next = clamp(width);
        leading.style.flex = `0 0 ${next}px`;
        leading.style.width = `${next}px`;

        // Explicitly (re)assert the trailing contract. Cheap, and it repairs any pinned width left
        // behind by an older build or a previously shared splitter implementation.
        trailing.style.flex = "1 1 auto";
        trailing.style.width = "";
        trailing.style.minWidth = "0";
    }

    function onPointerMove(event) {
        apply(startLeadingWidth + (event.clientX - startX));
    }

    function onPointerUp() {
        splitter.classList.remove("dragging");
        document.body.style.cursor = "";
        document.body.style.userSelect = "";
        window.removeEventListener("pointermove", onPointerMove);
        window.removeEventListener("pointerup", onPointerUp);
    }

    splitter.addEventListener("pointerdown", event => {
        event.preventDefault();
        startX = event.clientX;
        startLeadingWidth = leading.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });

    if (typeof ResizeObserver === "function") {
        const observer = new ResizeObserver(() => {
            const current = leading.getBoundingClientRect().width;
            if (current > 0) {
                apply(current);
            }
        });

        observer.observe(layout);
        splitter.__columnSplitterObserver = observer;
    }
}

// Source tab: file tree on the left, file detail on the right.
export function attachSourceSplitter(layout, sidebar, detail, splitter) {
    attachColumnSplitter(layout, sidebar, detail, splitter, 260, 460);
}

// Git tab: changed-files list on the left, diff on the right.
export function attachGitSplitter(layout, sidebar, detail, splitter) {
    attachColumnSplitter(layout, sidebar, detail, splitter, 280, 380);
}

// Workbench tab: composer on the left, chat history on the right.
//
// This previously called attachSourceSplitter directly. Borrowing another tab's splitter is what
// dragged the Source tab's "pin both panes" behaviour into the chat layout; the minimums were also
// the Source tab's, so the chat history reserved 460px it did not need. It gets its own entry point
// with its own minimums now, even though the mechanism is shared.
export function attachAssistantSplitter(layout, composer, transcript, splitter) {
    attachColumnSplitter(layout, composer, transcript, splitter, 320, 360);
}

// DiffView: two fixed 50/50 panes that share ONE horizontal scrollbar. The body owns the shared
// VERTICAL bar natively (both panes scroll vertically together). Horizontal is a single dedicated
// bar (hbar) whose track (hbarInner) is widened to the longest line, so one bottom bar spans the
// wider pane's range and drives BOTH panes' scrollLeft together. Element-keyed so re-renders do not
// stack handlers; a ResizeObserver re-measures when content changes.
export function attachDiffHScroll(leftPane, rightPane, hbar, hbarInner) {
    if (!leftPane || !rightPane || !hbar || !hbarInner) {
        return;
    }

    // Blazor re-creates the panes on re-render/resize. A one-time listener that closed over the
    // FIRST refs would then drive stale, detached elements -- the bar goes dead and "does not come
    // back". So stash the CURRENT elements on the bar and read them dynamically every time.
    hbar.__diffLeft = leftPane;
    hbar.__diffRight = rightPane;
    hbar.__diffInner = hbarInner;

    function measure() {
        const l = hbar.__diffLeft;
        const r = hbar.__diffRight;
        const inner = hbar.__diffInner;
        if (!l || !r || !inner) {
            return;
        }
        // The bar spans the FULL width (both panes), but each pane is only ~half that wide. So we
        // cannot size the track to the raw scrollWidth and map bar-pixels onto the pane 1:1 -- the
        // bar's range would come out far shorter than the pane's, and dragging it to the end would
        // leave a long line only part-scrolled (the "shrink the window, math is wrong" case).
        //
        // Correct rule: the bar's OWN scroll range must equal the pane's overflow. So
        //     track = visible width + how far the content overflows a pane
        // Then bar.scrollLeft (0..overflow) drives pane.scrollLeft (0..overflow) exactly, and the
        // bar only appears at all when a pane actually overflows (overflow > 0).
        const overflow = Math.max(
            l.scrollWidth - l.clientWidth,
            r.scrollWidth - r.clientWidth,
            0
        );
        inner.style.width = (hbar.clientWidth + overflow) + "px";
    }

    if (hbar.dataset.diffHAttached !== "true") {
        hbar.dataset.diffHAttached = "true";

        hbar.addEventListener("scroll", () => {
            const l = hbar.__diffLeft;
            const r = hbar.__diffRight;
            if (l) { l.scrollLeft = hbar.scrollLeft; }
            if (r) { r.scrollLeft = hbar.scrollLeft; }
        });

        window.addEventListener("resize", () => window.requestAnimationFrame(measure));
    }

    // Re-observe the CURRENT panes; a prior observer may be watching detached ones.
    if (typeof ResizeObserver === "function") {
        if (hbar.__diffHObserver) {
            hbar.__diffHObserver.disconnect();
        }
        const observer = new ResizeObserver(() => measure());
        observer.observe(leftPane);
        observer.observe(rightPane);
        hbar.__diffHObserver = observer;
    }

    // Measure now and again after layout settles (first paint can report a stale scrollWidth).
    measure();
    window.requestAnimationFrame(measure);
}

export function attachComposerAutoScroll(textarea) {
    if (!textarea || textarea.dataset.autoScrollAttached === "true") {
        return;
    }

    textarea.dataset.autoScrollAttached = "true";
    textarea.addEventListener("input", () => {
        textarea.scrollTop = textarea.scrollHeight;
    });
}


export function scrollElementToBottom(element) {
    if (!element) {
        return;
    }

    const scroll = (attempt = 0) => {
        window.requestAnimationFrame(() => {
            element.scrollTop = element.scrollHeight;
            if (attempt < 4) {
                window.setTimeout(() => scroll(attempt + 1), 40);
            }
        });
    };

    scroll();
}

export async function copyTextToClipboard(text) {
    if (!text) {
        return;
    }

    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return;
    }

    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.left = "-9999px";
    textarea.style.top = "0";
    document.body.appendChild(textarea);
    textarea.select();
    document.execCommand("copy");
    document.body.removeChild(textarea);
}


export function openHtmlDocument(html, title) {
    const popup = window.open("", "_blank");
    if (!popup) {
        return;
    }

    popup.opener = null;
    popup.document.open();
    popup.document.write(html || "");
    popup.document.title = title || popup.document.title;
    popup.document.close();
}


export function setBeforeUnloadGuard(enabled, message) {
    if (enabled) {
        window.__codingServicesBeforeUnloadMessage = message || "Refreshing will reset the current Coding Services session.";
        if (!window.__codingServicesBeforeUnloadHandler) {
            window.__codingServicesBeforeUnloadHandler = event => {
                event.preventDefault();
                event.returnValue = window.__codingServicesBeforeUnloadMessage;
                return window.__codingServicesBeforeUnloadMessage;
            };
            window.addEventListener("beforeunload", window.__codingServicesBeforeUnloadHandler);
        }

        return;
    }

    if (window.__codingServicesBeforeUnloadHandler) {
        window.removeEventListener("beforeunload", window.__codingServicesBeforeUnloadHandler);
        window.__codingServicesBeforeUnloadHandler = null;
    }

    window.__codingServicesBeforeUnloadMessage = "";
}


// --- chat code-block syntax highlighting -------------------------------------------------------
// A lightweight, self-hosted highlighter for transcript code blocks. Monaco is too heavy to spin
// up per inline block and a CDN highlighter would fight the CSP, so this tokenizes comments,
// strings, numbers, and a per-language keyword set; everything else stays plain. Idempotent
// (dataset guard) so re-renders don't double-wrap. Errors fall back to leaving the block as-is.
const HL_KEYWORDS = {
    csharp: "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield nameof when where",
    sql: "select from where join inner left right full outer cross apply on group by having order asc desc insert into values update set delete create table view index alter drop add column primary key foreign references not null default distinct union all as and or in exists between like is case when then else end with cast convert count sum avg min max top limit offset over partition begin commit rollback declare",
    javascript: "await async function return if else for while do break continue const let var new class extends super this typeof instanceof in of null undefined true false void delete try catch finally throw switch case default yield import export from get set static",
    json: "true false null",
    bash: "if then else elif fi for while do done case esac function return in export local set echo cd",
};

function hlKeywordSet(lang) {
    const alias = { cs: "csharp", ts: "javascript", typescript: "javascript", js: "javascript",
        sh: "bash", powershell: "bash", ps1: "bash", jsonc: "json" };
    const words = HL_KEYWORDS[alias[lang] || lang];
    return words ? new Set(words.split(" ")) : null;
}

function hlEscape(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function hlLineComment(lang) {
    if (lang === "sql") return "--";
    if (["bash", "sh", "powershell", "ps1", "python", "py", "yaml", "yml", "ini", "toml"].includes(lang)) return "#";
    return "//";
}

export function highlightCodeBlocks(container) {
    if (!container) {
        return;
    }

    container.querySelectorAll("pre code").forEach(block => {
        try {
            if (block.dataset.hl === "1") {
                return;
            }
            // A ```mermaid fence is a diagram, not code — renderMermaidBlocks owns it.
            if (/\blanguage-mermaid\b/i.test(block.className || "")) {
                return;
            }
            block.dataset.hl = "1";
            const match = (block.className || "").match(/language-([a-z0-9#+]+)/i);
            let lang = match ? match[1].toLowerCase() : "";
            if (lang === "c#") {
                lang = "csharp";
            }
            block.innerHTML = hlTokenize(block.textContent || "", lang);
        } catch (e) {
            // Leave the block as plain text on any tokenizer error.
        }
    });
}

function hlTokenize(code, lang) {
    const kw = hlKeywordSet(lang);
    const lc = hlLineComment(lang).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    // Priority: line comment, block comment, string, number, identifier, single char.
    const re = new RegExp(
        "(" + lc + "[^\\n]*)" +
        "|(/\\*[\\s\\S]*?\\*/)" +
        "|(\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`)" +
        "|(\\b\\d[\\d_.]*\\b)" +
        "|([A-Za-z_$][\\w$]*)" +
        "|([\\s\\S])", "g");
    let out = "";
    let m;
    while ((m = re.exec(code)) !== null) {
        if (m[1] || m[2]) {
            out += '<span class="hl-com">' + hlEscape(m[0]) + "</span>";
        } else if (m[3]) {
            out += '<span class="hl-str">' + hlEscape(m[0]) + "</span>";
        } else if (m[4]) {
            out += '<span class="hl-num">' + hlEscape(m[0]) + "</span>";
        } else if (m[5]) {
            const token = m[0];
            const isKeyword = kw && kw.has(lang === "sql" ? token.toLowerCase() : token);
            out += isKeyword ? '<span class="hl-kw">' + hlEscape(token) + "</span>" : hlEscape(token);
        } else {
            out += hlEscape(m[0]);
        }
    }
    return out;
}

// --- Mermaid diagrams -------------------------------------------------------
// A ```mermaid fence in model output becomes <pre><code class="language-mermaid">.
// We render it to inline SVG with the locally-vendored mermaid bundle (loaded as a
// classic <script>, which sets window.mermaid). securityLevel:'strict' matters here:
// the diagram source is UNTRUSTED model output, so labels are sanitized and click/script
// directives are inert. On any parse error we leave the raw text visible, never a blank.
let mermaidReady = false;
let mermaidSeq = 0;

function ensureMermaid() {
    if (!window.mermaid) {
        return false;
    }
    if (!mermaidReady) {
        window.mermaid.initialize({
            startOnLoad: false,
            securityLevel: "strict",
            theme: "default",
            fontFamily: "inherit",
        });
        mermaidReady = true;
    }
    return true;
}

// The vendored bundle is ~3.5MB, so the first render pass can fire before window.mermaid
// finishes parsing. Poll briefly rather than give up (and no-op forever) on that race.
async function waitForMermaid(tries) {
    for (let i = 0; i < tries; i++) {
        if (window.mermaid) {
            return true;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    return !!window.mermaid;
}

export async function renderMermaidBlocks(container) {
    if (!container) {
        return;
    }

    // Markdig's advanced extensions render a ```mermaid fence as <pre class="mermaid">SOURCE</pre>
    // (its built-in diagram extension) — NOT <pre><code class="language-mermaid">. Match that.
    const blocks = container.querySelectorAll("pre.mermaid");
    if (blocks.length === 0) {
        return;
    }

    const loaded = window.mermaid ? true : await waitForMermaid(20);
    for (const pre of blocks) {
        if (pre.dataset.mermaid === "1") {
            continue;
        }

        const source = pre.textContent || "";

        if (!loaded) {
            // Bundle never arrived (404 / global not set). Leave a visible note instead of
            // silently showing the raw fence, and allow a later pass to retry.
            const note = document.createElement("div");
            note.className = "mermaid-error";
            note.textContent = "⚠ Mermaid did not load (window.mermaid undefined) — diagram not rendered.";
            pre.replaceWith(note);
            continue;
        }

        pre.dataset.mermaid = "1";
        ensureMermaid();
        const host = document.createElement("div");
        host.className = "mermaid-rendered";

        try {
            mermaidSeq += 1;
            const { svg } = await window.mermaid.render("mmd-" + mermaidSeq, source);
            host.innerHTML = svg;
            pre.replaceWith(host);
        } catch (e) {
            // The diagram itself is malformed: surface the parse error and keep the source.
            const note = document.createElement("div");
            note.className = "mermaid-error";
            note.textContent = "⚠ Mermaid parse error: " + (e && e.message ? e.message : e);
            const raw = document.createElement("pre");
            raw.textContent = source;
            pre.replaceWith(note, raw);
        }
    }
}
