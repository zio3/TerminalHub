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
            
            // xterm.jsのリサイズイベントリスナーを追加（デバウンス付き）
            let resizeTimeout = null;
            term.onResize((size) => {
                console.log(`[JS] xterm.onResize: sessionId=${sessionId}, cols=${size.cols}, rows=${size.rows}`);
                
                // 既存のタイマーをクリア
                if (resizeTimeout) {
                    clearTimeout(resizeTimeout);
                }
                
                // デバウンス: 300ms後に実行
                resizeTimeout = setTimeout(() => {
                    console.log(`[JS] リサイズデバウンス完了: sessionId=${sessionId}`);
                    requestAnimationFrame(() => {
                        window.terminalFunctions.scrollToBottomReliably(sessionId);
                    });
                }, 300);
            });
            
            // ResizeObserverを設定（デバウンス付き）
            let observerTimeout = null;
            window.resizeObserverManager.add(sessionId, element, () => {
                // 既存のタイマーをクリア
                if (observerTimeout) {
                    clearTimeout(observerTimeout);
                }
                
                // デバウンス: 200ms後に実行
                observerTimeout = setTimeout(() => {
                    if (fitAddon) {
                        fitAddon.fit();
                        console.log(`[JS] ResizeObserverデバウンス完了: sessionId=${sessionId}, cols=${term.cols}, rows=${term.rows}`);
                        
                        if (dotNetRef) {
                            dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, term.cols, term.rows);
                        }
                        
                        // デバウンス完了後に確実なスクロールを実行
                        setTimeout(() => {
                            window.terminalFunctions.scrollToBottomReliably(sessionId);
                            console.log(`[JS] ResizeObserver後確実なスクロール: sessionId=${sessionId}`);
                        }, 50);
                    }
                }, 200);
            });
            
            window.multiSessionTerminals[sessionId] = {
                terminal: term,
                fitAddon: fitAddon,
                scrollPosition: 0,
                hasBufferedContent: false
            };
            
            // console.log(`[JS] ターミナル作成成功: sessionId=${sessionId}`);
            // console.log(`[JS] 現在のターミナル数: ${Object.keys(window.multiSessionTerminals).length}`);
        }
        
        return {
            write: (data) => term.write(data),
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
            
            // ターミナル表示時に確実なスクロールを実行
            setTimeout(() => {
                window.terminalFunctions.scrollToBottomReliably(sessionId);
                console.log(`[JS] ターミナル表示時の確実なスクロール: sessionId=${sessionId}`);
            }, 100);
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
            if (window.multiSessionTerminals[sessionId].terminal) {
                window.multiSessionTerminals[sessionId].terminal.dispose();
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

    // バッファ内容を書き込む専用関数（画面高さの2倍に制限、スクロール処理含む）
    writeBuffered: function(sessionId, data) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;
            
            // データを行単位で分割
            const lines = data.split('\n');
            const terminalRows = term.rows;
            const maxLines = terminalRows * 2;
            
            let processedData = data;
            
            // 行数が画面高さの2倍を超える場合は最新データのみ使用
            if (lines.length > maxLines) {
                const truncatedLines = lines.slice(-maxLines);
                processedData = truncatedLines.join('\n');
                console.log(`[JS] バッファ最適化: ${lines.length}行 → ${truncatedLines.length}行 (画面${terminalRows}行の2倍制限)`);
            }
            
            // 最適化されたバッファ内容を書き込み
            term.write(processedData);
            
            // バッファ内容の場合は確実なスクロールを実行
            if (terminalInfo.hasBufferedContent) {
                // 既にバッファ内容がある場合は通常のスクロール
                setTimeout(() => {
                    term.scrollToBottom();
                }, 50);
            } else {
                // 初回バッファ内容の場合は確実なスクロール
                setTimeout(() => {
                    window.terminalFunctions.scrollToBottomReliably(sessionId);
                    console.log(`[JS] バッファ内容書き込み後の確実なスクロール: sessionId=${sessionId}`);
                }, 100);
            }
            
            terminalInfo.hasBufferedContent = true;
        }
    },

    // バッファ内容を画面高さの2倍に制限して書き込む
    writeBufferedOptimized: function(sessionId, dataArray) {
        if (!window.multiSessionTerminals || !window.multiSessionTerminals[sessionId]) {
            return;
        }
        
        const terminalInfo = window.multiSessionTerminals[sessionId];
        const term = terminalInfo.terminal;
        const terminalRows = term.rows;
        
        // 画面高さの2倍分のデータのみを取得
        const maxLines = terminalRows * 2;
        let processedData = dataArray;
        
        if (dataArray.length > maxLines) {
            // 最新のmaxLines分のデータのみを使用
            processedData = dataArray.slice(-maxLines);
            console.log(`[JS] バッファ最適化: ${dataArray.length}行 → ${processedData.length}行 (画面${terminalRows}行の2倍)`);
        }
        
        // 最適化されたデータを書き込み
        const combinedData = processedData.join('');
        term.write(combinedData);
        
        // 確実なスクロール
        setTimeout(() => {
            window.terminalFunctions.scrollToBottomReliably(sessionId);
        }, 50);
        
        terminalInfo.hasBufferedContent = true;
    },
    
    // 確実にスクロールを最下部まで行う（リトライ機能付き）
    scrollToBottomReliably: function(sessionId, maxRetries = 2) {
        if (!window.multiSessionTerminals || !window.multiSessionTerminals[sessionId]) {
            return;
        }
        
        const terminalInfo = window.multiSessionTerminals[sessionId];
        const term = terminalInfo.terminal;
        
        let retryCount = 0;
        let lastScrollY = -1;
        
        const attemptScroll = () => {
            // 現在のスクロール位置を取得
            const currentScrollY = term.buffer.active.viewportY;
            const maxScrollY = term.buffer.active.baseY + term.rows - 1;
            
            // 最下部までスクロール
            term.scrollToBottom();
            
            // スクロール後の位置を確認
            setTimeout(() => {
                const newScrollY = term.buffer.active.viewportY;
                
                // スクロールが動いていない、かつ最下部に到達していない場合
                if (newScrollY === lastScrollY && newScrollY < maxScrollY && retryCount < maxRetries) {
                    retryCount++;
                    lastScrollY = newScrollY;
                    console.log(`[JS] スクロール再試行 ${retryCount}/${maxRetries}: sessionId=${sessionId}, current=${newScrollY}, max=${maxScrollY}`);
                    
                    // 少し待ってから再試行
                    setTimeout(attemptScroll, 100 * retryCount); // 段階的に待機時間を増やす
                } else if (newScrollY >= maxScrollY) {
                    console.log(`[JS] スクロール完了: sessionId=${sessionId}, position=${newScrollY}/${maxScrollY}`);
                } else {
                    lastScrollY = newScrollY;
                    // スクロールが動いた場合は継続
                    if (retryCount < maxRetries && newScrollY < maxScrollY) {
                        retryCount++;
                        setTimeout(attemptScroll, 50);
                    }
                }
            }, 50);
        };
        
        // 初回実行
        attemptScroll();
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

// terminalHubHelpers への追加機能（既存のhelpersオブジェクトを拡張）
if (!window.terminalHubHelpers) {
    window.terminalHubHelpers = {};
}

// ターミナル関連のヘルパー機能を追加
Object.assign(window.terminalHubHelpers, {
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
});

