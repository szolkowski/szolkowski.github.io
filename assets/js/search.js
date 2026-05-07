// Initialise Pagefind UI on every `.pagefind-search-mount` element. We
// render the same div in two places (top-of-content for mobile, sidebar
// for desktop) and CSS hides whichever is off-breakpoint. Mounting both
// keeps the UI working immediately if the user resizes across the
// breakpoint without reloading.
window.addEventListener('DOMContentLoaded', function () {
    if (typeof PagefindUI === 'undefined') return;

    var mounts = document.querySelectorAll('.pagefind-search-mount');
    if (!mounts.length) return;

    var instances = [];
    mounts.forEach(function (el) {
        var ui = new PagefindUI({
            element: el,
            showSubResults: true,
            showImages: false,
            excerptLength: 25,
            resetStyles: false,
        });
        instances.push({ el: el, ui: ui });
    });

    // Honour ?q=... so the WebSite.potentialAction.SearchAction urlTemplate
    // (declared in head-custom.html) can be followed end-to-end from a SERP
    // sitelink search box. Only trigger on the visible instance.
    var params = new URLSearchParams(window.location.search);
    var q = params.get('q');
    if (!q) return;
    instances.forEach(function (entry) {
        if (window.getComputedStyle(entry.el).display === 'none') return;
        if (typeof entry.ui.triggerSearch === 'function') {
            entry.ui.triggerSearch(q);
        }
    });
});
