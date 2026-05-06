// Consent banner controller. Reads localStorage; if the user hasn't decided
// yet, reveal the banner. Accept/Decline both store the decision and update
// gtag's Consent Mode v2 state — head-custom-google-analytics.html sets up
// the dataLayer + gtag() stub and the default-denied state.
(function () {
    var STORAGE_KEY = 'analytics_consent';

    function readStored() {
        try {
            return localStorage.getItem(STORAGE_KEY);
        } catch (e) {
            return null;
        }
    }

    function writeStored(value) {
        try {
            localStorage.setItem(STORAGE_KEY, value);
        } catch (e) { /* private browsing, full disk, etc. — ignore */ }
    }

    function updateGtag(value) {
        if (typeof window.gtag !== 'function') return;
        window.gtag('consent', 'update', {
            'ad_storage': value,
            'ad_user_data': value,
            'ad_personalization': value,
            'analytics_storage': value
        });
    }

    function init() {
        var stored = readStored();
        if (stored === 'granted' || stored === 'denied') return;

        var banner = document.getElementById('consent-banner');
        if (!banner) return;
        banner.hidden = false;

        function decide(value) {
            writeStored(value);
            updateGtag(value);
            banner.hidden = true;
        }

        var accept = document.getElementById('consent-accept');
        var decline = document.getElementById('consent-decline');
        if (accept) accept.addEventListener('click', function () { decide('granted'); });
        if (decline) decline.addEventListener('click', function () { decide('denied'); });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
