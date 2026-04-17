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

        const onKeyDown = (e) => {
            // IME 変換中は IME に委ねる（日本語変換候補の確定などを壊さない）
            // - e.isComposing: 標準プロパティ
            // - keyCode === 229: 古い挙動の保険（IME中は 229 になるブラウザあり）
            if (e.isComposing || e.keyCode === 229) return;

            // スペース以外 / 修飾キー併用は対象外
            if (e.key !== ' ') return;
            if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;

            // 既に長押し録音中 → repeat を抑制するだけ
            if (isLongPress) {
                e.preventDefault();
                return;
            }

            // repeat は初回追跡開始以外では無視（タイマーは1本のみ）
            if (e.repeat) {
                e.preventDefault();
                return;
            }

            // 初回押下: 通常のスペース挿入を抑制して閾値タイマーを開始
            e.preventDefault();
            tracking = true;
            if (holdTimer) clearTimeout(holdTimer);
            holdTimer = setTimeout(() => {
                if (!tracking) return;
                isLongPress = true;
                if (dotNetRef) {
                    try { dotNetRef.invokeMethodAsync('OnSpaceHoldVoiceStart'); } catch {}
                }
            }, this._spaceHoldThresholdMs);
        };

        const onKeyUp = (e) => {
            if (e.isComposing || e.keyCode === 229) return;
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

        el.addEventListener('keydown', onKeyDown);
        el.addEventListener('keyup', onKeyUp);
        el.addEventListener('blur', onBlur);

        this._spaceBindings.set(textAreaId, { el, onKeyDown, onKeyUp, onBlur });
        return true;
    },

    unbindSpaceHoldPushToTalk(textAreaId) {
        const b = this._spaceBindings.get(textAreaId);
        if (!b) return;
        try {
            b.el.removeEventListener('keydown', b.onKeyDown);
            b.el.removeEventListener('keyup', b.onKeyUp);
            b.el.removeEventListener('blur', b.onBlur);
        } catch {}
        this._spaceBindings.delete(textAreaId);
    }
};
