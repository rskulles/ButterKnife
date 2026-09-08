// Collapse the phone sidebar menu if it is open (no-op on wide screens, where d-md-block keeps it visible).
export function closeSidebar() {
    const el = document.getElementById("sidebar-nav");
    if (el?.classList.contains("show") && window.bootstrap?.Collapse) {
        window.bootstrap.Collapse.getOrCreateInstance(el, { toggle: false }).hide();
    }
}
