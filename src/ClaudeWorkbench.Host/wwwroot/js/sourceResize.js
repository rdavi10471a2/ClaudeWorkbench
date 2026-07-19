export function attachSourceSplitter(layout, sidebar, detail, splitter) {
    if (!layout || !sidebar || !detail || !splitter) {
        return;
    }

    let startX = 0;
    let startSidebarWidth = 0;
    let startDetailWidth = 0;

    const minSidebar = 260;
    const minDetail = 460;

    function onPointerMove(event) {
        const delta = event.clientX - startX;
        const nextSidebar = Math.max(minSidebar, startSidebarWidth + delta);
        const nextDetail = Math.max(minDetail, startDetailWidth - delta);
        sidebar.style.flex = `0 0 ${nextSidebar}px`;
        detail.style.flex = `0 0 ${nextDetail}px`;
        sidebar.style.flexBasis = `${nextSidebar}px`;
        detail.style.flexBasis = `${nextDetail}px`;
        sidebar.style.width = `${nextSidebar}px`;
        detail.style.width = `${nextDetail}px`;
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
        startSidebarWidth = sidebar.getBoundingClientRect().width;
        startDetailWidth = detail.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
}

export function attachGitSplitter(layout, sidebar, detail, splitter) {
    if (!layout || !sidebar || !detail || !splitter) {
        return;
    }

    let startX = 0;
    let startSidebarWidth = 0;
    let startDetailWidth = 0;

    const minSidebar = 280;
    const minDetail = 380;

    function onPointerMove(event) {
        const delta = event.clientX - startX;
        const nextSidebar = Math.max(minSidebar, startSidebarWidth + delta);
        const nextDetail = Math.max(minDetail, startDetailWidth - delta);
        sidebar.style.flex = `0 0 ${nextSidebar}px`;
        detail.style.flex = `1 1 auto`;
        sidebar.style.width = `${nextSidebar}px`;
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
        startSidebarWidth = sidebar.getBoundingClientRect().width;
        startDetailWidth = detail.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
}

export function attachMainSplitter(layout, connection, workPanel, splitter) {
    if (!layout || !connection || !workPanel || !splitter || splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startX = 0;
    let startConnectionWidth = 0;
    const minConnection = 260;
    const maxConnection = 620;

    function onPointerMove(event) {
        const delta = event.clientX - startX;
        const nextConnection = Math.min(maxConnection, Math.max(minConnection, startConnectionWidth + delta));
        connection.style.flexBasis = `${nextConnection}px`;
        connection.style.width = `${nextConnection}px`;
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
        startConnectionWidth = connection.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
}

export function attachTaskSplitter(layout, navigator, workspace, splitter) {
    if (!layout || !navigator || !workspace || !splitter || splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startX = 0;
    let startNavigatorWidth = 0;
    const minNavigator = 280;
    const maxNavigator = 680;

    function onPointerMove(event) {
        const delta = event.clientX - startX;
        const nextNavigator = Math.min(maxNavigator, Math.max(minNavigator, startNavigatorWidth + delta));
        navigator.style.flexBasis = `${nextNavigator}px`;
        navigator.style.width = `${nextNavigator}px`;
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
        startNavigatorWidth = navigator.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
}

export function attachAssistantSplitter(layout, transcript, composer, splitter) {
    if (!layout || !transcript || !composer || !splitter || splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";
    splitter.dataset.userSized = splitter.dataset.userSized || "false";

    let startY = 0;
    let startComposerHeight = 0;
    let layoutHeight = 0;
    let splitterHeight = 12;

    const minTranscript = 120;
    const minComposer = 156;
    const maxComposerDefault = 188;

    function applyAssistantLayout(defaultComposerHeight) {
        layoutHeight = layout.getBoundingClientRect().height;
        splitterHeight = Math.max(8, splitter.getBoundingClientRect().height || 12);
        if (!layoutHeight || layoutHeight <= splitterHeight + minTranscript + minComposer) {
            return;
        }

        const maxComposer = Math.max(minComposer, layoutHeight - splitterHeight - minTranscript);
        const nextComposer = Math.min(maxComposer, Math.max(minComposer, defaultComposerHeight));
        const nextTranscript = Math.max(minTranscript, layoutHeight - splitterHeight - nextComposer);
        layout.style.gridTemplateRows = `${nextTranscript}px ${splitterHeight}px ${nextComposer}px`;
    }

    function applyDefaultLayout() {
        const nextLayoutHeight = layout.getBoundingClientRect().height;
        if (!nextLayoutHeight) {
            return;
        }

        const proportionalComposer = Math.round(nextLayoutHeight * 0.18);
        const desiredComposer = Math.min(maxComposerDefault, Math.max(minComposer, proportionalComposer));
        applyAssistantLayout(desiredComposer);
    }

    function onPointerMove(event) {
        const delta = event.clientY - startY;
        const maxComposer = Math.max(minComposer, layoutHeight - splitterHeight - minTranscript);
        const nextComposer = Math.min(maxComposer, Math.max(minComposer, startComposerHeight - delta));
        const nextTranscript = Math.max(minTranscript, layoutHeight - splitterHeight - nextComposer);
        layout.style.gridTemplateRows = `${nextTranscript}px ${splitterHeight}px ${nextComposer}px`;
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
        startY = event.clientY;
        layoutHeight = layout.getBoundingClientRect().height;
        splitterHeight = Math.max(8, splitter.getBoundingClientRect().height || 12);
        startComposerHeight = composer.getBoundingClientRect().height;
        splitter.dataset.userSized = "true";
        splitter.classList.add("dragging");
        document.body.style.cursor = "row-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });

    const resizeHandler = () => {
        if (splitter.dataset.userSized === "true") {
            const currentComposerHeight = composer.getBoundingClientRect().height;
            if (currentComposerHeight > 0) {
                applyAssistantLayout(currentComposerHeight);
            }

            return;
        }

        applyDefaultLayout();
    };

    if (!layout.__codingServicesAssistantResizeHandler) {
        layout.__codingServicesAssistantResizeHandler = resizeHandler;
        window.addEventListener("resize", resizeHandler);
    }

    applyDefaultLayout();
}

export function attachStreamSplitter(layout, history, stream, splitter) {
    if (!layout || !history || !stream || !splitter || splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startX = 0;
    let startHistoryWidth = 0;
    let startStreamWidth = 0;

    const minHistory = 420;
    const minStream = 260;

    function onPointerMove(event) {
        const delta = event.clientX - startX;
        const nextHistory = Math.max(minHistory, startHistoryWidth + delta);
        const nextStream = Math.max(minStream, startStreamWidth - delta);
        layout.style.gridTemplateColumns = `${nextHistory}px 12px ${nextStream}px`;
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
        startHistoryWidth = history.getBoundingClientRect().width;
        startStreamWidth = stream.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
}

export function attachAgentSplitter(panel, stream, actions, splitter) {
    if (!panel || !stream || !actions || !splitter || splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startY = 0;
    let startStreamHeight = 0;
    let startActionsHeight = 0;

    const minStream = 160;
    const minActions = 120;

    function onPointerMove(event) {
        const delta = event.clientY - startY;
        const nextStream = Math.max(minStream, startStreamHeight + delta);
        const nextActions = Math.max(minActions, startActionsHeight - delta);
        stream.style.flexBasis = `${nextStream}px`;
        stream.style.height = `${nextStream}px`;
        actions.style.flexBasis = `${nextActions}px`;
        actions.style.height = `${nextActions}px`;
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
        startY = event.clientY;
        startStreamHeight = stream.getBoundingClientRect().height;
        startActionsHeight = actions.getBoundingClientRect().height;
        splitter.classList.add("dragging");
        document.body.style.cursor = "row-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });
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

export function scrollComposerToBottom(textarea) {
    if (!textarea) {
        return;
    }

    window.requestAnimationFrame(() => {
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

export function attachTranscriptCopyButtons(transcriptElement) {
    if (!transcriptElement || transcriptElement.__codingServicesCopyAttached) {
        return;
    }

    transcriptElement.__codingServicesCopyAttached = true;
    transcriptElement.addEventListener("click", async event => {
        const button = event.target && event.target.closest
            ? event.target.closest(".message-copy")
            : null;
        if (!button || !transcriptElement.contains(button)) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        const text = button.getAttribute("data-copy-text") || "";
        await copyTextToClipboard(text);
        const originalText = button.textContent;
        button.textContent = "Copied";
        window.setTimeout(() => {
            button.textContent = originalText || "Copy";
        }, 1200);
    });
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

export function openUrlInNewTab(url) {
    if (!url) {
        return;
    }

    const popup = window.open(url, "_blank");
    if (!popup) {
        return;
    }

    popup.opener = null;
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
