(function () {
    'use strict';
    if (window !== window.top || !window.chrome || !window.chrome.webview) return;
    const origin = location.origin;
    if (window.__lampawinOrigin !== origin) return;
    let current = null;
    let profileTimer;
    let installed = false;
    let installing = false;
    const send = (type, payload) => window.chrome.webview.postMessage({ version: 1, type, payload });
    function saveProfile() {
        const state = {};
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            state[key] = localStorage.getItem(key);
        }
        send('profile', state);
    }
    function scheduleSave() { clearTimeout(profileTimer); profileTimer = setTimeout(saveProfile, 500); }
    function saveProgress(progress) {
        if (!current || progress.sessionId !== current.id) return;
        const timeline = current.data.timeline;
        if (timeline && typeof timeline.handler === 'function' && Number.isFinite(progress.durationSeconds) && progress.durationSeconds > 0) {
            const time = Math.max(0, Math.min(progress.timeSeconds, progress.durationSeconds));
            const percent = progress.state === 'ended' ? 100 : Math.round(time / progress.durationSeconds * 100);
            timeline.handler(percent, time, progress.durationSeconds);
        }
        scheduleSave();
    }
    function chooseUrl(data) {
        if (typeof data.url === 'string' && data.url) return data.url;
        const quality = data.quality || {};
        for (const value of Object.values(quality)) {
            const url = typeof value === 'string' ? value : value && value.url;
            if (typeof url === 'string' && url) return url;
        }
        return '';
    }
    function play(data) {
        let url = chooseUrl(data);
        if (!url) { Lampa.Noty.show('Не удалось получить адрес видео'); return; }
        if (Lampa.Torserver && Lampa.Torserver.toPlayUrl) url = Lampa.Torserver.toPlayUrl(url);
        const id = crypto.randomUUID();
        current = { id, data };
        send('play', { sessionId: id, url, title: data.title || 'Просмотр',
            startSeconds: Math.max(0, Number(data.timeline && data.timeline.time) || 0) });
    }
    window.chrome.webview.addEventListener('message', function (event) {
        const message = event.data;
        if (!message || message.version !== 1) return;
        if (message.type === 'progress') saveProgress(message.payload);
        if (message.type === 'closed' && current && message.payload.sessionId === current.id) {
            if (message.payload.progress) saveProgress(message.payload.progress);
            current = null;
            scheduleSave();
        }
        if (message.type === 'flushProfile') saveProfile();
    });
    async function install() {
        if (installed || installing) return;
        if (!window.appready || !window.Lampa || !Lampa.Storage || !Lampa.Player || !Lampa.Player.listener) return;
        installing = true;
        try {
            const response = await fetch('/lampawin-config.json', { credentials: 'same-origin' });
            if (!response.ok) throw new Error('config');
            const config = await response.json();
            const settings = {
                parser_use: true, parser_torrent_type: 'jackett', parser_use_link: 'one',
                jackett_url: config.jackettUrl, jackett_key: config.jackettKey, jackett_url_two: '', jackett_key_two: '',
                torrserver_url: config.torrServerUrl, torrserver_url_two: '', torrserver_use_link: 'one',
                torrserver_auth: false, player: 'lampa', parse_timeout: 60
            };
            for (const [name, value] of Object.entries(settings)) Lampa.Storage.set(name, value);
            // Jackett gets only the text query. Keep the selected item's identity in
            // our own gateway context, so same-name remakes do not leak into this screen.
            const jquery = window.jQuery || window.$;
            if (jquery && typeof jquery.ajaxPrefilter === 'function') jquery.ajaxPrefilter(options => {
                try {
                    const url = new URL(options.url, origin);
                    if (url.origin !== origin || !url.pathname.startsWith('/jackett/') || !url.pathname.endsWith('/results')) return;
                    const active = Lampa.Activity && Lampa.Activity.active();
                    if (!active || active.component !== 'torrents' || !active.movie) return;
                    const movie = active.movie;
                    const date = movie.first_air_date || movie.release_date || '';
                    const year = Number(String(date).slice(0, 4));
                    if (!Number.isInteger(year) || year < 1800 || year > 2100) return;
                    url.searchParams.set('lampawin_year', String(year));
                    url.searchParams.set('lampawin_kind', movie.first_air_date || movie.number_of_seasons ? 'tv' : 'movie');
                    options.url = url.href;
                } catch (_) { /* User-entered searches without catalog context still work. */ }
            });
            Lampa.Player.listener.follow('create', event => {
                if (!event || !event.data || typeof event.abort !== 'function') return;
                event.abort();
                play(event.data);
            });
            if (Lampa.Storage.listener) Lampa.Storage.listener.follow('change', scheduleSave);
            // Save direct caches and history as well, which do not always emit Storage.change.
            setInterval(scheduleSave, 10000);
            window.addEventListener('pagehide', saveProfile);
            installed = true;
            send('ready', { indexers: config.indexers, warnings: config.warnings });
            scheduleSave();
        } catch (_) {
            installed = false;
            send('error', { message: 'Не удалось подключить локальные компоненты' });
        } finally { installing = false; }
    }
    const timer = setInterval(() => { install(); if (installed) clearInterval(timer); }, 100);
    setTimeout(() => {
        clearInterval(timer);
        if (!installed) send('error', { message: 'Интерфейс Lampa не загрузился. Нажмите «Восстановить».' });
    }, 60000);
    install();
})();
