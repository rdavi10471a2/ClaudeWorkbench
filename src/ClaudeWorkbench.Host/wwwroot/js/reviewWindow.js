export function tryCloseReviewWindow() {
  try {
    window.close();
  } catch {
    // Ignore close failures; some hosts block script-initiated window close.
  }
}
