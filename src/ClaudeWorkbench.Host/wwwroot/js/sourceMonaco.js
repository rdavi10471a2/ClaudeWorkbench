// Persistent, in-app Monaco source viewer — replaces the old per-file <iframe srcdoc>.
//
// This is the canonical "Monaco in a page" setup (the same one VS Code / the monaco-editor samples /
// BlazorMonaco use): load the vendored AMD loader once, require editor.main, then create ONE editor in
// a div and swap its model per file. Read-only — this is a browser, not an editor; the agent authors.
//
// One instance + model-swapping (instead of reloading Monaco per file) is also what later lets us
// register index-backed providers once and drive peek/go-to-definition, and call back into Blazor for
// cross-file jumps — none of which an srcdoc iframe could do cleanly.

let loaderPromise = null;

// Load the AMD loader + editor.main exactly once. window.monaco is the ready signal.
function loadMonaco(baseUrl) {
    if (window.monaco) {
        return Promise.resolve();
    }

    if (loaderPromise) {
        return loaderPromise;
    }

    loaderPromise = new Promise((resolve, reject) => {
        // Workers run the language services off the UI thread; point them at the vendored copy.
        window.MonacoEnvironment = {
            getWorkerUrl: function () {
                const src = 'self.MonacoEnvironment={baseUrl:"' + baseUrl + 'lib/monaco/"};'
                    + 'importScripts("' + baseUrl + 'lib/monaco/vs/base/worker/workerMain.js");';
                return URL.createObjectURL(new Blob([src], { type: 'text/javascript' }));
            }
        };

        const script = document.createElement('script');
        script.src = baseUrl + 'lib/monaco/vs/loader.js';
        script.onload = () => {
            // The AMD loader defines a global require; Blazor doesn't use AMD, so this is safe.
            window.require.config({ paths: { vs: baseUrl + 'lib/monaco/vs' } });
            window.require(['vs/editor/editor.main'], () => resolve(), reject);
        };
        script.onerror = () => reject(new Error('Failed to load Monaco loader from ' + script.src));
        document.head.appendChild(script);
    });

    return loaderPromise;
}

export async function initEditor(container, baseUrl) {
    if (!(container instanceof Element)) {
        return;
    }

    try {
        await loadMonaco(baseUrl);
    } catch (error) {
        container.textContent = (error && error.message) ? error.message : String(error);
        container.style.font = '13px Consolas, monospace';
        container.style.padding = '12px';
        container.style.whiteSpace = 'pre-wrap';
        return;
    }

    if (container.__monacoEditor) {
        return;
    }

    container.__monacoEditor = monaco.editor.create(container, {
        value: '',
        language: 'plaintext',
        theme: 'vs',
        readOnly: true,
        automaticLayout: true,
        minimap: { enabled: false },
        lineNumbers: 'on',
        scrollBeyondLastLine: false,
        wordWrap: 'off',
        fontFamily: 'Cascadia Code, Consolas, monospace',
        fontSize: 13,
        renderWhitespace: 'selection',
        // Kill the 1px overview-ruler edge line + scrollbar shadow that otherwise double up on the
        // container's own border and read as an overlapping outline on the right edge.
        overviewRulerBorder: false,
        hideCursorInOverviewRuler: true,
        scrollbar: {
            verticalScrollbarSize: 12,
            horizontalScrollbarSize: 12,
            useShadows: false
        }
    });
    container.__monacoDecorations = [];
}

// Swap the editor to `path`'s content and reveal `line`. Reuses the model for a path if it already
// exists (keyed by file URI) so re-opening the same file is cheap and preserves scroll history.
export function openFile(container, path, text, language, line) {
    if (!(container instanceof Element) || !container.__monacoEditor) {
        return;
    }

    const editor = container.__monacoEditor;
    const uri = monaco.Uri.parse('file:///' + String(path || 'source.txt').replaceAll('\\', '/'));

    let model = monaco.editor.getModel(uri);
    if (model) {
        if (model.getValue() !== text) {
            model.setValue(text || '');
        }
        monaco.editor.setModelLanguage(model, language || 'plaintext');
    } else {
        model = monaco.editor.createModel(text || '', language || 'plaintext', uri);
    }

    if (editor.getModel() !== model) {
        editor.setModel(model);
    }

    const maxLine = Math.max(1, model.getLineCount());
    const selected = Math.min(Math.max(Number(line || 1), 1), maxLine);
    editor.setSelection(new monaco.Range(selected, 1, selected, 1));
    editor.revealLineInCenter(selected);
    container.__monacoDecorations = editor.deltaDecorations(container.__monacoDecorations || [], [{
        range: new monaco.Range(selected, 1, selected, 1),
        options: { isWholeLine: true, className: 'selected-source-line' }
    }]);
}

// Force a layout pass — used when the editor becomes visible again after being hidden (e.g. toggling
// back from a rendered-markdown pane), where it may otherwise have measured itself at 0x0.
export function relayout(container) {
    if (container && container.__monacoEditor) {
        container.__monacoEditor.layout();
    }
}

export function dispose(container) {
    if (container && container.__monacoEditor) {
        container.__monacoEditor.dispose();
        container.__monacoEditor = null;
        container.__monacoDecorations = [];
    }
}
