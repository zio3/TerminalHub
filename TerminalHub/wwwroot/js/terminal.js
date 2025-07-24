// ResizeObserver管理クラス
class ResizeObserverManager {
    constructor() {
        this.observers = new Map();
    }

    add(sessionId, element, callback) {
        // 既存のObserverがあれば削除
        this.remove(sessionId);
        
        const observer = new ResizeObserver(callback);
        observer.observe(element);
        this.observers.set(sessionId, observer);
        // console.log(`[ResizeObserverManager] Observer追加: sessionId=${sessionId}`);
    }

    remove(sessionId) {
        if (this.observers.has(sessionId)) {
            const observer = this.observers.get(sessionId);
            observer.disconnect();
            this.observers.delete(sessionId);
            // console.log(`[ResizeObserverManager] Observer削除: sessionId=${sessionId}`);
        }
    }

    removeAll() {
        this.observers.forEach((observer, sessionId) => {
            observer.disconnect();
            // console.log(`[ResizeObserverManager] Observer削除: sessionId=${sessionId}`);
        });
        this.observers.clear();
    }

    has(sessionId) {
        return this.observers.has(sessionId);
    }
}

// グローバルインスタンス
window.resizeObserverManager = new ResizeObserverManager();

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
        // console.log(`[JS] createMultiSessionTerminal開始: terminalId=${terminalId}, sessionId=${sessionId}`);
        
        // 初期化
        if (!window.multiSessionTerminals) {
            window.multiSessionTerminals = {};
        }
        
        // 既存のターミナルがあれば警告
        if (window.multiSessionTerminals[sessionId]) {
            console.warn(`[JS] 警告: セッション ${sessionId} のターミナルは既に存在します！`);
        }
        
        const Terminal = window.Terminal;
        const FitAddon = window.FitAddon.FitAddon;
        
        const term = new Terminal({
            cursorBlink: true,
            fontSize: 14,
            fontFamily: 'Consolas, monospace',
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
            windowsMode: true  // Windows環境用の設定
        });
        
        // FitAddonを使う
        const fitAddon = new FitAddon();
        term.loadAddon(fitAddon);
        
        const element = document.getElementById(terminalId);
        if (element) {
            term.open(element);
            
            // requestAnimationFrameを使用してDOMの準備を確実に待つ
            requestAnimationFrame(() => {
                fitAddon.fit();
                console.log(`[JS] ターミナルフィット実行: cols=${term.cols}, rows=${term.rows}`);
                
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
                const terminalInfo = window.multiSessionTerminals[sessionId];
                if (!terminalInfo) return;
                
                // 既存のタイマーをクリア
                if (terminalInfo.resizeScrollTimer) {
                    clearTimeout(terminalInfo.resizeScrollTimer);
                }
                
                // スクロール処理の定義
                const performScroll = () => {
                    // 書き込み中でない場合は即座にスクロール
                    if (!terminalInfo.isWriting) {
                        term.scrollToBottom();
                        console.log(`[JS] リサイズ後即座にスクロール: sessionId=${sessionId}`);
                        
                        // 念のため少し後にもう一度
                        setTimeout(() => {
                            term.scrollToBottom();
                        }, 100);
                    } else {
                        // 書き込み中の場合はフラグを設定
                        terminalInfo.pendingScrollAfterWrite = true;
                        console.log(`[JS] 書き込み中のためスクロール遅延: sessionId=${sessionId}`);
                        
                        // 500ms後に強制スクロール（フェイルセーフ）
                        terminalInfo.resizeScrollTimer = setTimeout(() => {
                            term.scrollToBottom();
                            console.log(`[JS] リサイズ後の強制スクロール: sessionId=${sessionId}`);
                            terminalInfo.pendingScrollAfterWrite = false;
                        }, 500);
                    }
                };
                
                // 次のフレームで実行
                requestAnimationFrame(performScroll);
            });
            
            // ResizeObserverを設定
            window.resizeObserverManager.add(sessionId, element, () => {
                if (fitAddon) {
                    fitAddon.fit();
                    console.log(`[JS] リサイズ: sessionId=${sessionId}, cols=${term.cols}, rows=${term.rows}`);
                    
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, term.cols, term.rows);
                    }
                    
                    // リサイズ完了後のスクロール処理
                    const terminalInfo = window.multiSessionTerminals[sessionId];
                    if (!terminalInfo) {
                        // 初回作成時のため、シンプルなスクロール処理
                        setTimeout(() => {
                            term.scrollToBottom();
                            console.log(`[JS] ResizeObserver初回スクロール: sessionId=${sessionId}`);
                        }, 100);
                        return;
                    }
                    
                    // 既存のタイマーをクリア
                    if (terminalInfo.resizeScrollTimer) {
                        clearTimeout(terminalInfo.resizeScrollTimer);
                    }
                    
                    const performScrollAfterObserver = () => {
                        // 書き込み中でない場合は即座にスクロール
                        if (!terminalInfo.isWriting) {
                            term.scrollToBottom();
                            // 確実性のため少し後にもう一度
                            setTimeout(() => {
                                term.scrollToBottom();
                                console.log(`[JS] ResizeObserver後スクロール実行: sessionId=${sessionId}`);
                            }, 100);
                        } else {
                            // 書き込み中の場合はフラグを設定
                            terminalInfo.pendingScrollAfterWrite = true;
                            console.log(`[JS] ResizeObserver: 書き込み中のためスクロール遅延`);
                            
                            // 500ms後に強制スクロール
                            terminalInfo.resizeScrollTimer = setTimeout(() => {
                                term.scrollToBottom();
                                console.log(`[JS] ResizeObserver後の強制スクロール`);
                                terminalInfo.pendingScrollAfterWrite = false;
                            }, 500);
                        }
                    };
                    
                    requestAnimationFrame(performScrollAfterObserver);
                }
            });
            
            window.multiSessionTerminals[sessionId] = {
                terminal: term,
                fitAddon: fitAddon,
                scrollPosition: 0,
                hasBufferedContent: false,
                lastWriteTime: 0,
                isWriting: false,
                pendingScrollAfterWrite: false,
                resizeScrollTimer: null
            };
            
            // console.log(`[JS] ターミナル作成成功: sessionId=${sessionId}`);
            // console.log(`[JS] 現在のターミナル数: ${Object.keys(window.multiSessionTerminals).length}`);
        }
        
        return {
            write: (data) => {
                const terminalInfo = window.multiSessionTerminals[sessionId];
                if (terminalInfo) {
                    // 書き込み開始を記録
                    terminalInfo.lastWriteTime = Date.now();
                    terminalInfo.isWriting = true;
                    
                    term.write(data, () => {
                        // 書き込み完了を記録
                        terminalInfo.isWriting = false;
                        terminalInfo.lastWriteTime = Date.now();
                        
                        // リサイズ中のスクロール待機があれば実行
                        if (terminalInfo.pendingScrollAfterWrite) {
                            setTimeout(() => {
                                term.scrollToBottom();
                                console.log(`[JS] 書き込み完了後の遅延スクロール実行: sessionId=${sessionId}`);
                                terminalInfo.pendingScrollAfterWrite = false;
                            }, 50);
                        }
                    });
                } else {
                    term.write(data);
                }
            },
            clear: () => term.clear(),
            focus: () => term.focus(),
            resize: () => {
                if (fitAddon) {
                    fitAddon.fit();
                    // 手動リサイズ時はxterm.onResizeイベントで自動的にスクロールされる
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
                    scrollTop: term.buffer.active.baseY
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
        const allTerminals = document.querySelectorAll('[id^="terminal-"]');
        allTerminals.forEach(terminal => {
            terminal.style.display = 'none';
        });
    },

    showTerminal: function(sessionId) {
        const terminal = document.getElementById(`terminal-${sessionId}`);
        if (terminal) {
            terminal.style.display = 'block';
        }
    },

    terminalExists: function(sessionId) {
        return document.getElementById(`terminal-${sessionId}`) !== null;
    },

    // ターミナルクリーンアップ関数
    cleanupTerminal: function(sessionId) {
        // ResizeObserverのクリーンアップ
        window.resizeObserverManager.remove(sessionId);
        
        // ターミナルインスタンスのクリーンアップ
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            
            // タイマーのクリーンアップ
            if (terminalInfo.resizeScrollTimer) {
                clearTimeout(terminalInfo.resizeScrollTimer);
            }
            
            if (terminalInfo.terminal) {
                terminalInfo.terminal.dispose();
                // console.log(`[JS] ターミナル ${sessionId} を破棄`);
            }
            delete window.multiSessionTerminals[sessionId];
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
                // 複数回実行して確実にスクロール
                terminal.scrollToBottom();
                setTimeout(() => {
                    terminal.scrollToBottom();
                }, 50);
                setTimeout(() => {
                    terminal.scrollToBottom();
                }, 100);
                console.log(`[JS] ターミナル ${sessionId} を最下段にスクロール`);
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
            
            // バッファ内容の場合は即座に最下部へスクロール
            setTimeout(() => {
                term.scrollToBottom();
                console.log(`[JS] バッファ内容書き込み後スクロール: sessionId=${sessionId}`);
            }, 50);
            
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

