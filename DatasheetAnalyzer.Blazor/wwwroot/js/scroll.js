// Simple scroll helper used from Blazor via IJSRuntime.
window.scrollToElementById = function (id) {
    var el = document.getElementById(id);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
};
