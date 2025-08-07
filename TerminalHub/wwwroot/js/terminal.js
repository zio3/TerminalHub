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

// URL検出の設定
function setupUrlDetection(term) {
    // HTTP/HTTPSのURLパターン
    const urlRegex = /https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)/g;
    
    // デバッグ: APIの存在確認
    console.log('[URL Detection] Available APIs:', {
        registerLinkProvider: typeof term.registerLinkProvider,
        registerLinkMatcher: typeof term.registerLinkMatcher,
        registerDecoration: typeof term.registerDecoration
    });
    
    // xterm.js v5用のregisterLinkProvider実装
    if (typeof term.registerLinkProvider === 'function') {
        const linkProvider = {
            provideLinks: (bufferLineNumber, callback) => {
                const line = term.buffer.active.getLine(bufferLineNumber);
                if (!line) {
                    callback(undefined);
                    return;
                }
                
                // 行のテキストを取得
                let lineText = '';
                for (let i = 0; i < line.length; i++) {
                    const cell = line.getCell(i);
                    if (cell) {
                        lineText += cell.getChars() || ' ';
                    }
                }
                
                // URLを検出
                const links = [];
                let match;
                urlRegex.lastIndex = 0; // 正規表現をリセット
                
                while ((match = urlRegex.exec(lineText)) !== null) {
                    const link = {
                        range: {
                            start: { x: match.index + 1, y: bufferLineNumber + 1 },
                            end: { x: match.index + match[0].length + 1, y: bufferLineNumber + 1 }
                        },
                        text: match[0],
                        activate: (e, uri) => {
                            console.log('[URL Detection] Link activated:', uri);
                            window.open(uri, '_blank');
                        }
                    };
                    links.push(link);
                    console.log('[URL Detection] Found link:', match[0], 'at line', bufferLineNumber + 1);
                }
                
                callback(links);
            }
        };
        
        term.registerLinkProvider(linkProvider);
        console.log('[URL Detection] URL検出機能を有効化しました（registerLinkProvider使用）');
    } else {
        console.error('[URL Detection] registerLinkProvider APIが利用できません');
    }
}

//// IME検出とフォーカス制御
//function setupIMEDetection(term, element, sessionId) {
//    console.log(`[IME Detection] セットアップ開始: sessionId=${sessionId}`);
    
//    // xterm.jsのテキストエリアを取得
//    const textareas = element.querySelectorAll('.xterm-helper-textarea');
//    if (textareas.length === 0) {
//        console.log('[IME Detection] helper-textareaが見つかりません');
//        return;
//    }
    
//    const helperTextarea = textareas[0];
    
//    // composition開始イベントをリッスン
//    helperTextarea.addEventListener('compositionstart', (e) => {
//        console.log(`[IME Detection] IME開始検出: sessionId=${sessionId}`);
        
//        // 下部のテキストエリアを探してフォーカス
//        const inputTextarea = document.querySelector('textarea#inputText');
//        if (inputTextarea) {
//            console.log('[IME Detection] テキストエリアにフォーカスを移動');
//            inputTextarea.focus();
            
//            // 既存の入力があれば、それを保持
//            const existingText = inputTextarea.value;
//            if (existingText) {
//                // カーソルを最後に移動
//                inputTextarea.setSelectionRange(existingText.length, existingText.length);
//            }
//        } else {
//            console.log('[IME Detection] 入力用テキストエリアが見つかりません');
//        }
//    });
    
//    // キーダウンイベントでもIMEを検出（keyCode 229）
//    helperTextarea.addEventListener('keydown', (e) => {
//        if (e.keyCode === 229) {
//            console.log(`[IME Detection] IME keyCode 229検出: sessionId=${sessionId}`);
            
//            const inputTextarea = document.querySelector('textarea#inputText');
//            if (inputTextarea && document.activeElement !== inputTextarea) {
//                console.log('[IME Detection] テキストエリアにフォーカスを移動 (keyCode 229)');
//                inputTextarea.focus();
//            }
//        }
//    });
//}

// 右クリック検出とIMEスタイル制御
function setupContextMenuAndIME(element, sessionId) {
    let imeStyleSheet = null;
    
    // IME用のスタイルシートを作成
    function createIMEStyle() {
        if (!imeStyleSheet) {
            imeStyleSheet = document.createElement('style');
            imeStyleSheet.textContent = `
                .xterm-helper-textarea {
                    left: 0 !important;
                }
                .xterm .composition-view {
                    left: 0 !important;
                }
            `;
            imeStyleSheet.id = 'ime-positioning-style';
        }
    }
    
    // スタイルを適用
    function enableIMEStyle() {
        createIMEStyle();
        if (!document.getElementById('ime-positioning-style')) {
            document.head.appendChild(imeStyleSheet);
        }
    }
    
    // スタイルを削除
    function disableIMEStyle() {
        const style = document.getElementById('ime-positioning-style');
        if (style) {
            style.remove();
        }
    }
    
    // 初期状態でIMEスタイルを有効化
    enableIMEStyle();
    
    // 右クリックイベントを検出
    element.addEventListener('contextmenu', (e) => {
        console.log(`[IME] 右クリック検出 - IMEスタイル無効化`);
        disableIMEStyle();
        
        // 一定時間後に再度有効化
        setTimeout(() => {
            console.log(`[IME] IMEスタイル再有効化`);
            enableIMEStyle();
        }, 1000);
    });
    
    // クリックでIMEスタイルを再有効化
    element.addEventListener('click', () => {
        enableIMEStyle();
    });
}

window.terminalFunctions = {
    // マルチセッション用のターミナル作成関数
    createMultiSessionTerminal: function(terminalId, sessionId, dotNetRef) {
        console.log(`[JS] createMultiSessionTerminal開始: terminalId=${terminalId}, sessionId=${sessionId}`);
        
        // 初期化
        if (!window.multiSessionTerminals) {
            window.multiSessionTerminals = {};
            console.log(`[JS] createMultiSessionTerminal: multiSessionTerminals初期化`);
        }
        
        // 既存のターミナルがあれば警告
        if (window.multiSessionTerminals[sessionId]) {
            console.warn(`[JS] ★★★ 警告: セッション ${sessionId} のターミナルは既に存在します！`);
            const existing = window.multiSessionTerminals[sessionId];
            console.log(`[JS] 既存ターミナル状態: term=${!!existing.term}, disposed=${existing.disposed}`);
        }
        
        const Terminal = window.Terminal;
        const FitAddon = window.FitAddon.FitAddon;
        
        const term = new Terminal({
            cursorBlink: true,
            fontSize: 14,
            fontFamily: 'Consolas, monospace',
            scrollback: 10000,
            scrollOnInput: true,
            scrollOnOutput: true,
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
            windowsMode: true,  // Windows環境用の設定
            allowProposedApi: true  // 提案中のAPIを許可（スクロールバック保持のため）
        });
        
        // FitAddonを使う
        const fitAddon = new FitAddon();
        term.loadAddon(fitAddon);
        
        const element = document.getElementById(terminalId);
        if (element) {
            // 既存の内容をクリア（重要！）
            element.innerHTML = '';
            
            term.open(element);
            
            // requestAnimationFrameを使用してDOMの準備を確実に待つ
            requestAnimationFrame(() => {
                fitAddon.fit();
                // ターミナルフィット実行
                
                // フィット後のサイズを通知
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, term.cols, term.rows);
                }
            });
            
            // 直接入力モードのハンドラー
            term.onData((data) => {
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('SendInput', sessionId, data);
                }
            });
            
            // レンダリング後のイベント（再描画完了を検出）
            // 自動スクロールは削除 - ユーザーのスクロール操作を妨げないため
            
            // カスタムキーハンドラー（Ctrl+Cをコピー用に使う）
            term.attachCustomKeyEventHandler((arg) => {
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
            
            // 右クリック検出とIMEスタイル制御
            setupContextMenuAndIME(element, sessionId);
            
            // IME検出とフォーカス制御
         //   setupIMEDetection(term, element, sessionId);
            
            // xterm.jsのリサイズイベントリスナーを追加
            term.onResize((size) => {
                console.log(`[JS] onResize fired for ${sessionId}: ${size.cols}x${size.rows}`);
                
                // リサイズイベントをトラック
                const terminalInfo = window.multiSessionTerminals[sessionId];
                if (terminalInfo) {
                    // ターミナルリサイズ検出
                    // リサイズトリックが適用された場合のフラグ設定
                    if (terminalInfo.isFirstWrite) {
                        terminalInfo.resizeCount = 1;
                        // リサイズトリック検出
                    }
                }
                
                // C#側にサイズ変更を通知
                if (dotNetRef) {
                    console.log(`[JS] Calling OnTerminalSizeChanged for ${sessionId}`);
                    dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, size.cols, size.rows);
                } else {
                    console.log(`[JS] dotNetRef is null for ${sessionId}, cannot notify resize`);
                }
                
                // xtermの自動スクロール機能に任せる（scrollOnOutput: trueが設定済み）
            });
            
            // ResizeObserverを設定
            window.resizeObserverManager.add(sessionId, element, () => {
                console.log(`[JS] ResizeObserver fired for ${sessionId}`);
                if (fitAddon) {
                    fitAddon.fit();
                    // リサイズ処理
                    console.log(`[JS] After fit: ${term.cols}x${term.rows}`);
                    
                    if (dotNetRef) {
                        console.log(`[JS] ResizeObserver calling OnTerminalSizeChanged for ${sessionId}`);
                        dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, term.cols, term.rows);
                    } else {
                        console.log(`[JS] ResizeObserver: dotNetRef is null for ${sessionId}`);
                    }
                    
                    // xtermの自動スクロール機能に任せる（scrollOnOutput: trueが設定済み）
                }
            });
            
            // URL検出機能を追加
            setupUrlDetection(term);
            
            // スクロール関数をオーバーライドしてログを追加
            const originalScrollToBottom = term.scrollToBottom.bind(term);
            const originalScrollToTop = term.scrollToTop.bind(term);
            const originalScrollToLine = term.scrollToLine.bind(term);
            
            term.scrollToBottom = function() {
                return originalScrollToBottom();
            };
            
            term.scrollToTop = function() {
                return originalScrollToTop();
            };
            
            term.scrollToLine = function(line) {
                return originalScrollToLine(line);
            };
            
            // スクロール設定の状態を確認
            
            window.multiSessionTerminals[sessionId] = {
                terminal: term,
                fitAddon: fitAddon,
                scrollPosition: 0,
                hasBufferedContent: false
            };
            
            // console.log(`[JS] ターミナル作成成功: sessionId=${sessionId}`);
            // console.log(`[JS] 現在のターミナル数: ${Object.keys(window.multiSessionTerminals).length}`);
        }
        
        // ターミナルごとの状態を保存
        window.multiSessionTerminals[sessionId].isFirstWrite = true;
        window.multiSessionTerminals[sessionId].resizeCount = 0;
        
        return {
            write: (data) => {
                const terminalInfo = window.multiSessionTerminals[sessionId];
                
                // terminalInfoが存在しない場合は単純にデータを書き込み
                if (!terminalInfo) {
                    term.write(data);
                    return;
                }
                
                // リサイズ直後の書き込みはカウント
                if (terminalInfo.resizeCount > 0 && terminalInfo.resizeCount < 3) {
                    terminalInfo.resizeCount++;
                    // リサイズ後の書き込み
                    // リサイズデータは通常通り処理
                    term.write(data);
                    
                    if (terminalInfo.resizeCount >= 2) {
                        // リサイズ完了 - スクロールバック復元完了
                        // リサイズ完了 - スクロールバック復元
                        terminalInfo.resizeCount = 0;
                        
                    }
                }
                // セッション切り替え直後の最初の書き込みを検出（リサイズトリック前）
                else if (terminalInfo.isFirstWrite && data.includes('\x1b[2J') && (data.match(/\x1b\[K/g) || []).length > 10) {
                    // 画面クリアをスキップして、カーソル位置のみ適用
                    let processedData = data;
                    
                    // 画面クリア(\x1b[2J)を削除
                    processedData = processedData.replace(/\x1b\[2J/g, '');
                    // ホーム位置への移動(\x1b[H)も一時的に削除
                    processedData = processedData.replace(/\x1b\[H/g, '');
                    
                    // 改行を追加して続きから表示
                    term.write('\r\n--- セッション再開 ---\r\n');
                    term.write(processedData);
                    
                    // セッション切り替え検出 - スクロールバック保持
                    terminalInfo.isFirstWrite = false;
                    
                    // リサイズトリックを待つ
                    terminalInfo.resizeCount = 1;
                } else {
                    // 通常の書き込み
                    const beforeViewportY = term.buffer.active.viewportY;
                    const beforeLength = term.buffer.active.length;
                    const isAtBottom = beforeViewportY + term.rows >= beforeLength;
                    
                    term.write(data);
                    
                    if (terminalInfo) {
                        terminalInfo.isFirstWrite = false;
                    }
                }
            },
            clear: () => term.clear(),
            focus: () => term.focus(),
            resize: () => {
                console.log(`[JS] resize: 開始 sessionId=${sessionId}`);
                if (fitAddon) {
                    try {
                        fitAddon.fit();
                        console.log(`[JS] resize: fitAddon.fit()実行完了 cols=${term.cols}, rows=${term.rows}`);
                        // 手動リサイズ時はxterm.onResizeイベントで自動的にスクロールされる
                    } catch (e) {
                        console.log(`[JS] resize: エラー ${e.message}`);
                    }
                } else {
                    console.log(`[JS] resize: ★★★ fitAddonが存在しない`);
                }
            },
            getSize: () => {
                return { cols: term.cols, rows: term.rows };
            },
            scrollToBottom: () => {
                term.scrollToBottom();
            },
            scrollToTop: () => {
                term.scrollToTop();
            },
            getScrollPosition: () => {
                return {
                    scrollY: term.buffer.active.viewportY,
                    scrollTop: term.buffer.active.baseY,
                    length: term.buffer.active.length,
                    cursorY: term.buffer.active.cursorY
                };
            },
            dispose: () => {
                window.resizeObserverManager.remove(sessionId);
                term.dispose();
            }
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

    showTerminal: function(sessionId) {
        console.log(`[JS] showTerminal: 開始 sessionId=${sessionId}`);
        const terminal = document.getElementById(`terminal-${sessionId}`);
        if (terminal) {
            console.log(`[JS] showTerminal: ターミナル要素が見つかりました`);
            console.log(`[JS] showTerminal: 設定前 display=${terminal.style.display}, visibility=${terminal.style.visibility}, opacity=${terminal.style.opacity}`);
            terminal.style.display = 'block';
            console.log(`[JS] showTerminal: ターミナルを表示に設定`);
            console.log(`[JS] showTerminal: 設定後 display=${terminal.style.display}, offsetWidth=${terminal.offsetWidth}, offsetHeight=${terminal.offsetHeight}`);
            
            // セッション表示時に初回書き込みフラグをリセット
            if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
                window.multiSessionTerminals[sessionId].isFirstWrite = true;
                console.log(`[JS] showTerminal: 初回書き込みフラグをリセット`);
                
                // xtermオブジェクトの存在確認
                const termObj = window.multiSessionTerminals[sessionId];
                console.log(`[JS] showTerminal: xterm存在確認 - terminal=${!!termObj.terminal}, element=${!!termObj.terminal?.element}`);
                if (termObj.terminal && termObj.terminal.element) {
                    console.log(`[JS] showTerminal: xterm.element - display=${termObj.terminal.element.style.display}, 親要素=${!!termObj.terminal.element.parentElement}`);
                    
                    // xterm要素を強制的に表示
                    if (termObj.terminal.element.style.display === 'none' || !termObj.terminal.element.style.display) {
                        console.log(`[JS] showTerminal: xterm要素を強制表示`);
                        termObj.terminal.element.style.display = 'block';
                    }
                    
                    // 子要素も確認
                    const children = termObj.terminal.element.children;
                    console.log(`[JS] showTerminal: xterm子要素数=${children.length}`);
                    for (let i = 0; i < children.length; i++) {
                        if (children[i].style.display === 'none') {
                            console.log(`[JS] showTerminal: 子要素${i}を表示に変更`);
                            children[i].style.display = '';
                        }
                    }
                    
                    // 強制的にフォーカスとリフレッシュ
                    try {
                        // xterm要素が正しい親要素にあるか確認
                        if (termObj.terminal.element.parentNode !== terminal) {
                            console.log(`[JS] showTerminal: ★★★ 警告: xterm要素が正しい親要素にありません！`);
                            console.log(`[JS] showTerminal: 修復: xterm要素を再アタッチ`);
                            terminal.appendChild(termObj.terminal.element);
                        }
                        
                        termObj.terminal.focus();
                        termObj.terminal.refresh(0, termObj.terminal.rows - 1);
                        console.log(`[JS] showTerminal: フォーカスとリフレッシュ実行`);
                    } catch (e) {
                        console.log(`[JS] showTerminal: フォーカス/リフレッシュエラー: ${e.message}`);
                    }
                }
            } else {
                console.log(`[JS] showTerminal: ★★★ 警告: multiSessionTerminalsが見つからない sessionId=${sessionId}`);
                console.log(`[JS] showTerminal: デバッグ - window.multiSessionTerminals=${!!window.multiSessionTerminals}`);
                if (window.multiSessionTerminals) {
                    console.log(`[JS] showTerminal: 利用可能なセッション: ${Object.keys(window.multiSessionTerminals).join(', ')}`);
                }
            }
            console.log(`[JS] showTerminal: 完了`);
        } else {
            console.log(`[JS] showTerminal: ★★★ エラー: ターミナル要素が見つからない terminal-${sessionId}`);
        }
    },
    
    // ターミナルを一時的に非表示にして、データ受信後に表示
    showTerminalWithDelay: function(sessionId, delayMs = 100) {
        const terminal = document.getElementById(`terminal-${sessionId}`);
        if (terminal) {
            // まず非表示にする（opacity使用でスムーズに）
            terminal.style.transition = 'opacity 0.2s ease-in-out';
            terminal.style.opacity = '0';
            terminal.style.display = 'block';
            
            // セッション表示時に初回書き込みフラグをリセット
            if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
                const terminalInfo = window.multiSessionTerminals[sessionId];
                terminalInfo.isFirstWrite = true;
                terminalInfo.pendingShow = true;
                // セッションを一時非表示に設定
                
                // 指定時間後にフェードイン
                setTimeout(() => {
                    terminal.style.opacity = '1';
                    terminalInfo.pendingShow = false;
                    // セッションをフェードイン表示
                }, delayMs);
            }
        }
    },

    terminalExists: function(sessionId) {
        return document.getElementById(`terminal-${sessionId}`) !== null;
    },

    // ターミナルクリーンアップ関数
    cleanupTerminal: function(sessionId) {
        console.log(`[JS] cleanupTerminal: ★★★ 開始 sessionId=${sessionId}`);
        console.trace(`[JS] cleanupTerminal: 呼び出し元`);
        
        // 削除前の状態を記録
        if (window.multiSessionTerminals) {
            console.log(`[JS] cleanupTerminal: 削除前のターミナル数=${Object.keys(window.multiSessionTerminals).length}`);
            console.log(`[JS] cleanupTerminal: 削除前のセッションID一覧: ${Object.keys(window.multiSessionTerminals).join(', ')}`);
        }
        
        // ResizeObserverのクリーンアップ
        window.resizeObserverManager.remove(sessionId);
        
        // ターミナルインスタンスのクリーンアップ
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            if (window.multiSessionTerminals[sessionId].terminal) {
                window.multiSessionTerminals[sessionId].terminal.dispose();
                console.log(`[JS] cleanupTerminal: ターミナル ${sessionId} を破棄`);
            }
            delete window.multiSessionTerminals[sessionId];
            console.log(`[JS] cleanupTerminal: multiSessionTerminalsから削除`);
        }
        else {
            console.log(`[JS] cleanupTerminal: ★★★ 警告: 削除対象のターミナルが存在しない sessionId=${sessionId}`);
        }
        
        // ターミナルdiv内をクリア
        const terminalDiv = document.getElementById(`terminal-${sessionId}`);
        if (terminalDiv) {
            terminalDiv.innerHTML = '';
            // console.log(`[JS] ターミナルdiv terminal-${sessionId} をクリア`);
        }
    },

    // ターミナル破棄関数
    destroyTerminal: function(sessionId) {
        this.cleanupTerminal(sessionId);
    },

    // ターミナル再作成用の表示設定
    ensureTerminalVisible: function(sessionId) {
        const terminalDiv = document.getElementById(`terminal-${sessionId}`);
        if (terminalDiv) {
            terminalDiv.style.display = 'block';
            // console.log('[RecreateTerminal] ターミナルdiv表示設定');
        }
    },

    // ターミナルを最下段にスクロール
    scrollToBottom: function(sessionId) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminal = window.multiSessionTerminals[sessionId].terminal;
            if (terminal) {
                // xtermのscrollToBottom機能を1回だけ実行
                terminal.scrollToBottom();
            }
        }
    },

    // バッファ内容を書き込む専用関数（スクロール処理含む）
    writeBuffered: function(sessionId, data) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;
            
            // バッファ内容を書き込み
            term.write(data);
            
            // xtermの自動スクロール機能に任せる（scrollOnOutput: trueが設定済み）
            
            terminalInfo.hasBufferedContent = true;
        }
    },

    // スクロール位置を保存
    saveScrollPosition: function(sessionId) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;
            terminalInfo.scrollPosition = term.buffer.active.viewportY;
        }
    },

    // スクロール位置を復元
    restoreScrollPosition: function(sessionId) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;
            if (terminalInfo.scrollPosition > 0) {
                term.scrollToLine(terminalInfo.scrollPosition);
            }
        }
    }
};

// terminalHubHelpers オブジェクト
window.terminalHubHelpers = {
    // テキストエリアにフォーカス
    focusTextArea: function() {
        const textArea = document.querySelector('textarea[data-input-area]');
        if (textArea) {
            textArea.focus();
        }
    },
    
    // エレメントの存在確認
    checkElementExists: function(elementId) {
        return document.getElementById(elementId) !== null;
    }
};

