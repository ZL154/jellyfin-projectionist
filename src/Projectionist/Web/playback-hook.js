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
            if (!ac) {
                // Retry while ApiClient initialises. Up to ~30 seconds.
                if (attempts < 150) setTimeout(function () { loadConfig(attempts + 1); }, 200);
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
                .catch(function (err) { dlog('config:fetch-failed', { msg: String(err) }); });
        } catch (e) { dlog('config:throw', { msg: String(e) }); }
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
        if (!ac) {
            if (attempts < 150) setTimeout(function () { loadConfigV2(attempts + 1); }, 200);
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
            .catch(function (e) { dlog('config-failed', { msg: String(e) }); });
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

    function showSkip(video) {
        ensureStyleV2();
        var host = getOverlayHost();
        var btn = document.getElementById(BTN_ID);
        if (!btn) {
            btn = document.createElement('button');
            btn.id = BTN_ID;
            btn.textContent = 'Skip';
            btn.addEventListener('click', function () {
                try {
                    // skip-rate reporting
                    try {
                        var ac = getApiClient();
                        if (ac && currentPrerollFileName) {
                            var seconds = video ? Math.round(video.currentTime * 10) / 10 : 0;
                            fetch('/Plugins/Projectionist/SkipReport', {
                                method: 'POST',
                                headers: { 'Content-Type': 'application/json', 'X-Emby-Token': ac.accessToken() },
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
            if (minSkipSeconds > 0) {
                setTimeout(function () { showSkip(video); }, minSkipSeconds * 1000);
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
        video.addEventListener('pause', function () {
            // keep button visible during user-pause - they may want to skip during pause
        });
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
