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
    }
};
