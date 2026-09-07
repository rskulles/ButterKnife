export function wireInput(textarea, component) {
    if (!textarea || textarea.dataset.wired === "1") {
        return;
    }
    textarea.dataset.wired = "1";
    textarea.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            if (!textarea.disabled) {
                component.invokeMethodAsync("SendFromKeyboardAsync");
            }
        }
    });
}

export function scrollToBottom(el) {
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
}

export function focus(el) {
    el?.focus();
}
