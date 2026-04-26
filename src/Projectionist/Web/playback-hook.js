/*
 * Projectionist client-side hook
 *
 * Jellyfin's web/desktop/TV clients hard-code intro fetching to Movie items only:
 *   if (item.Type === 'Movie') { fetchIntros(item.Id); ... }
 * Episodes never call /Items/{id}/Intros, so our IIntroProvider is never asked.
 *
 * This hook waits for the playbackManager module to be available, then wraps
 * its play() function. When the user is about to play an Episode, we fetch
 * intros for it, prepend them to the play options, and let playbackManager
 * handle the rest. Movies still work normally (Jellyfin already fetches
 * intros for them server-side).
 */
// Top-level log so we can see in the browser console whether this script even loaded.
try { console.log('[Projectionist] hook script loaded'); } catch (_) {}

// ============== Hide the internal Projectionist library from the home screen ==============
// We can't use BlockedMediaFolders (it would prevent streaming the prerolls) so
// we hide the library tile + section client-side via CSS + a DOM mutation observer.
(function () {
    'use strict';
    var STYLE_ID = 'pjt-hide-library-style';
    var LIB_NAME = 'Projectionist Prerolls';

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        // Cards in the Latest / My Media / Libraries grids carry the library
        // name in a `data-libraryid` attribute that's resolvable via the card
        // body. We can't easily map id->name in CSS, so we walk the DOM.
        // The CSS rule itself just hides anything we tag with our marker class.
        s.textContent = '.pjt-hidden-library{display:none!important;visibility:hidden!important;}';
        document.head.appendChild(s);
    }

    function hideLibraryTiles(root) {
        try {
            var nodes = (root || document).querySelectorAll(
                '.card a[href*="/web/#/list.html"], .card .cardText a, ' +
                '.card .cardText, .card .cardImageContainer'
            );
            // The reliable target: any card whose visible label text matches the
            // library name. Walk all cards on the page and tag matching ones.
            var cards = (root || document).querySelectorAll(
                '.card, .listItem, .navMenuOption, button.emby-button, a.emby-button'
            );
            cards.forEach(function (card) {
                if (card.classList.contains('pjt-hidden-library')) return;
                var text = (card.textContent || '').trim();
                // Exact-match by library name; broad-match by URL contains "ProjectionistPrerolls".
                if (text === LIB_NAME ||
                    text.indexOf(LIB_NAME) === 0 ||
                    (card.querySelector && card.querySelector('a[href*="ProjectionistPrerolls"]'))) {
                    card.classList.add('pjt-hidden-library');
                }
                // Also walk up to the parent .card if we matched a child element
                if (card.classList.contains('pjt-hidden-library')) {
                    var parentCard = card.closest && card.closest('.card');
                    if (parentCard && parentCard !== card) parentCard.classList.add('pjt-hidden-library');
                }
            });
        } catch (_) {}
    }

    function startHider() {
        ensureStyle();
        hideLibraryTiles(document);
        // Re-run when the DOM changes (Jellyfin's web client is heavily SPA-driven)
        try {
            var obs = new MutationObserver(function (mutations) {
                // Throttle: only run if at least one added node has descendants
                var any = mutations.some(function (m) { return m.addedNodes && m.addedNodes.length; });
                if (any) hideLibraryTiles(document);
            });
            obs.observe(document.body, { childList: true, subtree: true });
        } catch (_) {}
        // Periodic safety net for stubborn SPA renders
        setInterval(function () { hideLibraryTiles(document); }, 2500);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', startHider);
    } else {
        startHider();
    }
})();

// ============== Skip-preroll button overlay ==============
// Appears in the lower-right of the player while a preroll is active. Reads
// configuration from /Plugins/Projectionist/Config so the user's "min seconds
// before skip" setting is honoured.
(function () {
    'use strict';
    var BTN_ID = 'pjt-skip-btn';
    var STYLE_ID = 'pjt-skip-style';
    var minSkipSeconds = 0;
    var enabled = true;
    var prerollPaths = new Set();

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent =
            '#' + BTN_ID + '{position:fixed;right:32px;bottom:120px;z-index:9999;'
            + 'background:rgba(20,20,28,.85);color:#fff;border:1px solid rgba(229,9,20,.6);'
            + 'border-radius:6px;padding:10px 18px;font:600 13px/1 -apple-system,sans-serif;'
            + 'cursor:pointer;backdrop-filter:blur(8px);transition:opacity .2s,transform .2s;'
            + 'letter-spacing:.04em;text-transform:uppercase;}'
            + '#' + BTN_ID + ':hover{background:rgba(229,9,20,.9);}'
            + '#' + BTN_ID + '.pjt-hidden{opacity:0;pointer-events:none;transform:translateY(8px);}';
        document.head.appendChild(s);
    }

    function loadConfig() {
        try {
            var ac = (window.ApiClient || (window.connectionManager && connectionManager.currentApiClient && connectionManager.currentApiClient()));
            if (!ac) return;
            ac.fetch({ url: ac.getUrl('Plugins/Projectionist/Config'), type: 'GET', dataType: 'json' })
                .then(function (cfg) {
                    if (!cfg) return;
                    minSkipSeconds = cfg.SkippableAfterSeconds || cfg.skippableAfterSeconds || 0;
                    enabled = (cfg.EnableSkippablePrerolls !== undefined ? cfg.EnableSkippablePrerolls : cfg.enableSkippablePrerolls) !== false;
                })
                .catch(function () {});
        } catch (_) {}
    }
    loadConfig();

    function isPrerollItem(item) {
        if (!item || !item.Path) return false;
        // Heuristic: item belongs to "Projectionist Prerolls" library, OR its path
        // is in the set we recorded when prepending intros.
        if (prerollPaths.has(item.Path)) return true;
        // Fallback: check parent name
        if (item.SeriesName === 'Projectionist Prerolls') return true;
        return false;
    }

    window.__projectionistMarkPreroll = function (path) {
        if (path) prerollPaths.add(path);
    };

    function showButton() {
        ensureStyle();
        var btn = document.getElementById(BTN_ID);
        if (!btn) {
            btn = document.createElement('button');
            btn.id = BTN_ID;
            btn.textContent = 'Skip';
            btn.addEventListener('click', function () {
                try {
                    var pm = window.playbackManager;
                    if (pm && typeof pm.nextTrack === 'function') pm.nextTrack();
                    else if (pm && typeof pm.stop === 'function') pm.stop();
                } catch (_) {}
            });
            document.body.appendChild(btn);
        }
        btn.classList.remove('pjt-hidden');
    }
    function hideButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) btn.classList.add('pjt-hidden');
    }

    function attachSkipWatcher() {
        if (!window.Events || !window.playbackManager) {
            setTimeout(attachSkipWatcher, 400);
            return;
        }
        var revealTimer = null;
        Events.on(playbackManager, 'playbackstart', function (e, player) {
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
            try {
                var item = playbackManager.currentItem(player);
                if (!isPrerollItem(item) || !enabled) {
                    hideButton();
                    return;
                }
                if (minSkipSeconds > 0) {
                    revealTimer = setTimeout(showButton, minSkipSeconds * 1000);
                } else {
                    showButton();
                }
            } catch (_) { hideButton(); }
        });
        Events.on(playbackManager, 'playbackstop', function () {
            hideButton();
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
        });
    }
    attachSkipWatcher();
})();

(function () {
    'use strict';

    if (window.__projectionistInstalled) {
        try { console.log('[Projectionist] already installed; skipping duplicate init'); } catch (_) {}
        return;
    }
    window.__projectionistInstalled = true;

    var TAG = '[Projectionist]';

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function log() {
        try { console.log.apply(console, [TAG].concat(Array.prototype.slice.call(arguments))); } catch (_) {}
    }

    function waitForPlaybackManager(cb, attempts) {
        attempts = attempts || 0;
        // playbackManager is exposed differently in different client versions.
        var pm = window.playbackManager
            || (window.require && tryRequire('playbackManager'))
            || (window.RequireJS && tryRequire('playbackManager'));
        if (pm && typeof pm.play === 'function') {
            cb(pm);
            return;
        }
        if (attempts > 80) { // ~16s of polling
            log('gave up waiting for playbackManager');
            return;
        }
        setTimeout(function () { waitForPlaybackManager(cb, attempts + 1); }, 200);
    }

    function tryRequire(name) {
        try { return window.require(name); } catch (_) { return null; }
    }

    /**
     * Fetch the intros for the given itemId via /Items/{id}/Intros.
     * Returns a promise resolving to an array of BaseItemDto, or [] on any failure.
     */
    function fetchIntros(apiClient, itemId, userId) {
        try {
            // ApiClient.getIntros handles the right URL + auth.
            return apiClient.getIntros(itemId).then(function (res) {
                var items = (res && res.Items) || [];
                return items;
            }).catch(function () { return []; });
        } catch (e) {
            return Promise.resolve([]);
        }
    }

    function isEpisode(item) {
        return item && (item.Type === 'Episode' || item.MediaType === 'Episode');
    }

    function patch(playbackManager) {
        if (playbackManager.__projectionistPatched) return;
        playbackManager.__projectionistPatched = true;
        var origPlay = playbackManager.play.bind(playbackManager);

        playbackManager.play = function (options) {
            var apiClient = getApiClient();
            if (!apiClient || !options) return origPlay(options);

            // We only intervene when the FIRST item in the request is an Episode.
            // For movies, Jellyfin already fetches intros itself so we no-op.
            var firstItem = null;
            if (Array.isArray(options.items) && options.items.length) {
                firstItem = options.items[0];
            }
            // Some call sites pass `ids` instead of pre-resolved items.
            var firstId = null;
            if (Array.isArray(options.ids) && options.ids.length) {
                firstId = options.ids[0];
            }

            // If we don't yet know the type, fetch it.
            var itemPromise;
            if (firstItem) {
                itemPromise = Promise.resolve(firstItem);
            } else if (firstId) {
                itemPromise = apiClient.getItem(apiClient.getCurrentUserId(), firstId)
                    .catch(function () { return null; });
            } else {
                return origPlay(options);
            }

            return itemPromise.then(function (item) {
                if (!isEpisode(item)) return origPlay(options);

                return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                    if (!intros.length) {
                        log('no preroll for episode', item.Name || item.Id);
                        return origPlay(options);
                    }
                    log('queueing', intros.length, 'preroll(s) before episode', item.Name || item.Id);

                    // Mark each intro path so the skip-button hook recognises it.
                    try {
                        intros.forEach(function (i) {
                            if (i && i.Path && typeof window.__projectionistMarkPreroll === 'function') {
                                window.__projectionistMarkPreroll(i.Path);
                            }
                        });
                    } catch (_) {}

                    // Prepend intros to whichever input the caller used.
                    var newOptions = Object.assign({}, options);
                    if (Array.isArray(options.items)) {
                        newOptions.items = intros.concat(options.items);
                        delete newOptions.ids;
                    } else if (Array.isArray(options.ids)) {
                        var introIds = intros.map(function (i) { return i.Id; });
                        newOptions.ids = introIds.concat(options.ids);
                        delete newOptions.items;
                    } else {
                        newOptions.items = intros.concat([item]);
                        delete newOptions.ids;
                    }
                    // Preserve playback start position only on the FEATURE, not on intros.
                    // playbackManager honours startPositionTicks on the first item; we
                    // don't want our preroll to skip ahead, so wipe it before play.
                    if (typeof newOptions.startPositionTicks !== 'undefined') {
                        // Save and re-apply when feature actually starts. The simplest
                        // robust approach: drop it for now — the user's resume position
                        // will still trigger via Jellyfin's normal resume on the feature.
                        delete newOptions.startPositionTicks;
                    }
                    return origPlay(newOptions);
                });
            });
        };

        log('episode preroll hook installed');
    }

    waitForPlaybackManager(patch);
})();
