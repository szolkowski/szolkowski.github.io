// Initialise Pagefind UI in the header search slot. The bundle is loaded with
// `defer` so PagefindUI is on window by the time DOMContentLoaded fires.
window.addEventListener('DOMContentLoaded', function () {
    if (typeof PagefindUI === 'undefined') return;
    if (!document.getElementById('search')) return;
    var ui = new PagefindUI({
        element: '#search',
        showSubResults: true,
        showImages: false,
        excerptLength: 25,
        resetStyles: false,
    });

    // Honour ?q=... so the WebSite.potentialAction.SearchAction urlTemplate
    // (declared in head-custom.html) can be followed end-to-end from a SERP
    // sitelink search box: /?q=foo → input prefilled, results dropdown open.
    var params = new URLSearchParams(window.location.search);
    var q = params.get('q');
    if (q && typeof ui.triggerSearch === 'function') {
        ui.triggerSearch(q);
    }
});
