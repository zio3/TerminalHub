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
            
            // 右クリック検出とIMEスタイル制御
            setupContextMenuAndIME(element, sessionId);
            
            // xterm.jsのリサイズイベントリスナーを追加
            term.onResize((size) => {
                // リサイズ完了時に最下部にスクロール（複数回実行して確実に）
                const scrollToBottomReliable = () => {
                    term.scrollToBottom();
                    // 少し待ってもう一度
                    setTimeout(() => {
                        term.scrollToBottom();
                        console.log(`[JS] xterm.onResize後スクロール実行: sessionId=${sessionId}, cols=${size.cols}, rows=${size.rows}`);
                    }, 50);
                };
                
                requestAnimationFrame(() => {
                    scrollToBottomReliable();
                });
            });
            
            // ResizeObserverを設定
            window.resizeObserverManager.add(sessionId, element, () => {
                if (fitAddon) {
                    fitAddon.fit();
                    console.log(`[JS] リサイズ: sessionId=${sessionId}, cols=${term.cols}, rows=${term.rows}`);
                    
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnTerminalSizeChanged', sessionId, term.cols, term.rows);
                    }
                    
                    // 複数のタイミングでスクロールを試行（より確実に）
                    const multipleScrollAttempts = () => {
                        // 即座に1回目
                        term.scrollToBottom();
                        
                        // 次のフレームで2回目
                        requestAnimationFrame(() => {
                            term.scrollToBottom();
                            
                            // さらに少し待って3回目
                            setTimeout(() => {
                                term.scrollToBottom();
                                console.log(`[JS] ResizeObserver後スクロール実行(複数回): sessionId=${sessionId}`);
                            }, 100);
                            
                            // 最後にもう一度（200ms後）
                            setTimeout(() => {
                                term.scrollToBottom();
                                console.log(`[JS] ResizeObserver後最終スクロール: sessionId=${sessionId}`);
                            }, 200);
                        });
                    };
                    
                    multipleScrollAttempts();
                }
            });
            
            window.multiSessionTerminals[sessionId] = {
                terminal: term,
                fitAddon: fitAddon
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
    }
};