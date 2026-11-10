export function stopMedia(dialog) {
    dialog?.querySelectorAll("audio, video").forEach(player => {
        player.pause();
        player.removeAttribute("src");
        player.load();
    });
}

export function show(dialog, receiver) {
    if (!dialog?.isConnected) return;
    if (!dialog.dataset.galleryBound) {
        dialog.dataset.galleryBound = "true";
        dialog.addEventListener("cancel", event => {
            event.preventDefault();
            stopMedia(dialog);
            receiver.invokeMethodAsync("Close");
        });
        dialog.addEventListener("keydown", event => {
            if (event.target.closest("audio, video, .pdf-reader, input, select, textarea")) return;
            if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
                event.preventDefault();
                receiver.invokeMethodAsync("Navigate", event.key === "ArrowLeft" ? -1 : 1);
            }
        });
        let start = null;
        dialog.addEventListener("touchstart", event => {
            start = event.touches.length === 1 && event.target.matches(".gallery-photo") && !dialog.querySelector(".is-zoomed")
                ? { x: event.touches[0].clientX, y: event.touches[0].clientY } : null;
        }, { passive: true });
        dialog.addEventListener("touchend", event => {
            if (!start || event.changedTouches.length !== 1) return;
            const dx = event.changedTouches[0].clientX - start.x;
            const dy = event.changedTouches[0].clientY - start.y;
            start = null;
            if (Math.abs(dx) > 65 && Math.abs(dy) < 60) receiver.invokeMethodAsync("Navigate", dx > 0 ? -1 : 1);
        }, { passive: true });
    }
    if (!dialog.open) dialog.showModal();
}
