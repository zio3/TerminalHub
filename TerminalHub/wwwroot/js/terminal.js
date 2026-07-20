// モバイル用タッチスクロール: touchmove を xterm の scrollLines に変換する。
// xterm.js 6 でビューポートが刷新され、タッチイベントが canvas に食われて
// ネイティブスクロールが「ページ全体のスクロール」になってしまう退行への対処。
// 指を離した後は簡易な慣性スクロール（減衰付き）を行う。
function attachTouchScroll(term, element) {
    // 再アタッチ対応: コンテナ div はセッション切替やターミナル再作成のたびに使い回されるが、
    // createMultiSessionTerminal は毎回呼ばれる。前回のリスナーを外さずに追加すると
    // リスナーが積み重なり（スクロール量が訪問回数分だけ多重化）、古いクロージャが
    // 破棄済みの term を参照し続けて例外を吐く。必ず前回分を外してから付け直す。
    if (typeof element._touchScrollDetach === 'function') {
        element._touchScrollDetach();
    }

    let lastY = null;       // 直前のタッチ Y 座標（null = 追跡していない）
    let residual = 0;       // 1セル未満の移動量の積み残し（px）
    let velocity = 0;       // 慣性用の速度（px/フレーム換算）
    let lastMoveTime = 0;
    let momentumId = null;  // 慣性スクロールの requestAnimationFrame ID

    // 描画セルの実高さ（px）。内部APIが取れない場合は要素高さ÷行数で近似
    const cellHeight = () => {
        const cell = term._core?._renderService?.dimensions?.css?.cell;
        return (cell && cell.height > 0) ? cell.height : Math.max(1, element.clientHeight / term.rows);
    };

    const stopMomentum = () => {
        if (momentumId !== null) {
            cancelAnimationFrame(momentumId);
            momentumId = null;
        }
    };

    // 積み残し付きで px をスクロール行数に変換して適用
    const scrollByPixels = (dy) => {
        const ch = cellHeight();
        residual += dy;
        const lines = Math.trunc(residual / ch);
        if (lines !== 0) {
            residual -= lines * ch;
            // 指を下へ動かす(dy>0) = 過去（スクロールバック側）を見る = scrollLines は負方向
            term.scrollLines(-lines);
        }
    };

    const onTouchStart = (e) => {
        if (e.touches.length !== 1) { lastY = null; return; }
        stopMomentum();
        lastY = e.touches[0].clientY;
        residual = 0;
        velocity = 0;
        lastMoveTime = e.timeStamp;
    };

    // preventDefault でページ全体へのスクロール伝播を止めるため passive: false
    const onTouchMove = (e) => {
        if (lastY === null || e.touches.length !== 1) return;
        const y = e.touches[0].clientY;
        const dy = y - lastY;
        lastY = y;

        try {
            scrollByPixels(dy);
        } catch {
            lastY = null; // ターミナル破棄後は追跡を止める
            return;
        }

        const dt = Math.max(1, e.timeStamp - lastMoveTime);
        velocity = dy / dt * 16; // 16ms(1フレーム)あたりの px に正規化
        lastMoveTime = e.timeStamp;

        e.preventDefault();
    };

    const onTouchEnd = () => {
        if (lastY === null) return;
        lastY = null;

        // 慣性スクロール（フリック後の減速）
        let v = velocity;
        residual = 0;
        const step = () => {
            v *= 0.95; // 減衰率
            if (Math.abs(v) < 0.5) { momentumId = null; return; }
            try {
                scrollByPixels(v);
            } catch {
                momentumId = null; // ターミナル破棄後は停止
                return;
            }
            momentumId = requestAnimationFrame(step);
        };
        momentumId = requestAnimationFrame(step);
    };

    const onTouchCancel = () => {
        lastY = null;
    };

    element.addEventListener('touchstart', onTouchStart, { passive: true });
    element.addEventListener('touchmove', onTouchMove, { passive: false });
    element.addEventListener('touchend', onTouchEnd, { passive: true });
    element.addEventListener('touchcancel', onTouchCancel, { passive: true });

    // デタッチ関数を要素に保持（次回の attach と cleanupTerminal から呼ばれる）
    element._touchScrollDetach = () => {
        stopMomentum();
        element.removeEventListener('touchstart', onTouchStart);
        element.removeEventListener('touchmove', onTouchMove);
        element.removeEventListener('touchend', onTouchEnd);
        element.removeEventListener('touchcancel', onTouchCancel);
        element._touchScrollDetach = null;
    };
}

// デバウンス関数
function debounce(func, wait) {
    let timeout;
    return function executedFunction(...args) {
        const later = () => {
            clearTimeout(timeout);
            func(...args);
        };
        clearTimeout(timeout);
        timeout = setTimeout(later, wait);
    };
}

// ResizeObserver管理クラス
class ResizeObserverManager {
    constructor() {
        this.observers = new Map();
        this.resizeCallbacks = new Map();
    }

    add(sessionId, element, callback) {
        // 既存のObserverがあれば削除
        this.remove(sessionId);
        
        // デバウンス処理を追加（200ms待機）
        const debouncedCallback = debounce(callback, 200);
        this.resizeCallbacks.set(sessionId, debouncedCallback);
        
        const observer = new ResizeObserver(debouncedCallback);
        observer.observe(element);
        this.observers.set(sessionId, observer);
        // console.log(`[ResizeObserverManager] Observer追加: sessionId=${sessionId}`);
    }

    remove(sessionId) {
        if (this.observers.has(sessionId)) {
            const observer = this.observers.get(sessionId);
            observer.disconnect();
            this.observers.delete(sessionId);
            this.resizeCallbacks.delete(sessionId);
            // console.log(`[ResizeObserverManager] Observer削除: sessionId=${sessionId}`);
        }
    }

    removeAll() {
        this.observers.forEach((observer, sessionId) => {
            observer.disconnect();
            // console.log(`[ResizeObserverManager] Observer削除: sessionId=${sessionId}`);
        });
        this.observers.clear();
        this.resizeCallbacks.clear();
    }

    has(sessionId) {
        return this.observers.has(sessionId);
    }
}

// グローバルインスタンス
window.resizeObserverManager = new ResizeObserverManager();

// フロー制御管理クラス
class FlowControlManager {
    constructor() {
        // 閾値設定（バイト単位）
        this.HIGH_WATERMARK = 500000;  // 500KB - 一時停止閾値
        this.LOW_WATERMARK = 100000;   // 100KB - 再開閾値

        // セッションごとの状態管理
        this.sessionStates = new Map();
    }

    getState(sessionId) {
        if (!this.sessionStates.has(sessionId)) {
            this.sessionStates.set(sessionId, {
                pendingBytes: 0,
                isPaused: false,
                dotNetRef: null
            });
        }
        return this.sessionStates.get(sessionId);
    }

    setDotNetRef(sessionId, dotNetRef) {
        const state = this.getState(sessionId);
        state.dotNetRef = dotNetRef;
    }

    // 書き込み前に呼び出す - バイト数を加算
    beforeWrite(sessionId, dataLength) {
        const state = this.getState(sessionId);
        state.pendingBytes += dataLength;

        // HIGH_WATERMARKを超えたら一時停止を通知
        if (!state.isPaused && state.pendingBytes > this.HIGH_WATERMARK) {
            state.isPaused = true;
            console.log(`[FlowControl] Session ${sessionId}: Pausing output (pending: ${state.pendingBytes} bytes)`);
            this.notifyPause(sessionId);
        }
    }

    // 書き込み完了後に呼び出す - バイト数を減算
    afterWrite(sessionId, dataLength) {
        const state = this.getState(sessionId);
        state.pendingBytes -= dataLength;

        // LOW_WATERMARKを下回ったら再開を通知
        if (state.isPaused && state.pendingBytes < this.LOW_WATERMARK) {
            state.isPaused = false;
            console.log(`[FlowControl] Session ${sessionId}: Resuming output (pending: ${state.pendingBytes} bytes)`);
            this.notifyResume(sessionId);
        }
    }

    notifyPause(sessionId) {
        const state = this.getState(sessionId);
        if (state.dotNetRef) {
            state.dotNetRef.invokeMethodAsync('OnFlowControlPause', sessionId)
                .catch(err => console.error('[FlowControl] Failed to notify pause:', err));
        }
    }

    notifyResume(sessionId) {
        const state = this.getState(sessionId);
        if (state.dotNetRef) {
            state.dotNetRef.invokeMethodAsync('OnFlowControlResume', sessionId)
                .catch(err => console.error('[FlowControl] Failed to notify resume:', err));
        }
    }

    // セッション削除時のクリーンアップ
    remove(sessionId) {
        this.sessionStates.delete(sessionId);
    }

    // デバッグ用: 現在の状態を取得
    getStatus(sessionId) {
        const state = this.getState(sessionId);
        return {
            pendingBytes: state.pendingBytes,
            isPaused: state.isPaused,
            highWatermark: this.HIGH_WATERMARK,
            lowWatermark: this.LOW_WATERMARK
        };
    }
}

// グローバルフロー制御マネージャー
window.flowControlManager = new FlowControlManager();

// ターミナル内リンクの動作モード（true = 開かずにURLをコピーする）。
// モバイル全画面（ホーム画面ショートカット等）では遷移すると戻れなくなるため、
// デバイス別設定として Blazor 側から setTerminalLinkCopyMode で反映される。
let terminalLinkCopyMode = false;
window.setTerminalLinkCopyMode = function (enabled) {
    terminalLinkCopyMode = !!enabled;
};

// リンク活性化を左クリックだけに限定するガード。
// xterm はリンクの activate を呼ぶ前にマウスボタンを見ないため、素通しにすると
// 右クリック（コンテキストメニューを出したいだけ）や中クリックでもリンクが開いてしまう。
// button: 0=左 / 1=中 / 2=右。event が無い経路（将来の API 変更等）では従来どおり通す。
function isPrimaryClick(e) {
    return !e || e.button === undefined || e.button === 0;
}

// リンクのアクティベート処理（WebLinksAddon / フォールバック共通）
function activateTerminalLink(uri) {
    if (terminalLinkCopyMode) {
        copyLinkToClipboard(uri);
    } else {
        window.open(uri, '_blank', 'noopener,noreferrer');
    }
}

// URLをクリップボードにコピーして通知を出す
function copyLinkToClipboard(uri) {
    // タップでターミナル(の隠しtextarea)にフォーカスが移り、モバイルでは
    // ソフトウェアキーボードが開いてしまう。コピーモードでは入力する意図が
    // 無いので、フォーカスを外してキーボードの出現を抑止する。
    if (document.activeElement && typeof document.activeElement.blur === 'function') {
        document.activeElement.blur();
    }

    const notify = (ok) => showLinkCopyNotice(uri, ok);
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(uri).then(() => notify(true)).catch(() => notify(false));
    } else {
        notify(false);
    }
}

// コピー結果の軽量トースト（Blazor を介さず JS だけで完結させる）
// キーボードや下部パネルに隠れないよう画面上部に表示する
function showLinkCopyNotice(uri, success) {
    const existing = document.getElementById('terminal-link-copy-notice');
    if (existing) existing.remove();

    const div = document.createElement('div');
    div.id = 'terminal-link-copy-notice';
    div.style.cssText = 'position:fixed;left:50%;top:16px;transform:translateX(-50%);' +
        'max-width:90vw;padding:8px 16px;border-radius:8px;z-index:3000;' +
        'font-size:0.85rem;color:#fff;box-shadow:0 2px 8px rgba(0,0,0,.4);' +
        'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;' +
        (success ? 'background:#198754;' : 'background:#dc3545;');
    div.textContent = success
        ? `📋 コピーしました: ${uri}`
        : `コピーできませんでした: ${uri}`;
    document.body.appendChild(div);
    setTimeout(() => div.remove(), 3000);
}

// バッファ行のテキストを取得（各リンクプロバイダー共通）
// ワイド文字（全角等）の2セル目は getChars() が空を返すためスペースで埋め、
// 文字列インデックスとターミナル列番号を揃える
// 行のテキストと、各文字が始まるセル列(0始まり)の対応表を返す。
//
// 全角文字は2セルを占め、xterm は2セル目を「幅0・getChars()=''」のセルとして返す。
// これを ' ' として拾うと（旧実装）パスの途中に空白が入り、空白を含まない前提の
// pathRegex がそこで切れる（例: C:\Users\info\テスト用メモ.md が
// "C:\Users\info\テ" までしかリンク化されない）。幅0のセルは飛ばす。
//
// また x はセル列なので、全角があると文字数（match.index）とズレる。columns で引き直す。
function getBufferLineText(line) {
    let text = '';
    const columns = [];
    for (let i = 0; i < line.length; i++) {
        const cell = line.getCell(i);
        if (!cell || cell.getWidth() === 0) {
            continue; // 全角の後続セル（幅0）は本体側に含まれている
        }
        const chars = cell.getChars() || ' ';
        // サロゲートペアや結合文字で chars が複数コード単位になる場合も同じ列を指す
        for (let k = 0; k < chars.length; k++) {
            columns.push(i);
        }
        text += chars;
    }
    columns.push(line.length); // 末尾（マッチ終端の変換用）
    return { text, columns };
}

// ファイルパス検出の正規表現（リンクプロバイダと選択/右クリックメニューで共有）。
// 空白・引用符・括弧・全角句読点は含めない（: は C:\ のため許容）。
// 第1候補: スラッシュ/バックスラッシュを含むトークン全体、第2候補: 拡張子付きファイル名。
// ここを唯一の定義とし、右クリックメニュー側もこの source から都度 RegExp を作って使う
// （＝表示上クリック可能なパスと、右クリックで拾うパスが必ず一致する）。
const FILE_PATH_TOKEN_REGEX = /[^\s"'`()\[\]{}<>|;,！？。、（）「」]*[\\/][^\s"'`()\[\]{}<>|;,！？。、（）「」]*|[^\s"'`()\[\]{}<>|;,！？。、（）「」]+\.[A-Za-z][A-Za-z0-9]{0,7}/g;

// ファイルパス検出の設定
// Claude Code 等が出力するファイル/フォルダ表記をクリック可能にする。
// 検出は割り切り: 「/ または \ を含むトークン（末尾まで丸ごと）」と
// 「拡張子付きファイル（拡張子は英字始まり、バージョン番号 1.0.65 等を除外）」。
// ドットファイル単体（.gitignore 等）は検出対象外（許容）。
// フォルダかファイルかは C# 側で実在チェックして判定する。誤検出してもクリック時に「見つかりません」に落ちるだけ。
// クリック時: コピー動作モード(terminalLinkCopyMode)ならコピー、通常は C# 側で
// フォルダ=エクスプローラー / ファイル=既定アプリ で開く。
function setupFilePathDetection(term, sessionId) {
    if (typeof term.registerLinkProvider !== 'function') {
        console.warn('[Path Detection] registerLinkProvider not available');
        return;
    }

    // 検出パターンは FILE_PATH_TOKEN_REGEX（モジュール先頭）に一本化。ここでは lastIndex を
    // 汚さないよう source から専用インスタンスを作る。
    const pathRegex = new RegExp(FILE_PATH_TOKEN_REGEX.source, 'g');

    const linkProvider = {
        provideLinks: (bufferLineNumber, callback) => {
            // bufferLineNumber は 1 始まり、getLine() は 0 始まり
            const line = term.buffer.active.getLine(bufferLineNumber - 1);
            if (!line) {
                callback(undefined);
                return;
            }

            const { text: lineText, columns } = getBufferLineText(line);

            const links = [];
            let match;
            pathRegex.lastIndex = 0;
            while ((match = pathRegex.exec(lineText)) !== null) {
                const text = match[0];

                // スラッシュだけの並び（罫線的な表記等）はリンク化しない
                if (/^[\\/]+$/.test(text)) continue;

                // コミットハッシュの範囲表記（68bf789..9931160 等、fetch/push 出力）は
                // 拡張子パターンに誤マッチするためスキップし、ハッシュリンク側に任せる
                if (/^[0-9a-f]{7,40}\.\.[0-9a-f]{7,40}$/i.test(text)) continue;

                // URL は URL 検出（WebLinksAddon）側に任せる:
                // マッチを含む空白区切りトークンに "://" があればスキップ
                const before = lineText.slice(0, match.index);
                const tokenStart = before.search(/\S+$/) === -1 ? match.index : before.search(/\S+$/);
                const token = lineText.slice(tokenStart).split(/\s/)[0];
                if (token.includes('://')) continue;

                links.push({
                    range: {
                        start: { x: columns[match.index] + 1, y: bufferLineNumber },
                        end: { x: columns[match.index + text.length] + 1, y: bufferLineNumber }
                    },
                    text: text,
                    activate: (e, uri) => { if (isPrimaryClick(e)) activateTerminalPath(sessionId, uri); }
                });
            }

            callback(links.length > 0 ? links : undefined);
        }
    };

    try {
        term.registerLinkProvider(linkProvider);
        console.log('[Path Detection] link provider registered');
    } catch (error) {
        console.error('[Path Detection] Failed to register link provider:', error);
    }
}

// コミットハッシュ検出の設定
// ターミナル出力中の Git コミットハッシュ（7〜40桁の16進）をクリック可能にする。
// クリックで C# 側がコミットを実在チェックし、コミット情報ダイアログを開く。
// アプリ内ダイアログでページ遷移しないため、コピー動作モードでも同じ挙動とする。
// 誤検出対策: 数字のみ/英字のみは除外（バージョン番号や英単語 "defaced" 等を拾わない）、
// 前後が - . / \ _ に隣接するものは除外（GUID の断片やファイル名の一部を拾わない）。
function setupCommitHashDetection(term, sessionId) {
    if (typeof term.registerLinkProvider !== 'function') {
        return;
    }

    const hashRegex = /\b[0-9a-f]{7,40}\b/g;

    const linkProvider = {
        provideLinks: (bufferLineNumber, callback) => {
            // bufferLineNumber は 1 始まり、getLine() は 0 始まり
            const line = term.buffer.active.getLine(bufferLineNumber - 1);
            if (!line) {
                callback(undefined);
                return;
            }

            const { text: lineText, columns } = getBufferLineText(line);

            const links = [];
            let match;
            hashRegex.lastIndex = 0;
            while ((match = hashRegex.exec(lineText)) !== null) {
                const text = match[0];

                // 数字のみ（タイムスタンプ等）・英字のみ（英単語）は除外
                if (!/[a-f]/.test(text) || !/[0-9]/.test(text)) continue;

                // GUID の断片（f49cfab0-13ff-...）やファイル名・パスの一部を拾わない。
                // ただし ".." は範囲表記（68bf789..9931160、push 出力等）なので両側とも許可する
                const prev = lineText[match.index - 1];
                const next = lineText[match.index + text.length];
                const prevIsRange = prev === '.' && lineText[match.index - 2] === '.';
                const nextIsRange = next === '.' && lineText[match.index + text.length + 1] === '.';
                if (prev && /[-.\\/_]/.test(prev) && !prevIsRange) continue;
                if (next && /[-.\\/_]/.test(next) && !nextIsRange) continue;

                links.push({
                    range: {
                        start: { x: columns[match.index] + 1, y: bufferLineNumber },
                        end: { x: columns[match.index + text.length] + 1, y: bufferLineNumber }
                    },
                    text: text,
                    activate: (e, uri) => {
                        if (!isPrimaryClick(e)) return;
                        if (window.terminalHubDotNetRef) {
                            window.terminalHubDotNetRef.invokeMethodAsync('OnTerminalCommitHashClick', sessionId, uri)
                                .catch(err => console.error('[Hash Detection] open failed:', err));
                        }
                    }
                });
            }

            callback(links.length > 0 ? links : undefined);
        }
    };

    try {
        term.registerLinkProvider(linkProvider);
    } catch (error) {
        console.error('[Hash Detection] Failed to register link provider:', error);
    }
}

// ===== PR/Issue 番号検出（#123 クリックで GitHub の該当 PR ページを開く） =====
// 実データ調査より: 実ターミナル出力の #数字 は大半が本物の PR/Issue 参照で、
// 唯一の恒常的な誤検出源は Claude Code の画像貼り付け UI「[Image #1]」だけだったため、それのみ除外する。
function setupPrNumberDetection(term, sessionId) {
    if (typeof term.registerLinkProvider !== 'function') {
        return;
    }

    const prRegex = /#\d{1,6}/g;

    const linkProvider = {
        provideLinks: (bufferLineNumber, callback) => {
            // bufferLineNumber は 1 始まり、getLine() は 0 始まり
            const line = term.buffer.active.getLine(bufferLineNumber - 1);
            if (!line) {
                callback(undefined);
                return;
            }

            const { text: lineText, columns } = getBufferLineText(line);

            const links = [];
            let match;
            prRegex.lastIndex = 0;
            while ((match = prRegex.exec(lineText)) !== null) {
                const text = match[0];

                // 「Image #1」（画像貼り付けマーカー）は PR 参照ではないので除外
                if (/Image\s*$/i.test(lineText.slice(0, match.index))) continue;

                links.push({
                    range: {
                        start: { x: columns[match.index] + 1, y: bufferLineNumber },
                        end: { x: columns[match.index + text.length] + 1, y: bufferLineNumber }
                    },
                    text: text,
                    activate: (e, uri) => { if (isPrimaryClick(e)) activateTerminalPrNumber(sessionId, uri); }
                });
            }

            callback(links.length > 0 ? links : undefined);
        }
    };

    try {
        term.registerLinkProvider(linkProvider);
    } catch (error) {
        console.error('[PR Detection] Failed to register link provider:', error);
    }
}

// パスリンクのアクティベート処理
function activateTerminalPath(sessionId, path) {
    if (terminalLinkCopyMode) {
        // コピー動作モード: URL と同様に開かずコピーする（モバイル全画面向け）
        copyLinkToClipboard(path);
        return;
    }
    if (window.terminalHubDotNetRef) {
        // C# 側でセッションのフォルダ基準に解決し、フォルダ=Explorer / ファイル=既定アプリ で開く
        window.terminalHubDotNetRef.invokeMethodAsync('OnTerminalPathClick', sessionId, path)
            .catch(err => console.error('[Path Detection] open failed:', err));
    }
}

// PR/Issue リンクのアクティベート処理。
// 外部ブラウザを開く（＝外部遷移する）ため、URL/パスと同様にコピー動作モードを尊重する。
// C# 側は copyMode=true のとき開かずに PR ページ URL を返すので、それをクリップボードへコピーする
// （モバイル全画面向け。#123 の生テキストではなく解決後の URL をコピーする方が有用なため）。
function activateTerminalPrNumber(sessionId, prText) {
    if (!window.terminalHubDotNetRef) return;
    window.terminalHubDotNetRef.invokeMethodAsync('OnTerminalPrNumberClick', sessionId, prText, terminalLinkCopyMode)
        .then(url => { if (url) copyLinkToClipboard(url); })
        .catch(err => console.error('[PR Detection] open failed:', err));
}

// URL検出の設定
function setupUrlDetection(term) {
    // WebLinksAddonが利用可能か確認
    if (typeof WebLinksAddon !== 'undefined' && WebLinksAddon.WebLinksAddon) {
        try {
            // WebLinksAddonを作成（URLをクリック可能にする）
            // 既定ハンドラは直接 window.open するため、コピー動作モードを差し込めるようカスタムハンドラを渡す
            const webLinksAddon = new WebLinksAddon.WebLinksAddon((event, uri) => {
                if (isPrimaryClick(event)) activateTerminalLink(uri);
            });
            term.loadAddon(webLinksAddon);
            console.log('[URL Detection] WebLinksAddon loaded successfully');
        } catch (error) {
            console.error('[URL Detection] Failed to load WebLinksAddon:', error);
            // フォールバック: 手動実装を使用
            setupUrlDetectionFallback(term);
        }
    } else {
        console.warn('[URL Detection] WebLinksAddon not available, using fallback');
        // フォールバック: 手動実装を使用
        setupUrlDetectionFallback(term);
    }
}

// WebLinksAddonが使用できない場合のフォールバック実装
function setupUrlDetectionFallback(term) {
    // HTTP/HTTPSのURLパターン
    const urlRegex = /https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)/g;
    
    // registerLinkProvider による手動リンクプロバイダー登録（xterm.js 6.0 でも利用可能）
    if (typeof term.registerLinkProvider === 'function') {
        const linkProvider = {
            provideLinks: (bufferLineNumber, callback) => {
                // bufferLineNumber は 1 始まり、getLine() は 0 始まり
                const line = term.buffer.active.getLine(bufferLineNumber - 1);
                if (!line) {
                    callback(undefined);
                    return;
                }
                
                // 行のテキストを取得
                const { text: lineText, columns } = getBufferLineText(line);

                // URLを検出
                const links = [];
                let match;
                urlRegex.lastIndex = 0; // 正規表現をリセット
                
                while ((match = urlRegex.exec(lineText)) !== null) {
                    const link = {
                        range: {
                            start: { x: columns[match.index] + 1, y: bufferLineNumber },
                            end: { x: columns[match.index + match[0].length] + 1, y: bufferLineNumber }
                        },
                        text: match[0],
                        activate: (e, uri) => {
                            if (!isPrimaryClick(e)) return;
                            console.log('[URL Detection] Link activated:', uri);
                            activateTerminalLink(uri);
                        }
                    };
                    links.push(link);
                }
                
                callback(links.length > 0 ? links : undefined);
            }
        };
        
        try {
            term.registerLinkProvider(linkProvider);
            console.log('[URL Detection] Fallback: Successfully registered link provider');
        } catch (error) {
            console.error('[URL Detection] Fallback: Failed to register link provider:', error);
        }
    } else {
        console.warn('[URL Detection] Fallback: No suitable API found for URL detection');
    }
}

// ---- 選択テキストの右クリックメニュー ----
//
// リンクプロバイダはトークンの「境界」を推測する必要があり、日本語やスペースを含む
// ファイル名では破綻するため対応を見送っている。選択は境界をユーザーが与えてくれるので、
// 選択テキストならその制約がない（「パスっぽいか」の判定自体は容易）。
//
// パスっぽい選択があるときだけ自前メニューを出し、それ以外はネイティブメニューに素通しする。
// こうすると通常の文章を選んでの DeepL・スペルチェック・絵文字は従来どおり使える。

// 選択がパスらしいか。リンクプロバイダの pathRegex と違い境界の切り出しは不要で、
// 「区切り文字を含むか / ドライブレターで始まるか / 拡張子で終わるか」だけ見る。
function looksLikePath(text) {
    if (!text || text.length > 4096 || /[\r\n]/.test(text)) return false;
    return /[\\/]/.test(text) || /^[A-Za-z]:/.test(text) || /\.[A-Za-z][A-Za-z0-9]{0,7}$/.test(text);
}

function closeTerminalContextMenu() {
    const existing = document.getElementById('terminal-context-menu');
    if (existing) existing.remove();
}

function showTerminalContextMenu(x, y, items) {
    closeTerminalContextMenu();

    const menu = document.createElement('div');
    menu.id = 'terminal-context-menu';
    menu.className = 'terminal-context-menu';
    menu.setAttribute('role', 'menu');

    for (const item of items) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'terminal-context-menu-item';
        button.setAttribute('role', 'menuitem');
        button.textContent = item.label;
        button.addEventListener('click', () => {
            closeTerminalContextMenu();
            item.action();
        });
        menu.appendChild(button);
    }

    // 画面外にはみ出さないよう、いったん不可視で置いて実寸から補正する
    menu.style.visibility = 'hidden';
    document.body.appendChild(menu);
    const rect = menu.getBoundingClientRect();
    menu.style.left = `${Math.min(x, window.innerWidth - rect.width - 4)}px`;
    menu.style.top = `${Math.min(y, window.innerHeight - rect.height - 4)}px`;
    menu.style.visibility = '';

    // 次のクリック・ESC・スクロールで閉じる（capture で確実に拾う）
    const dismiss = (e) => {
        if (e.type === 'keydown' && e.key !== 'Escape') return;
        if (e.type === 'mousedown' && menu.contains(e.target)) return;
        closeTerminalContextMenu();
        document.removeEventListener('mousedown', dismiss, true);
        document.removeEventListener('keydown', dismiss, true);
        window.removeEventListener('blur', dismiss);
    };
    document.addEventListener('mousedown', dismiss, true);
    document.addEventListener('keydown', dismiss, true);
    window.addEventListener('blur', dismiss);
}

// 右クリック位置（clientX/Y）をバッファのセル座標 {col:0始まり, row:0始まりの絶対行} に変換する。
// xterm にセル座標を返す公開APIが無いため、.xterm-screen の実寸から算出する。スクリーン領域の
// 幾何は cols×rows セルで一定なので、DOM/Canvas いずれのレンダラでも成立する。範囲外なら null。
function cellFromMouse(term, e) {
    const screen = term.element && term.element.querySelector('.xterm-screen');
    if (!screen) return null;
    const rect = screen.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    const cellW = rect.width / term.cols;
    const cellH = rect.height / term.rows;
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    if (x < 0 || y < 0) return null;
    const col = Math.floor(x / cellW);
    const rowInViewport = Math.floor(y / cellH);
    if (col >= term.cols || rowInViewport >= term.rows) return null;
    return { col, row: term.buffer.active.viewportY + rowInViewport };
}

// バッファ上の (col,row) にパスリンクのトークンがあればその文字列を返す。無ければ null。
// リンクプロバイダ（setupFilePathDetection）と同じ正規表現・同じ除外条件を用いるため、
// 「クリック可能なパスとして表示されているトークン」とちょうど一致する。
function pathTokenAtCell(term, col, row) {
    const line = term.buffer.active.getLine(row);
    if (!line) return null;
    const { text: lineText, columns } = getBufferLineText(line);
    const re = new RegExp(FILE_PATH_TOKEN_REGEX.source, 'g');
    let match;
    while ((match = re.exec(lineText)) !== null) {
        const text = match[0];
        // 以下 3 つの除外は setupFilePathDetection のリンク化条件と同一に保つ
        if (/^[\\/]+$/.test(text)) continue;                                   // 罫線的なスラッシュ列
        if (/^[0-9a-f]{7,40}\.\.[0-9a-f]{7,40}$/i.test(text)) continue;        // コミット範囲表記
        const before = lineText.slice(0, match.index);
        const tokenStart = before.search(/\S+$/) === -1 ? match.index : before.search(/\S+$/);
        const token = lineText.slice(tokenStart).split(/\s/)[0];
        if (token.includes('://')) continue;                                   // URL は URL 検出に委譲
        const startCol = columns[match.index];
        const endCol = columns[match.index + text.length];                     // 次文字の開始列＝排他的終端
        if (col >= startCol && col < endCol) return text;
    }
    return null;
}

// パス対象の共通コンテキストメニュー項目（コピー / 開く / フルパスをコピー）。
// 選択テキストにもパスリンクにも同じメニューを出すため共通化する。
function buildPathMenuItems(sessionId, text) {
    return [
        { label: 'コピー', action: () => copyLinkToClipboard(text) },
        // 「開く」はリンク左クリックと同じ経路（C# 側でセッションのフォルダ基準に解決し、
        // フォルダ=Explorer / ファイル=既定アプリ。実在しなければ警告トースト）
        { label: '開く', action: () => activateTerminalPath(sessionId, text) },
        {
            label: 'フルパスをコピー',
            action: () => {
                if (!window.terminalHubDotNetRef) { copyLinkToClipboard(text); return; }
                window.terminalHubDotNetRef.invokeMethodAsync('ResolveTerminalPath', sessionId, text)
                    .then(full => copyLinkToClipboard(full || text))
                    .catch(() => copyLinkToClipboard(text));
            }
        },
    ];
}

// element へ contextmenu を登録する。attachTouchScroll と同じく、コンテナ div は
// セッションごとに永続する一方 createMultiSessionTerminal は毎回呼ばれるため、
// 前回のリスナーを必ず外してから付け直す（外し忘れるとセッションを開くたび多重登録される）。
function attachSelectionContextMenu(term, element, sessionId) {
    if (typeof element._selectionMenuDetach === 'function') {
        element._selectionMenuDetach();
    }

    const onContextMenu = (e) => {
        // (1) 範囲選択があれば常にそちらを優先（対象は選択テキスト）。
        //     パスっぽい選択のときだけ自前メニューを出し、通常文はネイティブメニューへ委譲。
        let selection = '';
        try {
            selection = (term.getSelection() || '').trim();
        } catch {
            return; // 破棄済み等。ネイティブメニューに任せる
        }

        if (selection) {
            // パスでなければネイティブメニューの方が有用（DeepL・スペルチェック等）なので出さない
            if (!looksLikePath(selection)) return;
            e.preventDefault();
            showTerminalContextMenu(e.clientX, e.clientY, buildPathMenuItems(sessionId, selection));
            return;
        }

        // (2) 選択が無い場合は、右クリック位置にパスリンクがあればそれを対象にする。
        //     「開きたいのではなくフルパスが欲しい」ケースを、リンク左クリック（＝開く）と分けて拾う。
        const cell = cellFromMouse(term, e);
        if (!cell) return;
        const token = pathTokenAtCell(term, cell.col, cell.row);
        if (!token || !looksLikePath(token)) return; // パスリンク上でなければネイティブメニュー
        e.preventDefault();
        showTerminalContextMenu(e.clientX, e.clientY, buildPathMenuItems(sessionId, token));
    };

    element.addEventListener('contextmenu', onContextMenu);

    element._selectionMenuDetach = () => {
        closeTerminalContextMenu();
        element.removeEventListener('contextmenu', onContextMenu);
        element._selectionMenuDetach = null;
    };
}

window.terminalFunctions = {
    // マルチセッション用のターミナル作成関数
    createMultiSessionTerminal: function(terminalId, sessionId, dotNetRef, fontSize) {
        console.log(`[JS] createMultiSessionTerminal開始: terminalId=${terminalId}, sessionId=${sessionId}, fontSize=${fontSize}`);

        // 初期化
        if (!window.multiSessionTerminals) {
            window.multiSessionTerminals = {};
            console.log(`[JS] createMultiSessionTerminal: multiSessionTerminals初期化`);
        }

        // 既存のターミナルがあれば破棄してから再作成
        if (window.multiSessionTerminals[sessionId]) {
            console.warn(`[JS] セッション ${sessionId} の既存ターミナルを破棄して再作成`);
            this.cleanupTerminal(sessionId);
        }

        const Terminal = window.Terminal;
        const FitAddon = window.FitAddon.FitAddon;

        // モバイル判定でフォントサイズを調整（768px以下で2/3サイズ）
        const isMobile = window.innerWidth <= 768;
        const baseFontSize = (typeof fontSize === 'number' && fontSize > 0) ? fontSize : 14;
        const terminalFontSize = isMobile ? Math.round(baseFontSize * 2 / 3) : baseFontSize;

        const term = new Terminal({
            cursorBlink: true,
            fontSize: terminalFontSize,
            // 合成フォント 'TermMix'（app.css の @font-face 参照）。ASCII・罫線は Consolas のまま、
            // 囲み数字（①②③ 等）の範囲だけ等幅日本語フォントへ差し替えて読みやすくする。
            // 末尾はフォント未解決時の保険（Consolas → monospace）。
            fontFamily: "TermMix, 'Cascadia Mono', Consolas, monospace",
            // サーバー側エミュレータの TerminalGrid.MaxScrollback と必ず揃えること。
            // PR #91 以降、画面の真実はエミュレータ側にあり、xterm はセッション切替のたびに
            // そこからのリプレイで作り直される。ここだけ大きくしても、切り替えた瞬間に
            // エミュレータの上限まで減るため「履歴が急に半分になった」ように見えるだけ。
            scrollback: 5000,
            scrollOnInput: true,
            // scrollOnOutput は false にする。true だと出力のたびに最下部へ強制スクロールされ、
            // Claude Code のように毎秒複数回再描画する CLI では、履歴を遡って読むことができなくなる
            // （特にモバイル）。false でも最下部に居るときは新規出力に自動追従する（xterm 標準挙動）。
            scrollOnOutput: false,
            // ED2（CSI 2J 全画面消去）で画面内容をスクロールバックへ退避する（Windows Terminal と同じ挙動）。
            // クリアしても履歴を遡れるようになる。サーバー側のVTエミュレータ（TerminalGrid.EraseInDisplay）
            // も同じ意味論で実装しており、ライブ表示とセッション切替時のリプレイが一致する。
            scrollOnEraseInDisplay: true,
            theme: {
                background: '#1e1e1e',
                foreground: '#d4d4d4',
                cursor: '#d4d4d4',
                black: '#000000',
                red: '#cd3131',
                green: '#0dbc79',
                yellow: '#e5e510',
                blue: '#2472c8',
                magenta: '#bc3fbc',
                cyan: '#11a8cd',
                white: '#e5e5e5',
                brightBlack: '#666666',
                brightRed: '#f14c4c',
                brightGreen: '#23d18b',
                brightYellow: '#f5f543',
                brightBlue: '#3b8eea',
                brightMagenta: '#d670d6',
                brightCyan: '#29b8db',
                brightWhite: '#e5e5e5'
            },
            cols: 120,  // 固定列数
            rows: 30,   // 固定行数
            convertEol: true,
            // xterm.js 6.0 で windowsMode は廃止。ConPTY 前提の Windows ヒューリスティクスは windowsPty で指定する。
            // buildNumber 未指定（= reflow無効・非空白終端行を折返し扱い）で旧 windowsMode: true 相当の挙動を維持。
            windowsPty: { backend: 'conpty' },
            allowProposedApi: true  // 提案中のAPIを許可（windowsPty・スクロールバック保持等で必要）
        });
        
        // FitAddonを使う
        const fitAddon = new FitAddon();
        term.loadAddon(fitAddon);
        
        const element = document.getElementById(terminalId);
        if (element) {
            // 既存の内容をクリア（重要！）- より安全なDOM操作を使用
            while (element.firstChild) {
                element.removeChild(element.firstChild);
            }
            
            term.open(element);

            // ===== モバイル用タッチスクロール =====
            // xterm.js 6 でビューポート実装が刷新され（オーバーレイスクロールバー化）、
            // タッチが canvas に食われてネイティブのタッチスクロールが効かなくなった
            // （v5 までは動作していた退行）。指の移動量を scrollLines に変換して自前で処理する。
            // 併せて app.css の .xterm { touch-action: none } でページ全体へのスクロール伝播を抑止。
            attachTouchScroll(term, element);

            // 選択テキストの右クリックメニュー（パスっぽい選択のときだけ出す）
            attachSelectionContextMenu(term, element, sessionId);

            // リサイズ診断用: 最後に通知したサイズと通知元を記録
            let lastNotifiedSize = { cols: 0, rows: 0, source: '', time: 0 };

            // C#側にリサイズを通知する共通関数（診断情報付き）
            function notifyResize(cols, rows, source, detail) {
                if (!dotNetRef) return;
                const now = Date.now();
                const prev = lastNotifiedSize;
                let dupSource = '';
                if (prev.cols === cols && prev.rows === rows && now - prev.time < 500) {
                    dupSource = prev.source;
                }
                lastNotifiedSize = { cols, rows, source, time: now };
                dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, cols, rows, source, detail, dupSource);
            }

            // ターミナルを同期的にフィット（バッファ書き込み前に確実に初期化完了させるため）
            try {
                fitAddon.fit();
                console.log(`[JS] createMultiSessionTerminal: 初期fit完了 cols=${term.cols}, rows=${term.rows}`);
                notifyResize(term.cols, term.rows, 'init', '');
            } catch (e) {
                console.log(`[JS] createMultiSessionTerminal: 初期fitエラー (無視): ${e.message}`);
            }
            
            // 直接入力モードのハンドラー
            term.onData((data) => {
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('SendInput', sessionId, data);
                }
            });
            
            // レンダリング後のイベント（再描画完了を検出）
            // 自動スクロールは削除 - ユーザーのスクロール操作を妨げないため
            
            // カスタムキーハンドラー
            term.attachCustomKeyEventHandler((arg) => {
                // Ctrl+C: テキスト選択がある場合はコピー動作を許可
                if (arg.ctrlKey && arg.code === "KeyC" && arg.type === "keydown") {
                    const selection = term.getSelection();
                    if (selection) {
                        // テキスト選択がある場合はブラウザのデフォルトコピー動作を許可
                        // false を返すことで xterm.js の処理をスキップし、
                        // ブラウザのデフォルトのコピー動作を有効にする
                        return false;
                    }
                    // 選択がない場合は通常のCtrl+C（中断）として動作
                }

                return true; // その他のキーは通常通り処理
            });
            
            // xterm.jsのリサイズイベントリスナーを追加
            term.onResize((size) => {
                notifyResize(size.cols, size.rows, 'onResize', '');
            });

            // ResizeObserverを設定
            window.resizeObserverManager.add(sessionId, element, () => {
                // 非表示のターミナルではfit()をスキップ（11x5のような極小サイズになるのを防ぐ）
                if (element.style.display === 'none' || element.offsetParent === null) {
                    return;
                }

                // 要素のサイズが極小の場合もスキップ
                if (element.clientWidth < 100 || element.clientHeight < 100) {
                    return;
                }

                const beforeCols = term.cols, beforeRows = term.rows;
                if (fitAddon) {
                    fitAddon.fit();
                    const afterCols = term.cols, afterRows = term.rows;
                    const changed = (beforeCols !== afterCols || beforeRows !== afterRows);

                    // 極小サイズの場合はC#への通知をスキップ
                    if (afterCols < 20 || afterRows < 10) {
                        return;
                    }

                    // fit()でサイズが変わった場合、onResizeが自動発火してC#に通知される
                    // サイズ変化なしの場合のみここから通知（onResizeは発火しないため）
                    if (!changed) {
                        notifyResize(afterCols, afterRows, 'ResizeObserver',
                            `dom=${element.clientWidth}x${element.clientHeight},noChange`);
                    }
                    // changed時はonResizeが発火するので通知不要
                }
            });
            
            // URL検出機能を追加
            setupUrlDetection(term);

            // ファイルパス検出機能を追加（フォルダ=Explorer/ファイル=既定アプリで開く）
            setupFilePathDetection(term, sessionId);
            setupCommitHashDetection(term, sessionId);
            setupPrNumberDetection(term, sessionId);

            // Unicode11Addonをロード（ブロック文字の幅計算を改善）
            if (typeof Unicode11Addon !== 'undefined' && Unicode11Addon.Unicode11Addon) {
                try {
                    const unicode11Addon = new Unicode11Addon.Unicode11Addon();
                    term.loadAddon(unicode11Addon);
                    term.unicode.activeVersion = '11';
                    console.log('[Unicode11] Unicode11Addon loaded successfully');
                } catch (error) {
                    console.error('[Unicode11] Failed to load Unicode11Addon:', error);
                }
            }

            // WebglAddonをロード（GPU描画。xterm.js 6.0 で廃止された CanvasAddon の後継）
            // WebGLコンテキストは GPU リセットやスリープ復帰、コンテキスト数上限超過で失われることがある。
            // onContextLoss でアドオンを破棄すると xterm.js が自動的に DOM レンダラーへフォールバックする。
            if (typeof WebglAddon !== 'undefined' && WebglAddon.WebglAddon) {
                try {
                    const webglAddon = new WebglAddon.WebglAddon();
                    webglAddon.onContextLoss(() => {
                        console.warn('[WebglAddon] WebGLコンテキスト喪失を検出。アドオンを破棄しDOMレンダラーへフォールバック');
                        try { webglAddon.dispose(); } catch (e) { /* 破棄失敗は無視 */ }
                    });
                    term.loadAddon(webglAddon);
                    console.log('[WebglAddon] WebglAddon loaded successfully');
                } catch (error) {
                    console.error('[WebglAddon] Failed to load WebglAddon:', error);
                }
            }
            
            window.multiSessionTerminals[sessionId] = {
                terminal: term,
                fitAddon: fitAddon,
                // コンテナ要素。cleanupTerminal がタッチリスナー解除と DOM クリアに使う。
                // id から組み立てると命名規則（terminal-{guid}）に依存し、シェルパネルで外れる。
                container: element
            };

            // フロー制御マネージャーにdotNetRefを設定
            if (dotNetRef) {
                window.flowControlManager.setDotNetRef(sessionId, dotNetRef);
            }
            
            // console.log(`[JS] ターミナル作成成功: sessionId=${sessionId}`);
            // console.log(`[JS] 現在のターミナル数: ${Object.keys(window.multiSessionTerminals).length}`);

        } else {
            // DOM要素が見つからない場合はエラーログを出力
            console.error(`[JS] createMultiSessionTerminal: DOM要素が見つかりません terminalId=${terminalId}`);
            return null;
        }

        return {
            write: (data) => {
                const dataLength = data.length;

                // フロー制御: 書き込み前にバイト数を加算
                window.flowControlManager.beforeWrite(sessionId, dataLength);

                // サーバー側の状態バッファが復元順序を保証するため、ANSIシーケンスを
                // 加工せず、そのままxtermへ渡す。
                term.write(data, () => {
                    // 書き込み完了後にフロー制御を更新
                    window.flowControlManager.afterWrite(sessionId, dataLength);
                });
            },
            clear: () => term.clear(),
            focus: () => term.focus(),
            resize: async () => {
                // DOMの表示切替とBlazorの描画が完了した後のレイアウトでfitする。
                // 通常はブラウザーの描画フレームに同期する。バックグラウンドタブでは
                // requestAnimationFrameが停止するため即時に進み、途中で非表示になった
                // 場合にも安全弁で復元処理を止めない。
                await new Promise(resolve => {
                    let completed = false;
                    const finish = () => {
                        if (completed) return;
                        completed = true;
                        clearTimeout(fallbackTimer);
                        resolve();
                    };
                    const fallbackTimer = setTimeout(finish, 250);

                    if (document.visibilityState !== 'visible') {
                        finish();
                        return;
                    }

                    requestAnimationFrame(() => requestAnimationFrame(finish));
                });

                if (fitAddon) {
                    try {
                        fitAddon.fit();
                    } catch (e) {
                        console.log(`[Resize] resize() エラー ${sessionId.substring(0,8)}: ${e.message}`);
                    }
                }
            },
            getSize: () => {
                return { cols: term.cols, rows: term.rows };
            }
            // 破棄は terminalFunctions.cleanupTerminal に一本化する。ここに dispose を生やすと
            // フロー制御・タッチリスナー・DOM クリアを取りこぼした不完全な破棄経路が増える。
        };
    },

    // ターミナル表示制御関数
    hideAllTerminals: function() {
        console.log('[JS] hideAllTerminals: 開始');
        const allTerminals = document.querySelectorAll('[id^="terminal-"]');
        console.log(`[JS] hideAllTerminals: ${allTerminals.length}個のターミナルを非表示に`);
        allTerminals.forEach(terminal => {
            console.log(`[JS] hideAllTerminals: ${terminal.id}を非表示に設定`);
            terminal.style.display = 'none';
        });
        console.log('[JS] hideAllTerminals: 完了');
    },

    // コンテナの表示を戻すだけ。hideAllTerminals が display:none にしたのを対で戻す。
    // xterm 内部DOMの修復（子要素の総当たり再表示・親への再アタッチ）は、Blazor が DOM を
    // 差し替えていた時代の対症療法で、xterm が意図的に隠す内部要素まで触るリスクがあった。
    // 呼び出し元(SelectSession)は直後に xterm を作り直してリプレイするため、
    // ここでの focus/refresh も不要。
    showTerminal: function(sessionId) {
        const terminal = document.getElementById(`terminal-${sessionId}`);
        if (!terminal) {
            console.error(`[JS] showTerminal: ターミナル要素が見つからない terminal-${sessionId}`);
            return;
        }
        terminal.style.display = 'block';
    },
    
    // ターミナルクリーンアップ関数
    cleanupTerminal: function(sessionId) {

        // フロー制御のクリーンアップ
        window.flowControlManager.remove(sessionId);

        // ResizeObserverのクリーンアップ
        window.resizeObserverManager.remove(sessionId);
        
        // コンテナは作成時に保持した参照を使う。id を `terminal-${sessionId}` と組み立てると
        // 通常セッション（コンテナ terminal-{guid}）でしか当たらず、シェルパネル
        // （コンテナ shell-panel-*、キー shell-term-*）ではタッチリスナー解除と
        // DOM クリアが空振りする。旧データ用に getElementById もフォールバックとして残す。
        const terminalInfo = window.multiSessionTerminals && window.multiSessionTerminals[sessionId];
        const terminalDiv = terminalInfo?.container || document.getElementById(`terminal-${sessionId}`);

        // ターミナルインスタンスのクリーンアップ
        if (terminalInfo) {
            if (terminalInfo.terminal) {
                try {
                    terminalInfo.terminal.dispose();
                } catch (e) {
                    // WebglAddonの内部レンダラーが不整合な状態（スリープ復帰後・コンテキスト喪失後等）でも安全に破棄
                    console.log(`[JS] cleanupTerminal: dispose()エラー（無視）: ${e.message}`);
                }
                console.log(`[JS] cleanupTerminal: ターミナル ${sessionId} を破棄`);
            }
            delete window.multiSessionTerminals[sessionId];
            console.log(`[JS] cleanupTerminal: multiSessionTerminalsから削除`);
        }
        else {
            console.log(`[JS] cleanupTerminal: ★★★ 警告: 削除対象のターミナルが存在しない sessionId=${sessionId}`);
        }

        // ターミナルdiv内をクリア - より安全なDOM操作を使用
        if (terminalDiv) {
            // 登録したリスナーを外す（破棄済み term への参照を残さない）
            if (typeof terminalDiv._touchScrollDetach === 'function') {
                terminalDiv._touchScrollDetach();
            }
            if (typeof terminalDiv._selectionMenuDetach === 'function') {
                terminalDiv._selectionMenuDetach();
            }
            while (terminalDiv.firstChild) {
                terminalDiv.removeChild(terminalDiv.firstChild);
            }
            // console.log(`[JS] ターミナルdiv terminal-${sessionId} をクリア`);
        }
    },

    // ターミナル破棄関数
    destroyTerminal: function(sessionId) {
        this.cleanupTerminal(sessionId);
    },

    // ターミナルをリフレッシュ（バッファ復元後の表示更新用）
    refreshTerminal: function(sessionId) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;

            if (term) {
                try {
                    // サイズ変更は行わず、全行の再描画だけを実行する。
                    term.refresh(0, term.rows - 1);

                    console.log(`[JS] refreshTerminal: リフレッシュ完了 sessionId=${sessionId}, cols=${term.cols}, rows=${term.rows}`);
                } catch (e) {
                    console.log(`[JS] refreshTerminal: エラー ${e.message}`);
                }
            }
        }
    },

    // 全ターミナルのフォントサイズを一括更新
    updateAllTerminalFontSizes: function(fontSize) {
        if (!window.multiSessionTerminals) return;

        const isMobile = window.innerWidth <= 768;
        const actualSize = isMobile ? Math.round(fontSize * 2 / 3) : fontSize;

        for (const sessionId of Object.keys(window.multiSessionTerminals)) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            if (terminalInfo && terminalInfo.terminal) {
                terminalInfo.terminal.options.fontSize = actualSize;

                // 表示中のターミナルのみfit()を実行（非表示はResizeObserverに委譲）
                const element = terminalInfo.terminal.element;
                if (element && element.offsetParent !== null && terminalInfo.fitAddon) {
                    try {
                        terminalInfo.fitAddon.fit();
                    } catch (e) {
                        console.log(`[JS] updateAllTerminalFontSizes: fit()エラー sessionId=${sessionId}: ${e.message}`);
                    }
                }
            }
        }
        console.log(`[JS] updateAllTerminalFontSizes: fontSize=${fontSize}, actualSize=${actualSize}`);
    }
};
