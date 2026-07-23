// Composer paste/drop attachments. Captures RAW image or text data — a screenshot pasted with
// Ctrl+V (no file on disk), text pasted into the zone, or files dropped onto it — and POSTs the
// bytes to /uploads/paste. The server lands them in the workspace uploads/ folder and returns the
// saved path; we hand that back to the AssistantTab component (via the DotNet ref) so it shows as
// a normal attachment. Blazor Server would otherwise have to push these bytes over the SignalR
// circuit; a direct fetch keeps the binary off it.

export function initComposerAttach(dropZone, textarea, dotNetRef) {
    if (!dropZone) {
        return;
    }

    const swallow = (e) => { e.preventDefault(); e.stopPropagation(); };

    ["dragenter", "dragover"].forEach((name) =>
        dropZone.addEventListener(name, (e) => { swallow(e); dropZone.classList.add("dragover"); }));
    ["dragleave", "dragend"].forEach((name) =>
        dropZone.addEventListener(name, (e) => { swallow(e); dropZone.classList.remove("dragover"); }));

    dropZone.addEventListener("drop", async (e) => {
        swallow(e);
        dropZone.classList.remove("dragover");
        const files = Array.from(e.dataTransfer?.files ?? []);
        for (const file of files) {
            await uploadBlob(file, file.name, dotNetRef);
        }
    });

    // Paste INTO THE ZONE via a transparent <textarea> overlay: images become image attachments,
    // text becomes a .txt attachment. The overlay is what makes the RIGHT-CLICK "Paste" menu item
    // work — a browser only enables it for an editable target, so a bare div gets Ctrl+V but a
    // greyed-out context menu. We swallow the paste so nothing is actually inserted, and keep the
    // catcher empty so it never behaves like a text field. This is the "attach it as a file"
    // surface, distinct from the message textarea (which keeps native text paste to the prompt).
    const catcher = dropZone.querySelector(".paste-catch");
    if (catcher) {
        catcher.addEventListener("paste", async (e) => {
            const hadImage = await handleImageItems(e.clipboardData, dotNetRef);
            if (hadImage) {
                e.preventDefault();
            } else {
                const text = e.clipboardData?.getData("text/plain");
                if (text && text.length > 0) {
                    e.preventDefault();
                    await uploadBlob(new Blob([text], { type: "text/plain" }), pastedName("text/plain"), dotNetRef);
                }
            }
            catcher.value = "";
        });
        catcher.addEventListener("input", () => { catcher.value = ""; });
        dropZone.addEventListener("click", () => catcher.focus());
    }

    // Convenience: pasting an IMAGE anywhere in the message box also attaches it (a screenshot has
    // no sensible text representation). Text paste in the textarea is left untouched — it goes into
    // the prompt as before.
    if (textarea) {
        textarea.addEventListener("paste", async (e) => {
            if (await handleImageItems(e.clipboardData, dotNetRef)) {
                e.preventDefault();
            }
        });
    }
}

async function handleImageItems(clipboardData, dotNetRef) {
    const items = Array.from(clipboardData?.items ?? []);
    let handled = false;
    for (const item of items) {
        if (item.kind === "file" && item.type.startsWith("image/")) {
            const blob = item.getAsFile();
            if (blob) {
                await uploadBlob(blob, pastedName(item.type), dotNetRef);
                handled = true;
            }
        }
    }
    return handled;
}

function pastedName(type) {
    const map = {
        "image/png": ".png", "image/jpeg": ".jpg", "image/gif": ".gif", "image/webp": ".webp",
        "image/bmp": ".bmp", "image/svg+xml": ".svg", "image/x-icon": ".ico", "image/avif": ".avif",
        "text/plain": ".txt",
    };
    const extension = map[type] ?? (type.startsWith("image/") ? ".png" : ".txt");
    const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    return `pasted-${stamp}${extension}`;
}

async function uploadBlob(blob, name, dotNetRef) {
    try {
        const response = await fetch("/uploads/paste?name=" + encodeURIComponent(name), {
            method: "POST",
            headers: { "Content-Type": blob.type || "application/octet-stream" },
            body: blob,
        });
        if (!response.ok) {
            await dotNetRef.invokeMethodAsync("ReportUploadError", `Attachment upload failed (${response.status}).`);
            return;
        }

        const data = await response.json();
        await dotNetRef.invokeMethodAsync("AddUploadedAttachment", data.name, data.path);
    } catch (error) {
        await dotNetRef.invokeMethodAsync("ReportUploadError", String(error));
    }
}
