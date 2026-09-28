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
    var prerollIds = new Set();
    var prerollNames = new Set();
    var currentPrerollFileName = null;
    // Window-visible debug log so the Playwright/inspector can read state
    // without relying on console capture. Each entry: {t: ts, event: str, data: {}}.
    window.__pjtDebug = window.__pjtDebug || { entries: [], state: {} };
    function dlog(event, data) {
        try {
            window.__pjtDebug.entries.push({ t: Date.now(), event: event, data: data });
            if (window.__pjtDebug.entries.length > 200) window.__pjtDebug.entries.shift();
        } catch (_) {}
    }
    window.__pjtDebug.state.skipIIFE = {
        prerollIds: prerollIds, prerollPaths: prerollPaths, prerollNames: prerollNames,
    };
    window.__projectionistSettings = window.__projectionistSettings || { featurePreloadEnabled: false, featurePreloadMode: 0 };

    function tryRequire(name) {
        try {
            if (window.require) return window.require(name);
            if (window.RequireJS) return window.RequireJS(name);
        } catch (_) {}
        return null;
    }

    function getPlaybackManager() {
        return window.playbackManager || tryRequire('playbackManager');
    }

    function getEvents() {
        return window.Events || tryRequire('events') || tryRequire('Events');
    }

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent =
            '#' + BTN_ID + '{position:fixed;right:32px;bottom:120px;z-index:2147483000;'
            + 'background:rgba(20,20,28,.85);color:#fff;border:1px solid rgba(229,9,20,.6);'
            + 'border-radius:6px;padding:10px 18px;font:600 13px/1 -apple-system,sans-serif;'
            + 'cursor:pointer;backdrop-filter:blur(8px);transition:opacity .2s,transform .2s;'
            + 'letter-spacing:.04em;text-transform:uppercase;}'
            + '#' + BTN_ID + ':hover{background:rgba(229,9,20,.9);}'
            + '#' + BTN_ID + '.pjt-hidden{opacity:0;pointer-events:none;transform:translateY(8px);}';
        document.head.appendChild(s);
    }

    function loadConfig(attempts) {
        attempts = attempts || 0;
        try {
            var ac = (window.ApiClient || (window.connectionManager && connectionManager.currentApiClient && connectionManager.currentApiClient()));
            if (!ac || !hasAccessToken(ac)) {
                // Wait for ApiClient AND a signed-in user. A fresh load lands
                // on the login page first, where this request can only 401,
                // and the settings were then never fetched for the session.
                // The check is local (no request), so polling is cheap.
                setTimeout(function () { loadConfig(attempts); }, 1000);
                return;
            }
            ac.fetch({ url: ac.getUrl('Plugins/Projectionist/HookSettings'), type: 'GET', dataType: 'json' })
                .then(function (cfg) {
                    if (!cfg) return;
                    minSkipSeconds = cfg.SkippableAfterSeconds || cfg.skippableAfterSeconds || 0;
                    enabled = (cfg.EnableSkippablePrerolls !== undefined ? cfg.EnableSkippablePrerolls : cfg.enableSkippablePrerolls) !== false;
                    var preloadMode = parsePreloadMode(
                        cfg.FeaturePreloadMode !== undefined ? cfg.FeaturePreloadMode : cfg.featurePreloadMode,
                        (cfg.EnableFeaturePreload !== undefined ? cfg.EnableFeaturePreload : cfg.enableFeaturePreload) === true ? 1 : 0);
                    window.__projectionistSettings.featurePreloadMode = preloadMode;
                    window.__projectionistSettings.featurePreloadEnabled = preloadMode !== 0;
                    dlog('config:loaded', { minSkipSeconds: minSkipSeconds, enabled: enabled, preloadMode: preloadMode });
                })
                .catch(function (err) {
                    dlog('config:fetch-failed', { msg: String(err) });
                    if (attempts < 5) setTimeout(function () { loadConfig(attempts + 1); }, 3000);
                });
        } catch (e) { dlog('config:throw', { msg: String(e) }); }
    }

    function hasAccessToken(ac) {
        try {
            var t = typeof ac.accessToken === 'function' ? ac.accessToken() : ac.accessToken;
            return !!t;
        } catch (_) { return false; }
    }
    loadConfig();

    function parsePreloadMode(value, fallback) {
        if (value === null || value === undefined) return fallback || 0;
        if (typeof value === 'number') return value;
        var s = String(value).toLowerCase();
        if (/^\d+$/.test(s)) return parseInt(s, 10);
        if (s === 'hot') return 2;
        if (s === 'warm') return 1;
        return 0;
    }

    function isPrerollItem(item) {
        if (!item) {
            try { console.log('[Projectionist] isPrerollItem: NULL item'); } catch (_) {}
            return false;
        }
        var byId = !!(item.Id && prerollIds.has(item.Id));
        var byPath = !!prerollPaths.has(item.Path);
        var byName = !!(item.Name && prerollNames.has(item.Name));
        var byParent = item.SeriesName === 'Projectionist Prerolls' ||
            item.ParentName === 'Projectionist Prerolls' ||
            item.CollectionName === 'Projectionist Prerolls';
        var verdict = byId || byPath || byName || byParent;
        dlog('isPrerollItem', {
            verdict: verdict,
            id: item.Id, name: item.Name, path: item.Path,
            seriesName: item.SeriesName, parentName: item.ParentName, collectionName: item.CollectionName,
            type: item.Type, mediaType: item.MediaType,
            matched: { byId: byId, byPath: byPath, byName: byName, byParent: byParent },
            sets: { ids: prerollIds.size, paths: prerollPaths.size, names: prerollNames.size },
        });
        return verdict;
    }

    window.__projectionistMarkPreroll = function (path, id, name) {
        if (path) prerollPaths.add(path);
        if (id) prerollIds.add(id);
        if (name) prerollNames.add(name);
        dlog('mark-preroll', { path: path, id: id, name: name, ids: prerollIds.size, paths: prerollPaths.size, names: prerollNames.size });
    };

    function getApiClientLocal() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function getOverlayHost() {
        // Pick the most-foreground host: fullscreen first, then Jellyfin's
        // own player container variants (different versions name it
        // differently), then the topmost video's ancestor stacking context,
        // finally document.body.
        var fs = document.fullscreenElement || document.webkitFullscreenElement;
        if (fs) return fs;
        var candidates = [
            '.videoPlayerContainer',
            '.htmlVideoPlayerContainer',
            '.videoOsdBottom',
            '.videoPlayer',
            '#videoOsdPage',
            '.osdPage',
            '.dialogContainer',
            '.skinBody',
        ];
        for (var i = 0; i < candidates.length; i++) {
            var el = document.querySelector(candidates[i]);
            if (el) return el;
        }
        // Last-resort: parent of the current <video> tag.
        var video = document.querySelector('video');
        if (video) {
            var p = video.parentElement;
            // walk up a few levels so the button sits beside the OSD, not the bare <video>
            for (var k = 0; k < 3 && p && p.parentElement; k++) p = p.parentElement;
            if (p) return p;
        }
        return document.body;
    }

    function showButton() {
        ensureStyle();
        var host = getOverlayHost();
        var btn = document.getElementById(BTN_ID);
        var hostInfo = host.tagName + (host.className ? '.' + host.className : '');
        dlog('show-button', { host: hostInfo, btnAlreadyExists: !!btn });
        if (!btn) {
            btn = document.createElement('button');
            btn.id = BTN_ID;
            btn.textContent = 'Skip';
            btn.addEventListener('click', function () {
                try {
                    // Skip-rate reporting: capture currentTime and POST before skipping.
                    try {
                        var video = document.querySelector('video');
                        var seconds = video ? Math.round(video.currentTime * 10) / 10 : 0;
                        var ac = getApiClientLocal();
                        if (ac && currentPrerollFileName) {
                            // Jellyfin 12 rejects X-Emby-Token by default;
                            // only the Authorization header is accepted there.
                            fetch(ac.getUrl('Plugins/Projectionist/SkipReport'), {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-Emby-Token': ac.accessToken(),
                                    'Authorization': 'MediaBrowser Token="' + ac.accessToken() + '"',
                                },
                                body: JSON.stringify({ fileName: currentPrerollFileName, secondsBeforeSkip: seconds }),
                            }).catch(function () {});
                        }
                    } catch (_) {}
                    var pm = getPlaybackManager();
                    if (pm && typeof pm.nextTrack === 'function') pm.nextTrack();
                    else if (pm && typeof pm.stop === 'function') pm.stop();
                } catch (_) {}
            });
        }
        if (btn.parentNode !== host) host.appendChild(btn);
        btn.classList.remove('pjt-hidden');
    }
    function hideButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) btn.classList.add('pjt-hidden');
    }

    var _attachSkipAttempts = 0;
    function attachSkipWatcher() {
        var events = getEvents();
        var pm = getPlaybackManager();
        if (!events || !pm) {
            _attachSkipAttempts++;
            // Modern Jellyfin Web has removed window.playbackManager + window.require,
            // so this watcher can never bind. Give up after ~30 seconds of trying;
            // the video-element-based v2 watcher at the end of the file does the job.
            if (_attachSkipAttempts < 75) setTimeout(attachSkipWatcher, 400);
            else dlog('skip-watcher:giving-up', { attempts: _attachSkipAttempts });
            return;
        }
        dlog('attach-skip-watcher', { enabled: enabled, minSkipSeconds: minSkipSeconds });
        // Install a one-time fullscreenchange hook so the skip button gets
        // reparented into document.fullscreenElement whenever the player
        // toggles fullscreen. Without this, a body-parented button is not
        // painted while the video element is fullscreen.
        if (!window.__pjtFsHooked) {
            window.__pjtFsHooked = true;
            var reparent = function () {
                var btn = document.getElementById(BTN_ID);
                if (!btn) return;
                var host = getOverlayHost();
                if (btn.parentNode !== host) host.appendChild(btn);
            };
            document.addEventListener('fullscreenchange', reparent);
            document.addEventListener('webkitfullscreenchange', reparent);
        }
        var revealTimer = null;
        events.on(pm, 'playbackstart', function (e, player) {
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
            try {
                var item = pm.currentItem(player);
                dlog('playbackstart', {
                    hasItem: !!item, id: item && item.Id, name: item && item.Name,
                    type: item && item.Type, path: item && item.Path,
                    enabled: enabled, hostNow: (getOverlayHost() && (getOverlayHost().tagName + '.' + (getOverlayHost().className || ''))),
                });
                if (!isPrerollItem(item) || !enabled) {
                    currentPrerollFileName = null;
                    hideButton();
                    return;
                }
                // Track filename for skip-rate reporting.
                try {
                    if (item) {
                        if (item.Path) {
                            var p = String(item.Path);
                            var slash = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
                            currentPrerollFileName = slash >= 0 ? p.substring(slash + 1) : p;
                        } else if (item.Name) {
                            currentPrerollFileName = item.Name;
                        }
                    }
                } catch (_) {}
                if (minSkipSeconds > 0) {
                    revealTimer = setTimeout(showButton, minSkipSeconds * 1000);
                } else {
                    showButton();
                }
            } catch (_) { hideButton(); }
        });
        events.on(pm, 'playbackstop', function () {
            hideButton();
            currentPrerollFileName = null;
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
        });
        // itemchange: when playback advances to the next item, clear the
        // tracked filename — it will be re-set on the next playbackstart if
        // the new item is also a preroll.
        try {
            events.on(pm, 'itemchange', function () { currentPrerollFileName = null; });
        } catch (_) {}
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

    // playbackManager is NOT on `window` in modern Jellyfin Web (10.9+);
    // it's a named ES-module export. We capture it via two parallel paths,
    // whichever wins first:
    //
    //   1) Webpack chunk dig — push a synthetic chunk to harvest
    //      `__webpack_require__`, then walk `req.c` (module cache) for any
    //      exports object that duck-types as playbackManager.
    //
    //   2) `window.Events.on` monkey-patch — Jellyfin Web exposes
    //      `window.Events` (the pub/sub singleton). Every page that displays
    //      playback state (now-playing bar, OSD, item details) subscribes
    //      `Events.on(playbackManager, 'playbackstop', ...)` etc; the first
    //      such call hands us the playbackManager instance for free.
    //
    // We also fall back to the legacy `window.playbackManager` global if
    // some other plugin happens to expose it.
    function pbmLooksRight(obj) {
        return obj && typeof obj === 'object'
            && typeof obj.play === 'function'
            && typeof obj.canPlay === 'function'
            && typeof obj.getCurrentPlayer === 'function';
    }

    function tryWebpackDig() {
        try {
            var chunkGlobals = [];
            if (Array.isArray(window.webpackChunk)) chunkGlobals.push(window.webpackChunk);
            var names = Object.getOwnPropertyNames(window);
            for (var ni = 0; ni < names.length; ni++) {
                if (!/^webpackChunk/i.test(names[ni])) continue;
                var v = window[names[ni]];
                if (Array.isArray(v) && chunkGlobals.indexOf(v) === -1) chunkGlobals.push(v);
            }
            for (var ci = 0; ci < chunkGlobals.length; ci++) {
                var chunkGlobal = chunkGlobals[ci];
                var req = null;
                var chunkId = 'pjt-' + Date.now() + '-' + Math.random().toString(36).slice(2);
                try { chunkGlobal.push([[chunkId], {}, function (r) { req = r; }]); } catch (_) { continue; }
                if (typeof req !== 'function' || !req.c) continue;
                for (var modId in req.c) {
                    var mod = req.c[modId];
                    var exp = mod && mod.exports;
                    if (!exp || typeof exp !== 'object') continue;
                    // Common minified-export shapes: exp.f, exp.default,
                    // exp.playbackManager, or the exports object itself.
                    var candidates = [exp.f, exp.default, exp.playbackManager, exp];
                    for (var ki = 0; ki < candidates.length; ki++) {
                        if (pbmLooksRight(candidates[ki])) return candidates[ki];
                    }
                }
            }
        } catch (_) { }
        return null;
    }

    function tryLegacyGlobal() {
        return pbmLooksRight(window.playbackManager) ? window.playbackManager : null;
    }

    var __pjtEventsHookInstalled = false;
    function installEventsHook(onCapture) {
        if (__pjtEventsHookInstalled) return true;
        if (!window.Events || typeof window.Events.on !== 'function') return false;
        var origOn = window.Events.on;
        var origTrigger = window.Events.trigger;
        window.Events.on = function (obj, type, fn) {
            try { if (pbmLooksRight(obj)) onCapture(obj); } catch (_) { }
            return origOn.apply(this, arguments);
        };
        if (typeof origTrigger === 'function') {
            window.Events.trigger = function (obj, type, args) {
                try { if (pbmLooksRight(obj)) onCapture(obj); } catch (_) { }
                return origTrigger.apply(this, arguments);
            };
        }
        __pjtEventsHookInstalled = true;
        return true;
    }

    function waitForPlaybackManager(cb) {
        var captured = false;
        function onCapture(pbm) {
            if (captured) return;
            captured = true;
            try { cb(pbm); } catch (e) { log('patch threw', e); }
        }
        // Eager: try right now.
        var pbm = tryWebpackDig() || tryLegacyGlobal();
        if (pbm) { onCapture(pbm); return; }
        installEventsHook(onCapture);
        // Polling backup — modules + Events may not be ready immediately.
        var attempts = 0;
        var iv = setInterval(function () {
            attempts++;
            if (captured || attempts > 120) { // 120 * 250ms = 30s
                clearInterval(iv);
                if (!captured) log('gave up waiting for playbackManager (modern web)');
                return;
            }
            if (!__pjtEventsHookInstalled) installEventsHook(onCapture);
            var p = tryWebpackDig() || tryLegacyGlobal();
            if (p) { clearInterval(iv); onCapture(p); }
        }, 250);
    }

    function tryRequire(name) {
        try { return window.require(name); } catch (_) { return null; }
    }

    /**
     * Fetch the intros for the given itemId via /Items/{id}/Intros.
     * Returns a promise resolving to an array of BaseItemDto, or [] on any failure.
     */
    // Short-lived intros cache keyed by itemId. Lets us pre-warm the
    // /Intros response during the back half of the current episode so the
    // auto-next-episode transition fires play() with everything in hand —
    // no network round-trip standing between "episode ends" and
    // "next-episode play() committed" — which is what was letting
    // jellyfin-web slip in a navigate-to-home between the two.
    var __pjtIntrosCache = {};
    var INTROS_CACHE_TTL_MS = 60 * 1000;

    function fetchIntros(apiClient, itemId, userId) {
        if (!itemId) return Promise.resolve([]);
        var cached = __pjtIntrosCache[itemId];
        if (cached && (Date.now() - cached.t) < INTROS_CACHE_TTL_MS) {
            return Promise.resolve(cached.items);
        }
        try {
            return apiClient.getIntros(itemId).then(function (res) {
                var items = (res && res.Items) || [];
                __pjtIntrosCache[itemId] = { items: items, t: Date.now() };
                return items;
            }).catch(function () { return []; });
        } catch (e) {
            return Promise.resolve([]);
        }
    }

    function isEpisode(item) {
        return item && (item.Type === 'Episode' || item.MediaType === 'Episode');
    }

    function getFirstPlayItem(options) {
        if (!options) return null;
        if (Array.isArray(options.items) && options.items.length) {
            // Respect startIndex — when a series view plays from an
            // episode list, items[] holds the whole season and startIndex
            // points to the chosen episode. Using items[0] gives the
            // wrong item (always the season opener).
            var idx = (typeof options.startIndex === 'number' && options.startIndex >= 0)
                ? options.startIndex : 0;
            return options.items[idx] || options.items[0];
        }
        if (options.item) return options.item;
        if (options.Item) return options.Item;
        if (options.currentItem) return options.currentItem;
        return null;
    }

    function getFirstPlayId(options) {
        if (!options) return null;
        var idx = (typeof options.startIndex === 'number' && options.startIndex >= 0)
            ? options.startIndex : 0;
        if (Array.isArray(options.ids) && options.ids.length) return options.ids[idx] || options.ids[0];
        if (Array.isArray(options.itemIds) && options.itemIds.length) return options.itemIds[idx] || options.itemIds[0];
        if (Array.isArray(options.ItemIds) && options.ItemIds.length) return options.ItemIds[idx] || options.ItemIds[0];
        return options.id || options.Id || options.itemId || options.ItemId || null;
    }

    function isVideoFeature(item) {
        return item && (item.MediaType === 'Video' ||
            item.Type === 'Movie' ||
            item.Type === 'Episode' ||
            item.Type === 'MusicVideo');
    }

    function markIntrosForSkip(intros) {
        try {
            intros.forEach(function (i) {
                if (i && typeof window.__projectionistMarkPreroll === 'function') {
                    window.__projectionistMarkPreroll(i.Path, i.Id, i.Name);
                }
            });
        } catch (_) {}
    }

    function getAccessToken(apiClient) {
        try {
            if (apiClient && typeof apiClient.accessToken === 'function') return apiClient.accessToken();
        } catch (_) {}
        try {
            if (apiClient && apiClient.accessToken) return apiClient.accessToken;
        } catch (_) {}
        try {
            if (apiClient && apiClient._serverInfo && apiClient._serverInfo.AccessToken) return apiClient._serverInfo.AccessToken;
        } catch (_) {}
        try {
            if (apiClient && typeof apiClient.serverInfo === 'function') {
                var info = apiClient.serverInfo();
                if (info && info.AccessToken) return info.AccessToken;
            }
        } catch (_) {}
        return null;
    }

    function parsePreloadMode(value, fallback) {
        if (value === null || value === undefined) return fallback || 0;
        if (typeof value === 'number') return value;
        var s = String(value).toLowerCase();
        if (/^\d+$/.test(s)) return parseInt(s, 10);
        if (s === 'hot') return 2;
        if (s === 'warm') return 1;
        return 0;
    }

    function getFeaturePreloadMode() {
        var settings = window.__projectionistSettings || {};
        var mode = parsePreloadMode(settings.featurePreloadMode, settings.featurePreloadEnabled === true ? 1 : 0);
        return mode === 2 ? 2 : mode === 1 ? 1 : 0;
    }

    function buildHeaders(token, json) {
        var headers = {};
        if (json) headers['Content-Type'] = 'application/json';
        if (token) {
            headers['X-Emby-Token'] = token;
            headers['X-MediaBrowser-Token'] = token;
            headers.Authorization = 'MediaBrowser Token="' + token + '"';
        }
        return headers;
    }

    function normalizeStreamUrl(apiClient, url) {
        if (!url) return null;
        if (/^https?:\/\//i.test(url)) return url;
        var clean = url.charAt(0) === '/' ? url.substring(1) : url;
        try { return apiClient.getUrl(clean); } catch (_) { return url; }
    }

    function pickHotStreamUrl(apiClient, playbackInfo) {
        var sources = (playbackInfo && (playbackInfo.MediaSources || playbackInfo.mediaSources)) || [];
        for (var i = 0; i < sources.length; i++) {
            var source = sources[i] || {};
            var url = source.TranscodingUrl || source.transcodingUrl || source.DirectStreamUrl || source.directStreamUrl;
            if (url) return normalizeStreamUrl(apiClient, url);
        }
        return null;
    }

    function firstPlaylistUri(text) {
        var lines = String(text || '').split(/\r?\n/);
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (line && line.charAt(0) !== '#') return line;
        }
        return null;
    }

    function hotFetchUrl(url, token, itemName, depth) {
        depth = depth || 0;
        var headers = buildHeaders(token, false);
        if (!/\.m3u8(\?|$)/i.test(url)) {
            headers.Range = 'bytes=0-524287';
        }

        return fetch(url, {
            method: 'GET',
            headers: headers,
            credentials: 'same-origin',
            cache: 'no-store'
        }).then(function (res) {
            if (!res || (!res.ok && res.status !== 206)) {
                log('hot playback failed for', itemName, 'status', res && res.status);
                return;
            }

            var contentType = (res.headers && res.headers.get && res.headers.get('content-type')) || '';
            var isPlaylist = /\.m3u8(\?|$)/i.test(url) || /mpegurl|vnd\.apple\.mpegurl/i.test(contentType);
            if (isPlaylist && depth < 2) {
                return res.text().then(function (text) {
                    var next = firstPlaylistUri(text);
                    if (!next) {
                        log('hot playback opened playlist for', itemName);
                        return;
                    }
                    return hotFetchUrl(new URL(next, url).toString(), token, itemName, depth + 1);
                });
            }

            log('hot playback opened stream for', itemName);
            if (res.body && typeof res.body.getReader === 'function') {
                var reader = res.body.getReader();
                return reader.read().then(function () {
                    try { reader.cancel(); } catch (_) {}
                });
            }
            return res.arrayBuffer().catch(function () {});
        }).catch(function () {
            log('hot playback request failed for', itemName);
        });
    }

    function preloadFeature(apiClient, item, options) {
        var mode = getFeaturePreloadMode();
        if (mode === 0 || !apiClient || !item || !item.Id) return;

        window.__projectionistWarmedFeatures = window.__projectionistWarmedFeatures || {};
        var startTicks = (options && options.startPositionTicks) || 0;
        var warmKey = mode + ':' + item.Id + ':' + startTicks;
        if (window.__projectionistWarmedFeatures[warmKey]) return;
        window.__projectionistWarmedFeatures[warmKey] = true;

        (function () {
            try {
                var userId = typeof apiClient.getCurrentUserId === 'function' ? apiClient.getCurrentUserId() : null;
                var token = getAccessToken(apiClient);
                var headers = buildHeaders(token, true);
                var itemName = item.Name || item.Id;
                if (!token) log((mode === 2 ? 'hot' : 'warm') + ' playback has no access token for', itemName);
                var body = {
                    UserId: userId,
                    StartTimeTicks: startTicks,
                    IsPlayback: mode === 2,
                    AutoOpenLiveStream: mode === 2,
                    EnableDirectPlay: true,
                    EnableDirectStream: true,
                    EnableTranscoding: true
                };

                log((mode === 2 ? 'hot opening' : 'warming') + ' playback info for', itemName);
                fetch(apiClient.getUrl('Items/' + encodeURIComponent(item.Id) + '/PlaybackInfo'), {
                    method: 'POST',
                    headers: headers,
                    credentials: 'same-origin',
                    body: JSON.stringify(body)
                }).then(function (res) {
                    if (res && !res.ok) {
                        log((mode === 2 ? 'hot' : 'warm') + ' playback failed for', itemName, 'status', res.status);
                        throw new Error('preload playback info failed');
                    }
                    return res.json().catch(function () { return null; });
                }).then(function (info) {
                    if (mode !== 2) {
                        log('warmed playback info for', itemName);
                        return;
                    }
                    var streamUrl = pickHotStreamUrl(apiClient, info);
                    if (!streamUrl) {
                        log('hot playback found no early stream url for', itemName);
                        return;
                    }
                    return hotFetchUrl(streamUrl, token, itemName, 0);
                }).catch(function () {});
            } catch (_) {}
        })();
    }

    function patch(playbackManager) {
        if (playbackManager.__projectionistPatched) return;
        playbackManager.__projectionistPatched = true;
        var origPlay = playbackManager.play.bind(playbackManager);

        playbackManager.play = function (options) {
            var apiClient = getApiClient();

            // Mark that we're mid-play so the auto-next handler skips. We
            // clear this when the inner origPlay() returns (success or
            // failure). 5-second safety-net timer in case the promise
            // never resolves.
            window.__projectionistPlayInFlight = true;
            var clearInFlight = (function () {
                var cleared = false;
                return function () {
                    if (cleared) return;
                    cleared = true;
                    window.__projectionistPlayInFlight = false;
                };
            })();
            setTimeout(clearInFlight, 5000);
            // Helper: invoke origPlay and clear the flag once it settles.
            function origPlayAndSettle(opts) {
                try {
                    var p = origPlay(opts);
                    if (p && typeof p.then === 'function') {
                        return p.then(function (v) { clearInFlight(); return v; },
                                      function (e) { clearInFlight(); throw e; });
                    }
                    clearInFlight();
                    return p;
                } catch (err) {
                    clearInFlight();
                    throw err;
                }
            }

            if (!apiClient || !options) return origPlayAndSettle(options);

            // Episodes need us to prepend intros; movies fetch intros natively,
            // but we still prefetch their intro list so the skip button can
            // recognize movie prerolls.
            var firstItem = getFirstPlayItem(options);
            var firstId = getFirstPlayId(options);
            if (typeof firstItem === 'string') {
                firstId = firstId || firstItem;
                firstItem = null;
            }

            // If we don't yet know the type, fetch it.
            var itemPromise;
            if (firstItem) {
                itemPromise = Promise.resolve(firstItem);
            } else if (firstId) {
                itemPromise = apiClient.getItem(apiClient.getCurrentUserId(), firstId)
                    .catch(function () { return null; });
            } else {
                return origPlayAndSettle(options);
            }

            return itemPromise.then(function (item) {
                // Cache the upcoming feature title so the countdown overlay
                // can display it. Set for both Movie and Episode branches.
                try {
                    if (item) {
                        window.__projectionistUpcomingFeatureTitle =
                            item.Name || item.SeriesName || '';
                    }
                } catch (_) {}
                if (!isEpisode(item)) {
                    if (!isVideoFeature(item)) return origPlayAndSettle(options);
                    return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                        markIntrosForSkip(intros);
                        preloadFeature(apiClient, item, options);
                        return origPlayAndSettle(options);
                    });
                }

                return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                    if (!intros.length) {
                        log('no preroll for episode', item.Name || item.Id);
                        return origPlayAndSettle(options);
                    }
                    log('queueing', intros.length, 'preroll(s) before episode', item.Name || item.Id);
                    preloadFeature(apiClient, item, options);
                    markIntrosForSkip(intros);

                    // Prepend intros to whichever input the caller used.
                    // Honor startIndex: keep target item + everything after,
                    // drop items before it (the user is starting at the
                    // selected episode, not the season opener), prepend the
                    // intros, and reset startIndex to 0 so playback begins
                    // with the first intro.
                    var sIdx = (typeof options.startIndex === 'number' && options.startIndex >= 0)
                        ? options.startIndex : 0;
                    var newOptions = Object.assign({}, options);
                    newOptions.startIndex = 0;
                    // Drop options that were derived for the original target
                    // item and would contaminate intro playback (mediaSource
                    // id, audio/subtitle track indices, etc.).
                    delete newOptions.mediaSourceId;
                    delete newOptions.audioStreamIndex;
                    delete newOptions.subtitleStreamIndex;
                    // KEEP the full queue (don't trim later episodes). Per
                    // Playwright diagnostics, trimming left an empty queue
                    // after the target episode, which triggered jellyfin-
                    // web's "no more items → navigate to home" flow — that
                    // was the home-screen flash. With the full queue
                    // preserved, internal nextTrack() advances cleanly to
                    // the next episode. We then inject intros for upcoming
                    // queue items via queueNext during timeupdate (see
                    // maybePrefetchNextUp) so each episode still gets its
                    // own preroll.
                    if (Array.isArray(options.items)) {
                        var introIdsFromItems = intros.map(function (i) { return i.Id; });
                        var keepIdsFromItems = options.items.slice(sIdx).map(function (it) { return it.Id; });
                        if (!keepIdsFromItems.length || !keepIdsFromItems[0]) return origPlayAndSettle(options);
                        newOptions.ids = introIdsFromItems.concat(keepIdsFromItems);
                        newOptions.serverId = (options.items[sIdx] && options.items[sIdx].ServerId)
                            || newOptions.serverId
                            || apiClient.serverId();
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                        delete newOptions.itemIds;
                        delete newOptions.ItemIds;
                    } else if (Array.isArray(options.ids)) {
                        var introIds = intros.map(function (i) { return i.Id; });
                        var keepIds = options.ids.slice(sIdx);
                        newOptions.ids = introIds.concat(keepIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.itemIds)) {
                        var introItemIds = intros.map(function (i) { return i.Id; });
                        var keepItemIds = options.itemIds.slice(sIdx);
                        newOptions.itemIds = introItemIds.concat(keepItemIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.ItemIds)) {
                        var introUpperItemIds = intros.map(function (i) { return i.Id; });
                        var keepUpperItemIds = options.ItemIds.slice(sIdx);
                        newOptions.ItemIds = introUpperItemIds.concat(keepUpperItemIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else {
                        newOptions.items = intros.concat([item]);
                        delete newOptions.ids;
                        delete newOptions.itemIds;
                        delete newOptions.ItemIds;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                        delete newOptions.id;
                        delete newOptions.Id;
                        delete newOptions.itemId;
                        delete newOptions.ItemId;
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
                    return origPlayAndSettle(newOptions);
                });
            }).catch(function (err) {
                // Make sure we never leave the in-flight flag stuck on a
                // promise rejection in the upstream chain.
                clearInFlight();
                throw err;
            });
        };

        log('episode preroll hook installed');

        // ---- Auto-next-episode bridge ----
        // The queue-trim in our play() wrapper means jellyfin-web's
        // built-in queue auto-advance has nothing left to play after the
        // current episode ends. To restore the "next episode auto-plays"
        // behaviour, we listen for the playbackstop event: when an
        // Episode ends naturally and the user has EnableNextEpisodeAutoPlay
        // turned on, we fetch the next-up episode and call play() for it
        // ourselves — which goes back through our wrapper and prepends a
        // fresh preroll. Each episode gets its own preroll, indefinitely.
        if (!playbackManager.__projectionistAutoNextHooked) {
            playbackManager.__projectionistAutoNextHooked = true;
            var Events = window.Events;
            if (Events && typeof Events.on === 'function') {
                Events.on(playbackManager, 'playbackstop', function (e, info) {
                    try { handleAutoNextOnStop(playbackManager, info); }
                    catch (err) { console.warn(TAG, 'auto-next handler error', err); }
                });
                // Poll every 4 seconds during playback. jellyfin-web emits
                // 'timeupdate' on the PLAYER, not on playbackManager — and
                // the active player changes between episodes — so a
                // setInterval is the simplest reliable pulse for the
                // near-end queueNext injection. Cheap: each tick just
                // reads playbackManager.currentItem() / getPlayerState().
                var prefetchedForItem = null;
                setInterval(function () {
                    try {
                        var curPlayer = null;
                        try { curPlayer = playbackManager.getCurrentPlayer && playbackManager.getCurrentPlayer(); } catch (_) {}
                        if (!curPlayer) return; // nothing playing
                        var state = null;
                        try { state = playbackManager.getPlayerState && playbackManager.getPlayerState(curPlayer); } catch (_) {}
                        if (!state) return;
                        maybePrefetchNextUp(playbackManager, { state: state }, function (forId) {
                            prefetchedForItem = forId;
                        }, function () { return prefetchedForItem; });
                    } catch (err) { /* swallow */ }
                }, 4000);
                log('auto-next-episode bridge installed (polling)');
            }
        }
    }

    function maybePrefetchNextUp(playbackManager, info, mark, getMarked) {
        if (!info) return;
        var state = info.state || info; // jellyfin sometimes hands state directly
        var item = state.NowPlayingItem
            || (state.PlayState && state.PlayState.NowPlayingItem);
        if (!item) {
            try { item = playbackManager.currentItem && playbackManager.currentItem(); } catch (_) {}
        }
        if (!item || item.Type !== 'Episode' || !item.SeriesId) return;
        var positionTicks = (state.PlayState && state.PlayState.PositionTicks) || 0;
        var runTimeTicks = item.RunTimeTicks || 0;
        if (runTimeTicks <= 0 || positionTicks <= 0) return;
        // Trigger pre-fetch in last 30 seconds OR last 10% — whichever fires
        // first — but not before 30 seconds in (to avoid cheap-shot fetches
        // on quick browse-and-stop).
        var trigger = Math.max(runTimeTicks - 30 * 10000000, runTimeTicks * 0.90);
        if (positionTicks < trigger) return;
        if (getMarked() === item.Id) return; // already pre-fetched for this item
        mark(item.Id);

        var apiClient = getApiClient();
        if (!apiClient) return;
        // Find episodes AFTER the current one in the series. We always
        // ask for at least 3 (current + next + the one after) because
        // jellyfin-web's translateItemsForPlayback EXPANDS a single
        // queueNext({items: [Episode]}) call into the FULL series queue
        // starting from Pilot — which then gets inserted after the
        // current ep, making jellyfin advance to Pilot when current
        // ends. Including 2+ items (or starting with a non-Episode item)
        // bypasses that expansion code path.
        Promise.all([
            apiClient.getCurrentUser().catch(function () { return null; }),
            apiClient.getEpisodes(item.SeriesId, {
                UserId: apiClient.getCurrentUserId(),
                Limit: 200,
                Fields: 'RunTimeTicks'
            }).catch(function () { return null; })
        ]).then(function (results) {
            var user = results[0];
            var eps = results[1];
            if (user && user.Configuration) {
                __pjtAutoNextEnabled = !!user.Configuration.EnableNextEpisodeAutoPlay;
            }
            // If user disabled auto-next, don't queue anything ahead.
            if (!__pjtAutoNextEnabled) return;
            if (!eps || !Array.isArray(eps.Items) || !eps.Items.length) return;
            // Locate current in the series episode list, then pick the
            // one immediately after. Limit=200 + scan is more reliable
            // than StartItemId (which jellyfin's apiClient sometimes
            // sends as a parameter the server ignores).
            var curIdxInSeries = -1;
            for (var i = 0; i < eps.Items.length; i++) {
                if (eps.Items[i].Id === item.Id) { curIdxInSeries = i; break; }
            }
            if (curIdxInSeries < 0 || curIdxInSeries >= eps.Items.length - 1) return;
            var nextEp = eps.Items[curIdxInSeries + 1];
            var nextNextEp = eps.Items[curIdxInSeries + 2] || null;
            if (!nextEp || !nextEp.Id || nextEp.Id === item.Id) return;

            __pjtPrewarmed = {
                forEpisodeId: item.Id,
                nextEp: nextEp,
                ts: Date.now()
            };

            // getPlaylist() returns a Promise in modern jellyfin-web (not
            // an array), so we await it instead of `Array.isArray`-ing the
            // wrapping object.
            return Promise.all([
                fetchIntros(apiClient, nextEp.Id),
                Promise.resolve(playbackManager.getPlaylist ? playbackManager.getPlaylist() : [])
            ]).then(function (resp) {
                var intros = resp[0] || [];
                var playlist = resp[1] || [];
                try { markIntrosForSkip(intros); } catch (_) {}

                var curIdxInQueue = -1;
                try { curIdxInQueue = playbackManager.getCurrentPlaylistIndex(); } catch (_) {}
                var queueAlreadyHasNext = Array.isArray(playlist)
                    && curIdxInQueue >= 0
                    && !!playlist[curIdxInQueue + 1];

                // ---- queueNext payload composition ----
                // CRITICAL: jellyfin-web's translateItemsForPlayback expands
                // a single-Episode queueNext into the FULL series queue
                // starting from Pilot. To bypass:
                //   - items.length must be >= 2, AND/OR
                //   - firstItem must NOT be Type='Episode'
                // Intros are Movie items (from the hidden preroll library),
                // so when intros are non-empty they make the firstItem a
                // Movie and expansion is skipped. When intros are empty
                // and we need to add the next episode, we also include
                // nextNextEp so items.length === 2.
                var toQueue;
                if (queueAlreadyHasNext) {
                    // Native auto-advance has next ep covered. Only insert
                    // intros between current and next (they're Movies, no
                    // expansion concern). If no intros, do nothing.
                    if (!intros.length) return;
                    toQueue = intros;
                } else {
                    // Queue would otherwise empty after current — must
                    // inject the next episode.
                    if (intros.length) {
                        // firstItem will be a Movie intro → no expansion.
                        toQueue = intros.concat([nextEp]);
                    } else if (nextNextEp) {
                        // No intros AND no leading non-Episode → pad with
                        // nextNextEp so items.length === 2 → no expansion.
                        toQueue = [nextEp, nextNextEp];
                    } else {
                        // Last episode of series; we can't pad. Accept
                        // that no-intros + queue-empty + last-ep =
                        // playback ends naturally (no auto-next exists).
                        return;
                    }
                }
                try {
                    playbackManager.queueNext({ items: toQueue });
                    log('queued ahead: ' + intros.length + ' preroll(s) '
                        + '(queueHasNext=' + queueAlreadyHasNext + ', payload=' + toQueue.length + ')');
                } catch (err) {
                    console.warn(TAG, 'queueNext failed', err);
                }
            });
        });
    }

    // Shared state for auto-next safety gates.
    var __pjtAutoNext = { lastFiredAtMs: 0, lastFiredFor: null };

    // Pre-warmed bundle used to make handleAutoNextOnStop FULLY SYNCHRONOUS
    // when conditions allow. Populated by maybePrefetchNextUp during the
    // back half of an episode; consumed when that episode hits its natural
    // end. Synchronous == we can call playbackManager.play() before jellyfin
    // -web's queue-empty handling gets a chance to navigate the user back
    // to the home screen.
    var __pjtPrewarmed = null;
    // Long-lived snapshot of EnableNextEpisodeAutoPlay so the stop handler
    // doesn't need to await getCurrentUser(). Refreshed by prefetch.
    var __pjtAutoNextEnabled = null;

    function handleAutoNextOnStop(playbackManager, info) {
        if (!info) return;
        // If jellyfin-web already has a next item queued, let its native
        // auto-advance handle it. We only fill in when the queue is empty.
        if (info.nextItem) return;

        // Don't fire while our own patched play() is mid-execution (e.g.
        // the user clicked "next episode" themselves and we're already
        // building a new queue) — that's the most likely cause of the
        // "show plays twice with offset audio" bug.
        if (window.__projectionistPlayInFlight) {
            log('auto-next: skipped — play() already in flight');
            return;
        }

        // Cooldown: never fire auto-next more than once per 10 seconds.
        var nowMs = Date.now();
        if (nowMs - __pjtAutoNext.lastFiredAtMs < 10000) {
            log('auto-next: skipped — cooldown active');
            return;
        }

        var state = info.state || {};
        var nowPlayingItem = state.NowPlayingItem
            || (state.PlayState && state.PlayState.NowPlayingItem)
            || null;
        if (!nowPlayingItem) {
            try { nowPlayingItem = playbackManager.currentItem && playbackManager.currentItem(); } catch (_) {}
        }
        if (!nowPlayingItem || nowPlayingItem.Type !== 'Episode') return;
        if (!nowPlayingItem.SeriesId) return;

        // Strict natural-end check. Require BOTH a positive runtime AND
        // a position at or beyond 85% of it. Without this, a "show only
        // played for a second then jumped" symptom could trigger us to
        // fire a duplicate playback while the real one is still loading.
        var positionTicks = (state.PlayState && state.PlayState.PositionTicks) || 0;
        var runTimeTicks = nowPlayingItem.RunTimeTicks || 0;
        if (runTimeTicks <= 0) {
            log('auto-next: skipped — no runtime info on stopped item');
            return;
        }
        if (positionTicks < runTimeTicks * 0.85) {
            // user stopped or navigated away before reaching the end
            return;
        }

        // ---- SYNC FAST PATH ----
        // If we pre-warmed during the back half of this episode (see
        // maybePrefetchNextUp), we already know the next-up item and the
        // user's auto-next preference. Fire playbackManager.play() RIGHT
        // NOW, in the same event-loop tick as playbackstop. No awaits =
        // no chance for jellyfin-web's queue-empty handling to navigate
        // the user back to the home screen between this episode ending
        // and the next one starting.
        if (__pjtAutoNextEnabled === false) {
            // user explicitly disabled auto-next-episode
            return;
        }
        if (__pjtPrewarmed
            && __pjtPrewarmed.forEpisodeId === nowPlayingItem.Id
            && __pjtPrewarmed.nextEp
            && (nowMs - __pjtPrewarmed.ts) < 120000) {
            var prewarmedNextEp = __pjtPrewarmed.nextEp;
            __pjtPrewarmed = null; // consume
            if (prewarmedNextEp.Id !== nowPlayingItem.Id) {
                __pjtAutoNext.lastFiredAtMs = nowMs;
                __pjtAutoNext.lastFiredFor = prewarmedNextEp.Id;
                log('auto-next (sync): playing', prewarmedNextEp.Name || prewarmedNextEp.Id);
                try { playbackManager.play({ items: [prewarmedNextEp] }); }
                catch (err) { console.warn(TAG, 'auto-next sync play failed', err); }
                return;
            }
        }

        // ---- ASYNC SLOW PATH ----
        // Pre-warm didn't fire (very short episode, user jumped to end,
        // first play after a tab refresh, etc). Fall back to the original
        // async lookup. The home-screen flash will be visible here, but
        // for the common case the sync path above keeps it clean.
        var apiClient = getApiClient();
        if (!apiClient) return;

        apiClient.getCurrentUser().then(function (user) {
            if (!user || !user.Configuration || !user.Configuration.EnableNextEpisodeAutoPlay) {
                // user disabled auto-next in their settings; respect it
                return null;
            }
            // Use getEpisodes with StartItemId to get the episode AFTER
            // current — NOT NextUp (which returns the first unwatched ep
            // and gives the wrong answer when watching mid-series).
            return apiClient.getEpisodes(nowPlayingItem.SeriesId, {
                UserId: apiClient.getCurrentUserId(),
                StartItemId: nowPlayingItem.Id,
                Limit: 2,
                Fields: 'RunTimeTicks,MediaSourceCount'
            });
        }).then(function (eps) {
            if (!eps || !Array.isArray(eps.Items) || eps.Items.length < 2) return;
            var nextEp = eps.Items[1];
            if (!nextEp || !nextEp.Id) return;
            // Don't loop on the same episode (e.g. if it's still unwatched
            // because we triggered playbackstop before the server recorded
            // completion).
            if (nextEp.Id === nowPlayingItem.Id) return;
            // Don't fire for the same target within the cooldown window
            // (extra belt-and-braces if cooldown timestamp is stale).
            if (__pjtAutoNext.lastFiredFor === nextEp.Id
                && nowMs - __pjtAutoNext.lastFiredAtMs < 15000) {
                log('auto-next: skipped — already fired for this target recently');
                return;
            }
            __pjtAutoNext.lastFiredAtMs = nowMs;
            __pjtAutoNext.lastFiredFor = nextEp.Id;
            log('auto-next: playing', nextEp.Name || nextEp.Id);
            // No delay — intros for nextEp were pre-warmed during the
            // back half of the current episode (see maybePrefetchNextUp),
            // so the wrapped play() is a synchronous cache hit on
            // fetchIntros and beats jellyfin-web's queue-empty navigate-
            // to-home. Without the pre-warm, falling back to a network
            // round-trip would re-introduce the home-screen flash.
            try { playbackManager.play({ items: [nextEp] }); }
            catch (err) { console.warn(TAG, 'auto-next play failed', err); }
        }).catch(function (err) {
            console.warn(TAG, 'auto-next lookup failed', err);
        });
    }

    waitForPlaybackManager(patch);
})();

// ============== Post-roll hook (MVP) ==============
// Listen for playbackstop on a FEATURE (not on a preroll/post-roll itself).
// When the feature stops, fetch /Plugins/Projectionist/PostRoll/Picks and
// log the candidate count. Actual playback requires items in the hidden
// library and will be wired up in a future patch release.
(function () {
    'use strict';

    function tryRequire(name) {
        try {
            if (window.require) return window.require(name);
            if (window.RequireJS) return window.RequireJS(name);
        } catch (_) {}
        return null;
    }

    function getPlaybackManager() {
        return window.playbackManager || tryRequire('playbackManager');
    }

    function getEvents() {
        return window.Events || tryRequire('events') || tryRequire('Events');
    }

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function isPrerollItem(item) {
        if (!item) return false;
        if (item.SeriesName === 'Projectionist Prerolls' ||
            item.ParentName === 'Projectionist Prerolls' ||
            item.CollectionName === 'Projectionist Prerolls') return true;
        return false;
    }

    function attachPostRoll() {
        var events = getEvents();
        var pm = getPlaybackManager();
        if (!events || !pm) {
            setTimeout(attachPostRoll, 400);
            return;
        }
        events.on(pm, 'playbackstop', function (e, stopInfo) {
            try {
                // Guard: don't trigger if we're stopping a preroll/post-roll itself.
                if (window.__projectionistPostRollPlaying === true) return;
                var item = null;
                try {
                    if (stopInfo && stopInfo.item) item = stopInfo.item;
                    else if (stopInfo && stopInfo.mediaInfo) item = stopInfo.mediaInfo;
                    else if (typeof pm.currentItem === 'function') item = pm.currentItem();
                } catch (_) {}
                if (isPrerollItem(item)) return;

                var ac = getApiClient();
                if (!ac) return;
                ac.fetch({ url: ac.getUrl('Plugins/Projectionist/PostRoll/Picks'), type: 'GET', dataType: 'json' })
                    .then(function (res) {
                        var items = (res && (res.Items || res.items)) || [];
                        try { console.log('[Projectionist] post-roll candidates:', items.length); } catch (_) {}
                        // MVP: log only. Future patch release will play the picks
                        // (requires items to be addressable from the hidden library).
                    })
                    .catch(function () {});
            } catch (_) {}
        });
    }
    attachPostRoll();
})();

// ============== Skip button (video-element fallback) ==============
// Modern Jellyfin Web removed window.require + window.playbackManager from
// the global scope, so the original playbackManager-based skip button never
// fires. This IIFE hooks the <video> element directly via MutationObserver:
//   1. When a <video> appears, listen for 'loadedmetadata' / 'playing'.
//   2. Parse the itemId from the stream URL (/Videos/{id}/stream.*).
//   3. Ask the server whether that item belongs to the hidden Projectionist
//      Prerolls library. Cache the result.
//   4. If yes, show the skip button (parented to the video's container).
//   5. Click = video.currentTime = video.duration, which triggers Jellyfin
//      to advance to the next item in the play queue.
(function () {
    'use strict';
    var BTN_ID = 'pjt-skip-btn';
    var STYLE_ID = 'pjt-skip-style-v2';
    var ITEM_CACHE = {}; // itemId -> isPreroll
    var PREROLL_PATHS = null; // Set<string> populated from /Plugins/Projectionist/Prerolls
    var enabled = true;
    var minSkipSeconds = 0;
    var configLoadedV2 = false;

    function dlog(event, data) {
        try {
            window.__pjtDebug = window.__pjtDebug || { entries: [], state: {} };
            window.__pjtDebug.entries.push({ t: Date.now(), event: 'v2:' + event, data: data });
            if (window.__pjtDebug.entries.length > 200) window.__pjtDebug.entries.shift();
        } catch (_) {}
    }

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function loadConfigV2(attempts) {
        attempts = attempts || 0;
        var ac = getApiClient();
        var token = null;
        try { token = ac && (typeof ac.accessToken === 'function' ? ac.accessToken() : ac.accessToken); } catch (_) {}
        if (!ac || !token) {
            // Not signed in yet (fresh load lands on the login page): the
            // request would only 401 and never be retried. Wait locally.
            setTimeout(function () { loadConfigV2(attempts); }, 1000);
            return;
        }
        ac.fetch({ url: ac.getUrl('Plugins/Projectionist/HookSettings'), type: 'GET', dataType: 'json' })
            .then(function (cfg) {
                if (!cfg) return;
                enabled = (cfg.EnableSkippablePrerolls !== undefined ? cfg.EnableSkippablePrerolls : true) !== false;
                minSkipSeconds = cfg.SkippableAfterSeconds || 0;
                configLoadedV2 = true;
                dlog('config-loaded', { enabled: enabled, minSkipSeconds: minSkipSeconds });
                // Pre-warm the preroll-path cache so the very first playback
                // doesn't have to wait on /Prerolls inside isPrerollItemId.
                loadPrerollPaths();
            })
            .catch(function (e) {
                dlog('config-failed', { msg: String(e) });
                if (attempts < 5) setTimeout(function () { loadConfigV2(attempts + 1); }, 3000);
            });
    }
    loadConfigV2();

    function ensureStyleV2() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent =
            '#' + BTN_ID + '{position:fixed;right:32px;bottom:120px;z-index:2147483000;'
            + 'background:rgba(20,20,28,.85);color:#fff;border:1px solid rgba(229,9,20,.6);'
            + 'border-radius:6px;padding:10px 18px;font:600 13px/1 -apple-system,sans-serif;'
            + 'cursor:pointer;backdrop-filter:blur(8px);transition:opacity .2s,transform .2s;'
            + 'letter-spacing:.04em;text-transform:uppercase;}'
            + '#' + BTN_ID + ':hover{background:rgba(229,9,20,.9);}'
            + '#' + BTN_ID + '.pjt-hidden{opacity:0;pointer-events:none;transform:translateY(8px);}';
        document.head.appendChild(s);
    }

    function getOverlayHost() {
        // Fullscreen: button MUST live inside document.fullscreenElement,
        // otherwise the browser doesn't paint anything outside that subtree.
        var fs = document.fullscreenElement || document.webkitFullscreenElement;
        if (fs) return fs;
        // Non-fullscreen: DO NOT parent into .videoPlayerContainer. That
        // container has a CSS transform (translateZ for GPU acceleration)
        // which makes its fixed-positioned children relative to ITSELF, not
        // the viewport. Result: `position:fixed; bottom:120px` renders 120px
        // from the container's bottom — usually offscreen, since the
        // container is full-viewport tall. Anchoring at document.body keeps
        // the button viewport-relative. The button's z-index (2147483000) is
        // already maxed out so it floats above Jellyfin's player chrome.
        return document.body;
    }

    function parseItemIdFromSrc(src) {
        if (!src) return null;
        // Jellyfin stream URLs: /Videos/{itemId}/stream.* or /Videos/{itemId}/master.m3u8
        var m = /\/Videos\/([0-9a-f]{32})\//i.exec(src);
        return m ? m[1] : null;
    }

    function loadPrerollPaths() {
        var ac = getApiClient();
        if (!ac) return Promise.resolve(null);
        return ac.fetch({ url: ac.getUrl('Plugins/Projectionist/Prerolls'), type: 'GET', dataType: 'json' })
            .then(function (data) {
                var set = new Set();
                (data && data.Files ? data.Files : []).forEach(function (f) {
                    if (f && f.Path) set.add(String(f.Path).toLowerCase());
                });
                PREROLL_PATHS = set;
                dlog('preroll-paths-loaded', { count: set.size });
                return set;
            })
            .catch(function (e) {
                dlog('preroll-paths-fail', { msg: String(e) });
                return null;
            });
    }

    function ensurePrerollPaths() {
        if (PREROLL_PATHS !== null) return Promise.resolve(PREROLL_PATHS);
        return loadPrerollPaths();
    }

    function isPrerollItemId(itemId) {
        if (!itemId) return Promise.resolve(false);
        if (ITEM_CACHE[itemId] !== undefined) return Promise.resolve(ITEM_CACHE[itemId]);
        var ac = getApiClient();
        if (!ac) return Promise.resolve(false);
        return Promise.all([
            ac.fetch({ url: ac.getUrl('Items/' + itemId), type: 'GET', dataType: 'json' }).catch(function () { return null; }),
            ensurePrerollPaths(),
        ]).then(function (results) {
            var item = results[0];
            var paths = results[1];
            if (!item) return false;
            var byPath = paths && item.Path && paths.has(String(item.Path).toLowerCase());
            var byName = !!item && (
                item.SeriesName === 'Projectionist Prerolls' ||
                item.ParentName === 'Projectionist Prerolls' ||
                item.CollectionName === 'Projectionist Prerolls' ||
                item.GrandparentName === 'Projectionist Prerolls'
            );
            var verdict = byPath || byName;
            ITEM_CACHE[itemId] = verdict;
            dlog('item-resolved', {
                id: itemId, name: item && item.Name, path: item && item.Path,
                byPath: !!byPath, byName: !!byName, verdict: verdict,
                pathCount: paths ? paths.size : 0,
            });
            return verdict;
        });
    }

    var currentPrerollFileName = null;
    var attachedVideos = new WeakSet();
    // The <video> currently showing a preroll, and the pending delayed
    // reveal. onVideoMetadata runs for both 'loadedmetadata' and 'playing';
    // an uncancelled reveal used to fire after a manual skip and put the
    // button over the episode, where clicking it jumped to the episode's end.
    var skipTarget = null;
    var revealTimer = null;

    function cancelReveal() {
        if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
    }

    function showSkip(video) {
        ensureStyleV2();
        skipTarget = video;
        var host = getOverlayHost();
        var btn = document.getElementById(BTN_ID);
        if (!btn) {
            btn = document.createElement('button');
            btn.id = BTN_ID;
            btn.textContent = 'Skip';
            btn.addEventListener('click', function () {
                var video = skipTarget;
                cancelReveal();
                btn.classList.add('pjt-hidden');
                if (!video) return;
                try {
                    // skip-rate reporting
                    try {
                        var ac = getApiClient();
                        if (ac && currentPrerollFileName) {
                            var seconds = video ? Math.round(video.currentTime * 10) / 10 : 0;
                            fetch(ac.getUrl('Plugins/Projectionist/SkipReport'), {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-Emby-Token': ac.accessToken(),
                                    'Authorization': 'MediaBrowser Token="' + ac.accessToken() + '"'
                                },
                                body: JSON.stringify({ fileName: currentPrerollFileName, secondsBeforeSkip: seconds }),
                            }).catch(function () {});
                        }
                    } catch (_) {}
                    // Skip = jump to the end. Jellyfin advances the queue on 'ended'.
                    if (video && isFinite(video.duration) && video.duration > 0) {
                        video.currentTime = Math.max(0, video.duration - 0.25);
                    }
                } catch (_) {}
            });
        }
        if (btn.parentNode !== host) host.appendChild(btn);
        btn.classList.remove('pjt-hidden');
        dlog('show', { host: host.tagName + '.' + (host.className || ''), btnReused: !!btn.parentNode });
    }

    function hideSkip() {
        cancelReveal();
        skipTarget = null;
        var btn = document.getElementById(BTN_ID);
        if (btn) btn.classList.add('pjt-hidden');
        currentPrerollFileName = null;
    }

    function onVideoMetadata(video) {
        if (!enabled) return;
        var src = video.currentSrc || video.src;
        var id = parseItemIdFromSrc(src);
        dlog('video-metadata', { id: id, src: (src || '').substring(0, 120) });
        if (!id) { hideSkip(); return; }
        isPrerollItemId(id).then(function (isPre) {
            // The element may have moved on to the next item while the
            // lookup was in flight.
            if (parseItemIdFromSrc(video.currentSrc || video.src) !== id) return;
            if (!isPre) { hideSkip(); return; }
            // Track filename for skip-rate reporting.
            var ac = getApiClient();
            if (ac) {
                ac.fetch({ url: ac.getUrl('Items/' + id), type: 'GET', dataType: 'json' })
                    .then(function (item) {
                        if (item && item.Path) {
                            var p = String(item.Path);
                            var slash = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
                            currentPrerollFileName = slash >= 0 ? p.substring(slash + 1) : p;
                        } else if (item && item.Name) {
                            currentPrerollFileName = item.Name;
                        }
                    })
                    .catch(function () {});
            }
            cancelReveal();
            if (minSkipSeconds > 0) {
                revealTimer = setTimeout(function () {
                    revealTimer = null;
                    // Only reveal if the same preroll is still on screen.
                    if (parseItemIdFromSrc(video.currentSrc || video.src) !== id || video.ended) return;
                    showSkip(video);
                }, minSkipSeconds * 1000);
            } else {
                showSkip(video);
            }
        });
    }


    function attachToVideo(video) {
        if (attachedVideos.has(video)) return;
        attachedVideos.add(video);
        dlog('attach-video', { src: (video.currentSrc || video.src || '').substring(0, 80) });
        video.addEventListener('loadedmetadata', function () { onVideoMetadata(video); });
        video.addEventListener('playing', function () { onVideoMetadata(video); });
        video.addEventListener('emptied', hideSkip);
        video.addEventListener('ended', hideSkip);
        // If metadata already loaded, kick now.
        if (video.readyState >= 1) onVideoMetadata(video);
    }

    // Initial sweep + MutationObserver for late-arriving <video> elements.
    function sweepVideos() {
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) attachToVideo(videos[i]);
    }
    sweepVideos();
    try {
        var obs = new MutationObserver(function () { sweepVideos(); });
        obs.observe(document.body, { childList: true, subtree: true });
    } catch (_) {}

    // Reparent on fullscreen transitions.
    function reparent() {
        var btn = document.getElementById(BTN_ID);
        if (!btn) return;
        var host = getOverlayHost();
        if (btn.parentNode !== host) host.appendChild(btn);
    }
    document.addEventListener('fullscreenchange', reparent);
    document.addEventListener('webkitfullscreenchange', reparent);

    dlog('v2-installed', {});
})();
