// Tiny cookie helpers used to persist the selected household id across page
// refreshes. Only called from OnAfterRenderAsync (never during prerendering),
// so there is no need to guard against a missing `document` here.
window.mimamoriUi = {
    // Wraps the callback-based Geolocation API in a promise so Blazor can await it.
    // Resolves to null on refusal, timeout or an unavailable sensor: the settings
    // screen treats "we could not tell where you are" as an ordinary outcome and
    // leaves the family to pick a place name instead.
    getPosition: function () {
        return new Promise(function (resolve) {
            if (!navigator.geolocation) {
                resolve(null);
                return;
            }
            navigator.geolocation.getCurrentPosition(
                function (position) {
                    resolve({
                        latitude: position.coords.latitude,
                        longitude: position.coords.longitude
                    });
                },
                function () { resolve(null); },
                { enableHighAccuracy: false, timeout: 10000, maximumAge: 600000 });
        });
    },
    getCookie: function (name) {
        const match = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    },
    setCookie: function (name, value, days) {
        const maxAgeDays = days || 365;
        const expires = new Date(Date.now() + maxAgeDays * 86400000).toUTCString();
        document.cookie = name + '=' + encodeURIComponent(value) + '; expires=' + expires + '; path=/; SameSite=Lax';
    },
    isInLineApp: function () {
        // 「LINEへ戻る」はLINEアプリ内ブラウザ(LIFF)から開いたときだけ意味があります。
        // PCのブラウザで押しても戻り先は無く、LINEの案内ページに飛ぶだけなので、
        // ここで判定して導線を出し分けます。LINEのUAは "Line/" を含みます。
        try {
            const ua = navigator.userAgent || '';
            if (/\bLine\//i.test(ua)) {
                return true;
            }
            // LIFFのSDKが読み込まれている場合はそちらを信用します。
            if (window.liff && typeof window.liff.isInClient === 'function') {
                return window.liff.isInClient();
            }
            return false;
        } catch (e) {
            return false;
        }
    },
    initMascot: async function () {
        // 3Dは装飾なので、失敗しても画面は止めない。ただし黙って静止画に
        // 落ちると「アニメーションが壊れている」と見分けがつかず、原因を
        // 追う手がかりがどこにも残らない。本番でも必ず理由をconsoleに出す。
        try {
            await import('/mimamori-mascot-3d.js');
            window.mimamoriMascot.init();
        } catch (error) {
            console.error('Mascot 3D failed to start; the still image stays.', error);
            document.querySelectorAll('.mascot-viewer').forEach((host) => host.classList.add('is-fallback'));
            throw error;
        }
    },
    reactMascot: async function (name) {
        if (!window.mimamoriMascot) {
            await import('/mimamori-mascot-3d.js');
        }
        window.mimamoriMascot.react(name);
    },

    // --- Voice input -------------------------------------------------------
    // Speaking is easier than typing for an older family member, so the assistant
    // box can be filled by voice. This uses the browser's built-in Web Speech API:
    // no audio ever leaves the browser through our server, and no speech service
    // has to be provisioned or paid for.
    speechSupported: function () {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    },

    // Listens for a single utterance and resolves with the recognised text
    // (empty string when nothing usable was heard). Never rejects, so the caller
    // can simply fall back to the keyboard.
    listenOnce: function (lang) {
        const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!Recognition) {
            return Promise.resolve('');
        }

        // Only one microphone session may be live at a time; starting a second one
        // throws in Chrome, so reuse-and-abort rather than stacking sessions.
        if (this._recognition) {
            try { this._recognition.abort(); } catch { /* already stopped */ }
            this._recognition = null;
        }

        return new Promise(resolve => {
            const recognition = new Recognition();
            recognition.lang = lang || 'ja-JP';
            recognition.interimResults = false;
            recognition.maxAlternatives = 1;
            recognition.continuous = false;

            let settled = false;
            const finish = value => {
                if (settled) {
                    return;
                }
                settled = true;
                this._recognition = null;
                resolve(value || '');
            };

            recognition.onresult = event => {
                const result = event.results && event.results[0] && event.results[0][0];
                finish(result ? result.transcript : '');
            };
            // onerror covers a denied microphone permission, no speech, and network
            // failures alike; onend covers the browser simply giving up. Either way
            // the user gets the text box back instead of a stuck "listening" state.
            recognition.onerror = () => finish('');
            recognition.onend = () => finish('');

            this._recognition = recognition;
            try {
                recognition.start();
            } catch {
                finish('');
            }
        });
    },

    stopListening: function () {
        if (this._recognition) {
            try { this._recognition.abort(); } catch { /* already stopped */ }
            this._recognition = null;
        }
    }
};
