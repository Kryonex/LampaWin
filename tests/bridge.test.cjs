'use strict';
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const script = fs.readFileSync(require('node:path').join(__dirname, '../src/LampaWin.Core/Web/lampawin-bridge.js'), 'utf8');
let failures = 1;
const sent = [], handlers = {}, settings = {}, cache = new Map();
let create, storageChanged, searchPrefilter;
const intervals = [];
const context = {
    location: { origin: 'http://127.0.0.1:12345' }, crypto: { randomUUID },
    __lampawinOrigin: 'http://127.0.0.1:12345', console,
    appready: true, URL,
    jQuery: { ajaxPrefilter: callback => { searchPrefilter = callback; } },
    chrome: { webview: { postMessage: m => sent.push(m), addEventListener: (name, cb) => { handlers[name] = cb; } } },
    localStorage: { get length() { return cache.size; }, key: i => [...cache.keys()][i], getItem: k => cache.get(k), setItem: (k, v) => cache.set(k, v) },
    setTimeout: () => 1, clearTimeout: () => {}, setInterval: cb => { intervals.push(cb); return intervals.length; }, clearInterval: () => {},
    addEventListener: () => {},
    fetch: async () => { if (failures-- > 0) throw Error('temporary config failure'); return { ok: true, json: async () => ({ jackettUrl: '/jackett', jackettKey: 'managed', torrServerUrl: '/torrserver', indexers: ['fixture'], warnings: [] }) }; },
    Lampa: {
        Activity: { active: () => ({ component: 'torrents', movie: { first_air_date: '2025-07-13', number_of_seasons: 1 } }) },
        Storage: { set: (k, v) => { settings[k] = v; }, listener: { follow: (_, cb) => { storageChanged = cb; } } },
        Player: { listener: { follow: (_, cb) => { create = cb; } }, playlist: () => {} },
        Torserver: { toPlayUrl: url => url.replace('&preload', '&play') }, Noty: { show: () => {} }, Select: { show: () => {}, close: () => {} }
    }
};
context.window = context; context.top = context;
vm.createContext(context); vm.runInContext(script, context);
(async () => {
    await new Promise(setImmediate);
    assert(sent.some(m => m.type === 'error'), 'temporary configuration error reported');
    intervals[0](); await new Promise(setImmediate);
    assert(sent.some(m => m.type === 'ready'), 'configuration automatically retries after failure');
    assert.equal(settings.parser_torrent_type, 'jackett');
    assert.equal(settings.torrserver_auth, false);
    assert.equal(settings.jackett_key, 'managed');
    const searchRequest = { url: '/jackett/api/v2.0/indexers/all/results?Query=The%20Institute' };
    searchPrefilter(searchRequest);
    assert.equal(new URL(searchRequest.url).searchParams.get('lampawin_year'), '2025');
    assert.equal(new URL(searchRequest.url).searchParams.get('lampawin_kind'), 'tv');
    const externalRequest = { url: 'https://example.org/results' };
    searchPrefilter(externalRequest);
    assert.equal(externalRequest.url, 'https://example.org/results', 'external searches are never modified');
    let aborted = false, timeline;
    create({ data: { url: 'http://127.0.0.1:12345/torrserver/stream?link=fixture', title: 'Фильм', timeline: { time: 42, handler: (...args) => { timeline = args; } } }, abort: () => { aborted = true; } });
    assert(aborted, 'HTML player suppressed');
    const play = sent.find(m => m.type === 'play');
    assert.equal(play.payload.startSeconds, 42, 'resume position sent to native player');
    handlers.message({ data: { version: 1, type: 'progress', payload: { sessionId: 'wrong', timeSeconds: 1, durationSeconds: 120, state: 'playing' } } });
    assert.equal(timeline, undefined, 'stale playback session ignored');
    handlers.message({ data: { version: 1, type: 'progress', payload: { sessionId: play.payload.sessionId, timeSeconds: 60, durationSeconds: 120, state: 'playing' } } });
    assert.deepEqual(timeline, [50, 60, 120], 'native progress returns to Lampa timeline');
    handlers.message({ data: { version: 1, type: 'progress', payload: { sessionId: play.payload.sessionId, timeSeconds: 119, durationSeconds: 120, state: 'ended' } } });
    assert.equal(timeline[0], 100, 'finished media marked fully watched');
    cache.set('history', 'preserved');
    handlers.message({ data: { version: 1, type: 'flushProfile', payload: {} } });
    assert.equal(sent.at(-1).payload.history, 'preserved', 'full profile exports caches and history');
    assert.equal(typeof storageChanged, 'function');
    const queue = [
        { url: 'http://127.0.0.1:12345/torrserver/stream?index=1&preload', title: 'Серия 1' },
        { url: 'http://127.0.0.1:12345/torrserver/stream?index=2&preload', title: 'Серия 2' }
    ];
    create({ data: Object.assign({}, queue[0], { url: queue[0].url.replace('&preload', '&play') }), abort: () => {} });
    context.Lampa.Player.playlist(queue);
    const firstEpisode = sent.filter(m => m.type === 'play').at(-1);
    assert.equal(sent.at(-1).payload.nextTitle, 'Серия 2', 'playlist supplied after Player.play updates native next-episode offer');
    handlers.message({ data: { version: 1, type: 'closed', payload: { sessionId: firstEpisode.payload.sessionId, ended: true } } });
    const countBefore = sent.filter(m => m.type === 'play').length;
    handlers.message({ data: { version: 1, type: 'nextEpisode', payload: { sessionId: 'stale' } } });
    assert.equal(sent.filter(m => m.type === 'play').length, countBefore, 'stale next-episode command ignored');
    handlers.message({ data: { version: 1, type: 'nextEpisode', payload: { sessionId: firstEpisode.payload.sessionId } } });
    const secondEpisode = sent.filter(m => m.type === 'play').at(-1);
    assert.equal(secondEpisode.payload.title, 'Серия 2', 'next command starts the actual next playlist item');
    assert.equal(sent.at(-1).payload.nextTitle, null, 'last episode has no next offer');
    handlers.message({ data: { version: 1, type: 'progress', payload: { sessionId: secondEpisode.payload.sessionId, timeSeconds: 17, durationSeconds: 100, state: 'playing' } } });
    handlers.message({ data: { version: 1, type: 'retryPlayback', payload: { sessionId: secondEpisode.payload.sessionId } } });
    assert.equal(sent.filter(m => m.type === 'play').at(-1).payload.startSeconds, 17, 'retry retains most recent playback position');
    console.log('PASS bridge retry, automatic configuration, native playback interception, resume, stale session, timeline, completion and profile export');
})().catch(error => { console.error(error); process.exitCode = 1; });
