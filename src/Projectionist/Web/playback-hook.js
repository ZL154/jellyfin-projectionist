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

    function loadConfig() {
        try {
            var ac = (window.ApiClient || (window.connectionManager && connectionManager.currentApiClient && connectionManager.currentApiClient()));
            if (!ac) return;
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
                })
                .catch(function () {});
        } catch (_) {}
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
        if (!item) return false;
        // Heuristic: item belongs to "Projectionist Prerolls" library, OR its path
        // is in the set we recorded when prepending intros.
        if (item.Id && prerollIds.has(item.Id)) return true;
        if (prerollPaths.has(item.Path)) return true;
        if (item.Name && prerollNames.has(item.Name)) return true;
        // Fallback: check parent name
        if (item.SeriesName === 'Projectionist Prerolls' ||
            item.ParentName === 'Projectionist Prerolls' ||
            item.CollectionName === 'Projectionist Prerolls') return true;
        return false;
    }

    window.__projectionistMarkPreroll = function (path, id, name) {
        if (path) prerollPaths.add(path);
        if (id) prerollIds.add(id);
        if (name) prerollNames.add(name);
    };

    function getApiClientLocal() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function getOverlayHost() {
        return document.fullscreenElement
            || document.webkitFullscreenElement
            || document.querySelector('.videoPlayerContainer')
            || document.querySelector('.htmlVideoPlayerContainer')
            || document.body;
    }

    function showButton() {
        ensureStyle();
        var host = getOverlayHost();
        var btn = document.getElementById(BTN_ID);
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
                            fetch('/Plugins/Projectionist/SkipReport', {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-Emby-Token': ac.accessToken(),
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

    function attachSkipWatcher() {
        var events = getEvents();
        var pm = getPlaybackManager();
        if (!events || !pm) {
            setTimeout(attachSkipWatcher, 400);
            return;
        }
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

    function getFirstPlayItem(options) {
        if (!options) return null;
        if (Array.isArray(options.items) && options.items.length) return options.items[0];
        if (options.item) return options.item;
        if (options.Item) return options.Item;
        if (options.currentItem) return options.currentItem;
        return null;
    }

    function getFirstPlayId(options) {
        if (!options) return null;
        if (Array.isArray(options.ids) && options.ids.length) return options.ids[0];
        if (Array.isArray(options.itemIds) && options.itemIds.length) return options.itemIds[0];
        if (Array.isArray(options.ItemIds) && options.ItemIds.length) return options.ItemIds[0];
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
            if (!apiClient || !options) return origPlay(options);

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
                return origPlay(options);
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
                    if (!isVideoFeature(item)) return origPlay(options);
                    return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                        markIntrosForSkip(intros);
                        preloadFeature(apiClient, item, options);
                        return origPlay(options);
                    });
                }

                return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                    if (!intros.length) {
                        log('no preroll for episode', item.Name || item.Id);
                        return origPlay(options);
                    }
                    log('queueing', intros.length, 'preroll(s) before episode', item.Name || item.Id);
                    preloadFeature(apiClient, item, options);
                    markIntrosForSkip(intros);

                    // Prepend intros to whichever input the caller used.
                    var newOptions = Object.assign({}, options);
                    if (Array.isArray(options.items)) {
                        newOptions.items = intros.concat(options.items);
                        delete newOptions.ids;
                        delete newOptions.itemIds;
                        delete newOptions.ItemIds;
                    } else if (Array.isArray(options.ids)) {
                        var introIds = intros.map(function (i) { return i.Id; });
                        newOptions.ids = introIds.concat(options.ids);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.itemIds)) {
                        var introItemIds = intros.map(function (i) { return i.Id; });
                        newOptions.itemIds = introItemIds.concat(options.itemIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.ItemIds)) {
                        var introUpperItemIds = intros.map(function (i) { return i.Id; });
                        newOptions.ItemIds = introUpperItemIds.concat(options.ItemIds);
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
                    return origPlay(newOptions);
                });
            });
        };

        log('episode preroll hook installed');
    }

    waitForPlaybackManager(patch);
})();

// ============== Cinema countdown overlay (MVP) ==============
// When playbackstart fires AND the current item is a preroll AND
// config.EnableCountdownOverlay is true, render a 5-4-3-2-1 numeral overlay
// with the upcoming feature title. Auto-removes after
// config.CountdownDurationSeconds seconds.
(function () {
    'use strict';
    var OVERLAY_ID = 'pjt-countdown';
    var countdownEnabled = false;
    var countdownDuration = 5;
    var configLoaded = false;

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

    function loadConfig() {
        try {
            var ac = getApiClient();
            if (!ac) return;
            ac.fetch({ url: ac.getUrl('Plugins/Projectionist/Config'), type: 'GET', dataType: 'json' })
                .then(function (cfg) {
                    if (!cfg) return;
                    configLoaded = true;
                    countdownEnabled = (cfg.EnableCountdownOverlay !== undefined
                        ? cfg.EnableCountdownOverlay
                        : cfg.enableCountdownOverlay) === true;
                    var dur = (cfg.CountdownDurationSeconds !== undefined
                        ? cfg.CountdownDurationSeconds
                        : cfg.countdownDurationSeconds);
                    if (typeof dur === 'number' && dur > 0) countdownDuration = dur;
                })
                .catch(function () {});
        } catch (_) {}
    }
    loadConfig();

    function isPrerollItem(item) {
        if (!item) return false;
        if (item.SeriesName === 'Projectionist Prerolls' ||
            item.ParentName === 'Projectionist Prerolls' ||
            item.CollectionName === 'Projectionist Prerolls') return true;
        // Defer to the skip-button IIFE's tracker if exposed via globals.
        try {
            // The skip-button IIFE marks prerolls via window.__projectionistMarkPreroll;
            // there's no readable mirror, so fall back to library-name heuristic only.
        } catch (_) {}
        return false;
    }

    function renderCountdown() {
        try {
            var existing = document.getElementById(OVERLAY_ID);
            if (existing) existing.remove();
            var overlay = document.createElement('div');
            overlay.id = OVERLAY_ID;
            overlay.style.cssText =
                'position:fixed;top:0;left:0;width:100vw;height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;background:rgba(0,0,0,0.5);z-index:99999;color:#fff;font-family:sans-serif;pointer-events:none;';
            var num = document.createElement('div');
            num.style.cssText = 'font-size:30vw;font-weight:900;line-height:1;text-shadow:0 0 30px rgba(229,9,20,0.8);';
            var title = document.createElement('div');
            title.style.cssText = 'font-size:5vw;margin-top:24px;opacity:0.85;font-weight:300;';
            title.textContent = window.__projectionistUpcomingFeatureTitle || '';
            overlay.appendChild(num);
            if (window.__projectionistUpcomingFeatureTitle) overlay.appendChild(title);
            document.body.appendChild(overlay);
            var n = countdownDuration;
            num.textContent = n;
            var t = setInterval(function () {
                n--;
                if (n < 1) { clearInterval(t); overlay.remove(); return; }
                num.textContent = n;
            }, 1000);
        } catch (_) {}
    }

    function attachCountdownWatcher() {
        var events = getEvents();
        var pm = getPlaybackManager();
        if (!events || !pm) {
            setTimeout(attachCountdownWatcher, 400);
            return;
        }
        events.on(pm, 'playbackstart', function (e, player) {
            try {
                if (!countdownEnabled) return;
                var item = pm.currentItem(player);
                if (!isPrerollItem(item)) return;
                renderCountdown();
            } catch (_) {}
        });
    }
    attachCountdownWatcher();
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
