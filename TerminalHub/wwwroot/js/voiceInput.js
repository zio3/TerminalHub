// 音声入力マネージャー（実験的機能）
// Web Speech API (webkitSpeechRecognition) を使用したPush-to-Talk方式の音声入力
window.voiceInputManager = {
    recognition: null,
    isRecording: false,
    confirmedText: '',
    baseText: '',
    dotNetRef: null,
    textAreaId: null,

    // ブラウザが音声認識をサポートしているか確認
    isSupported() {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    },

    // 初期化：SpeechRecognitionインスタンスを作成し、マイク権限を事前取得
    async init(dotNetRef) {
        this.dotNetRef = dotNetRef;

        if (!this.isSupported()) {
            console.warn('[VoiceInput] Web Speech API はこのブラウザでサポートされていません');
            return false;
        }

        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        this.recognition = new SpeechRecognition();
        this.recognition.lang = 'ja-JP';
        this.recognition.interimResults = true;
        this.recognition.continuous = true;

        this.recognition.onresult = (event) => {
            let interim = '';
            let confirmed = '';

            for (let i = 0; i < event.results.length; i++) {
                const result = event.results[i];
                if (result.isFinal) {
                    confirmed += result[0].transcript;
                } else {
                    interim += result[0].transcript;
                }
            }

            this.confirmedText = confirmed;
            const fullText = this.baseText + confirmed + interim;

            // テキストエリアに反映
            if (this.textAreaId) {
                const textArea = document.getElementById(this.textAreaId);
                if (textArea) {
                    // Blazorのバインディングを更新するためにイベントを発火
                    const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
                        window.HTMLTextAreaElement.prototype, 'value'
                    ).set;
                    nativeInputValueSetter.call(textArea, fullText);
                    textArea.dispatchEvent(new Event('input', { bubbles: true }));
                }
            }

            // Blazor側にも通知
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnVoiceResult', fullText);
            }
        };

        this.recognition.onend = () => {
            // 録音中に予期せず停止した場合は自動再開
            if (this.isRecording) {
                try {
                    this.recognition.start();
                } catch (e) {
                    console.warn('[VoiceInput] 再開に失敗:', e);
                    this.isRecording = false;
                }
            }
        };

        this.recognition.onerror = (event) => {
            // no-speech は通常動作なので無視
            if (event.error === 'no-speech') return;
            console.warn('[VoiceInput] エラー:', event.error);
            if (event.error === 'not-allowed') {
                this.isRecording = false;
            }
        };

        // マイク権限を事前取得
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            stream.getTracks().forEach(track => track.stop());
        } catch (e) {
            console.warn('[VoiceInput] マイク権限の取得に失敗:', e);
        }

        return true;
    },

    // 録音開始
    startRecording(textAreaId) {
        if (!this.recognition || this.isRecording) return;

        this.textAreaId = textAreaId;
        this.confirmedText = '';

        // テキストエリアの現在値をベースとして保持
        const textArea = document.getElementById(textAreaId);
        this.baseText = textArea ? textArea.value : '';

        this.isRecording = true;
        try {
            this.recognition.start();
        } catch (e) {
            console.warn('[VoiceInput] 開始に失敗:', e);
            this.isRecording = false;
        }
    },

    // 録音停止
    stopRecording() {
        if (!this.recognition || !this.isRecording) return;

        this.isRecording = false;
        try {
            this.recognition.stop();
        } catch (e) {
            console.warn('[VoiceInput] 停止に失敗:', e);
        }
    },

    // クリーンアップ
    dispose() {
        this.stopRecording();
        if (this.recognition) {
            this.recognition.onresult = null;
            this.recognition.onend = null;
            this.recognition.onerror = null;
            this.recognition = null;
        }
        this.dotNetRef = null;
    },

    // ===== 中ボタン Push-to-Talk =====
    // テキストエリア上でマウスの中ボタンを押している間だけ録音を行う。
    // ブラウザの中ボタンオートスクロールを抑制するため preventDefault する。
    // textareaId → bind 情報のマップ。コンポーネント破棄時に unbind で解除する。
    _middleBindings: new Map(),

    bindMiddlePushToTalk(textAreaId, dotNetRef) {
        const el = document.getElementById(textAreaId);
        if (!el) return false;

        // 既存バインドがあれば一度外す（再初期化時の重複登録防止）
        this.unbindMiddlePushToTalk(textAreaId);

        let holding = false;

        const onMouseDown = (e) => {
            if (e.button !== 1) return; // middle button only
            e.preventDefault();
            if (holding) return;
            holding = true;
            if (dotNetRef) {
                try { dotNetRef.invokeMethodAsync('OnMiddleMouseVoiceStart'); } catch {}
            }
        };

        const onMouseUp = (e) => {
            if (e.button !== 1) return;
            if (!holding) return;
            holding = false;
            if (dotNetRef) {
                try { dotNetRef.invokeMethodAsync('OnMiddleMouseVoiceStop'); } catch {}
            }
        };

        // 中ボタン押下中にテキストエリア外に出ても、確実に録音を止めるため document 側でも拾う
        const onDocumentMouseUp = (e) => {
            if (e.button !== 1) return;
            if (!holding) return;
            holding = false;
            if (dotNetRef) {
                try { dotNetRef.invokeMethodAsync('OnMiddleMouseVoiceStop'); } catch {}
            }
        };

        // 中ボタン押下でブラウザが auxclick / scroll を出すのを抑える
        const onAuxClick = (e) => {
            if (e.button === 1) e.preventDefault();
        };

        el.addEventListener('mousedown', onMouseDown);
        el.addEventListener('mouseup', onMouseUp);
        el.addEventListener('auxclick', onAuxClick);
        document.addEventListener('mouseup', onDocumentMouseUp);

        this._middleBindings.set(textAreaId, {
            el, onMouseDown, onMouseUp, onAuxClick, onDocumentMouseUp
        });
        return true;
    },

    unbindMiddlePushToTalk(textAreaId) {
        const b = this._middleBindings.get(textAreaId);
        if (!b) return;
        try {
            b.el.removeEventListener('mousedown', b.onMouseDown);
            b.el.removeEventListener('mouseup', b.onMouseUp);
            b.el.removeEventListener('auxclick', b.onAuxClick);
            document.removeEventListener('mouseup', b.onDocumentMouseUp);
        } catch {}
        this._middleBindings.delete(textAreaId);
    },

    // ===== スペース長押し Push-to-Talk =====
    // スペース押下を 300ms 遅らせ、閾値を超えたら録音開始、手前で離したら通常のスペース入力。
    _spaceBindings: new Map(),
    _spaceHoldThresholdMs: 300,

    bindSpaceHoldPushToTalk(textAreaId, dotNetRef) {
        const el = document.getElementById(textAreaId);
        if (!el) return false;

        this.unbindSpaceHoldPushToTalk(textAreaId);

        let tracking = false;        // keydown 受信後、release/threshold 待ち
        let isLongPress = false;     // 閾値到達済み（録音中）
        let holdTimer = null;
        let imeActive = false;       // IME 変換中フラグ（compositionstart/end で追跡）
        let lastCompositionEndAt = 0; // IME 確定直後の grace period 用

        const insertSpaceAtCursor = () => {
            // textarea.value + execCommand は古いが、Blazor のバインディング
            // 更新と cursor 位置維持のため、setRangeText を使う
            try {
                const start = el.selectionStart ?? el.value.length;
                const end = el.selectionEnd ?? el.value.length;
                el.setRangeText(' ', start, end, 'end');
                el.dispatchEvent(new Event('input', { bubbles: true }));
            } catch {
                // 失敗時は素直に追加
                el.value = el.value + ' ';
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }
        };

        const thresholdMs = this._spaceHoldThresholdMs;
        // 毎回読み取って、DevTools で後からフラグを切り替えられるようにする。
        // 使い方: voiceInputManager._spacePttDebug = true
        const isDebug = () => !!(window.voiceInputManager && window.voiceInputManager._spacePttDebug);

        const onKeyDown = (e) => {
            // 判定材料を全部ログに出す（debug 有効時）
            if (isDebug()) {
                console.log('[SpacePTT] keydown check:',
                    'key=', JSON.stringify(e.key),
                    'code=', e.code,
                    'keyCode=', e.keyCode,
                    'isComposing=', e.isComposing,
                    'imeActive=', imeActive,
                    'msSinceCompEnd=', Date.now() - lastCompositionEndAt,
                    'repeat=', e.repeat);
            }

            // IME 変換中は IME に委ねる（日本語変換候補の確定などを壊さない）
            // - e.isComposing: 標準プロパティ
            // - keyCode === 229: 古い挙動の保険（IME中は 229 になるブラウザあり）
            // - imeActive: compositionstart/end で追跡する自前フラグ
            // - lastCompositionEndAt: IME 確定直後 (50ms) はまだ IME が消化中の可能性があるので grace period
            if (imeActive) { if (isDebug()) console.log('[SpacePTT]   → skip: imeActive'); return; }
            if (e.isComposing) { if (isDebug()) console.log('[SpacePTT]   → skip: isComposing'); return; }
            if (e.keyCode === 229) { if (isDebug()) console.log('[SpacePTT]   → skip: keyCode=229'); return; }
            if (Date.now() - lastCompositionEndAt < 50) { if (isDebug()) console.log('[SpacePTT]   → skip: recent compositionend'); return; }

            // スペース以外 / 修飾キー併用は対象外
            if (e.key !== ' ') return;
            if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;

            // スペース関連の既定動作は一律抑止する（first press / repeat / long press すべて）。
            // 短押しで離された場合のみ keyup で手動挿入する方針。
            // stopPropagation も併用して Blazor 側の @onkeydown によるバブリング処理を避ける。
            e.preventDefault();
            e.stopPropagation();

            if (isDebug()) console.log('[SpacePTT]   → INTERCEPT (preventDefault) repeat=', e.repeat, 'tracking=', tracking, 'longPress=', isLongPress);

            if (isLongPress) return;
            if (tracking) return; // repeat / 連続 keydown は既に追跡中

            // 初回押下: 閾値タイマーを開始
            tracking = true;
            if (holdTimer) clearTimeout(holdTimer);
            holdTimer = setTimeout(() => {
                if (!tracking) return;
                isLongPress = true;
                if (isDebug()) console.log('[SpacePTT] threshold reached → recording start');
                if (dotNetRef) {
                    try { dotNetRef.invokeMethodAsync('OnSpaceHoldVoiceStart'); } catch {}
                }
            }, thresholdMs);
        };

        const onKeyUp = (e) => {
            if (imeActive || e.isComposing || e.keyCode === 229) return;
            if (e.key !== ' ') return;
            if (!tracking && !isLongPress) return;

            if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
            tracking = false;

            if (isLongPress) {
                isLongPress = false;
                if (dotNetRef) {
                    try { dotNetRef.invokeMethodAsync('OnSpaceHoldVoiceStop'); } catch {}
                }
            } else {
                // 閾値未満で離された → 通常のスペース入力を挿入
                insertSpaceAtCursor();
            }
        };

        // フォーカスを外したときに追跡中なら安全に停止
        const onBlur = () => {
            if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
            const wasLongPress = isLongPress;
            tracking = false;
            isLongPress = false;
            if (wasLongPress && dotNetRef) {
                try { dotNetRef.invokeMethodAsync('OnSpaceHoldVoiceStop'); } catch {}
            }
        };

        // IME 変換開始: 以降 keydown 介入を停止する
        const onCompositionStart = () => {
            imeActive = true;
            // 追跡中だったら安全に解除（先にタイマーが動いてたら止める）
            if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
            tracking = false;
            if (isLongPress) {
                isLongPress = false;
                if (dotNetRef) {
                    try { dotNetRef.invokeMethodAsync('OnSpaceHoldVoiceStop'); } catch {}
                }
            }
            if (isDebug()) console.log('[SpacePTT] compositionstart → IME active');
        };
        const onCompositionEnd = () => {
            imeActive = false;
            lastCompositionEndAt = Date.now();
            if (isDebug()) console.log('[SpacePTT] compositionend');
        };

        // capture=true でバブリング前に受け取り、Blazor の @onkeydown より先に
        // preventDefault / stopPropagation できるようにする。
        el.addEventListener('keydown', onKeyDown, true);
        el.addEventListener('keyup', onKeyUp, true);
        el.addEventListener('blur', onBlur);
        el.addEventListener('compositionstart', onCompositionStart);
        el.addEventListener('compositionend', onCompositionEnd);

        if (isDebug()) console.log('[SpacePTT] bound to', textAreaId);

        this._spaceBindings.set(textAreaId, {
            el, onKeyDown, onKeyUp, onBlur, onCompositionStart, onCompositionEnd
        });
        return true;
    },

    unbindSpaceHoldPushToTalk(textAreaId) {
        const b = this._spaceBindings.get(textAreaId);
        if (!b) return;
        try {
            b.el.removeEventListener('keydown', b.onKeyDown, true);
            b.el.removeEventListener('keyup', b.onKeyUp, true);
            b.el.removeEventListener('blur', b.onBlur);
            b.el.removeEventListener('compositionstart', b.onCompositionStart);
            b.el.removeEventListener('compositionend', b.onCompositionEnd);
        } catch {}
        this._spaceBindings.delete(textAreaId);
    }
};
