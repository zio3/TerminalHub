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

// デバッグ用データ記録
window.terminalDebug = {
    enabled: true,  // デバッグモードの有効/無効
    dataLog: [],    // 受信データのログ
    ansiLog: [],    // ANSIシーケンスのログ
    maxLogSize: 1000,  // 最大ログサイズ
    
    // データをログに記録
    logData: function(sessionId, data, context) {
        if (!this.enabled) return;
        
        const entry = {
            timestamp: new Date().toISOString(),
            sessionId: sessionId,
            context: context,
            dataLength: data.length,
            data: data.substring(0, 200),  // 最初の200文字のみ保存
            fullData: data  // 完全なデータ（必要に応じて）
        };
        
        this.dataLog.push(entry);
        if (this.dataLog.length > this.maxLogSize) {
            this.dataLog.shift();  // 古いエントリを削除
        }
        
        // ANSIシーケンスを検出
        this.detectAnsiSequences(sessionId, data);
    },
    
    // ANSIエスケープシーケンスを検出して記録
    detectAnsiSequences: function(sessionId, data) {
        const sequences = [];
        const regex = /\x1b\[([0-9;]*)([A-Za-z])/g;
        let match;
        
        while ((match = regex.exec(data)) !== null) {
            const seq = {
                timestamp: new Date().toISOString(),
                sessionId: sessionId,
                sequence: match[0],
                params: match[1],
                command: match[2],
                index: match.index,
                context: this.getSequenceDescription(match[2], match[1])
            };
            
            sequences.push(seq);
            this.ansiLog.push(seq);
        }
        
        if (this.ansiLog.length > this.maxLogSize) {
            this.ansiLog = this.ansiLog.slice(-this.maxLogSize);
        }
        
        // 重要なシーケンスを検出したら警告
        if (sequences.length > 0) {
            console.log(`[TerminalDebug] ANSIシーケンス検出: sessionId=${sessionId}`, sequences);
        }
    },
    
    // ANSIシーケンスの説明を取得
    getSequenceDescription: function(command, params) {
        const descriptions = {
            'A': `カーソル上移動(${params || 1}行)`,
            'B': `カーソル下移動(${params || 1}行)`,
            'C': `カーソル右移動(${params || 1}列)`,
            'D': `カーソル左移動(${params || 1}列)`,
            'H': `カーソル位置設定(${params || '1,1'})`,
            'J': params === '2' ? '画面全体クリア' : '画面部分クリア',
            'K': params === '2' ? '行全体クリア' : '行部分クリア',
            'm': 'テキスト属性設定',
            's': 'カーソル位置保存',
            'u': 'カーソル位置復元'
        };
        
        return descriptions[command] || `不明なコマンド(${command})`;
    },
    
    // デバッグ情報を表示
    showReport: function() {
        console.group('[TerminalDebug] デバッグレポート');
        console.log('データログ数:', this.dataLog.length);
        console.log('ANSIログ数:', this.ansiLog.length);
        
        // 最近のデータログ
        console.group('最近のデータ受信:');
        this.dataLog.slice(-10).forEach(entry => {
            console.log(`${entry.timestamp} [${entry.context}] ${entry.dataLength}バイト`);
        });
        console.groupEnd();
        
        // ANSIシーケンスの統計
        const ansiStats = {};
        this.ansiLog.forEach(seq => {
            const key = seq.command;
            ansiStats[key] = (ansiStats[key] || 0) + 1;
        });
        
        console.group('ANSIシーケンス統計:');
        Object.entries(ansiStats).forEach(([cmd, count]) => {
            console.log(`${cmd}: ${count}回`);
        });
        console.groupEnd();
        
        console.groupEnd();
    },
    
    // ログをクリア
    clear: function() {
        this.dataLog = [];
        this.ansiLog = [];
        console.log('[TerminalDebug] ログをクリアしました');
    }
};

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

    // バッファ内容を書き込む専用関数（スクロール処理含む）
    writeBuffered: function(sessionId, data) {
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const terminalInfo = window.multiSessionTerminals[sessionId];
            const term = terminalInfo.terminal;
            
            // デバッグログ
            window.terminalDebug.logData(sessionId, data, 'writeBuffered');
            
            // 大きなデータをチャンクに分割して書き込み
            this.writeDataInChunks(term, data).then(() => {
                // 書き込み完了後にスクロール
                term.scrollToBottom();
                console.log(`[JS] バッファ内容書き込み完了後スクロール: sessionId=${sessionId}`);
            }).catch(error => {
                console.error(`[JS] バッファ書き込みエラー: sessionId=${sessionId}`, error);
            });
            
            terminalInfo.hasBufferedContent = true;
        }
    },

    // データをチャンクに分割して順次書き込み
    writeDataInChunks: async function(terminal, data, chunkSize = 1024) {
        if (!data || data.length === 0) return;
        
        // データが小さい場合は直接書き込み
        if (data.length <= chunkSize) {
            return new Promise((resolve) => {
                terminal.write(data, resolve);
            });
        }
        
        // 大きなデータをチャンクに分割
        for (let i = 0; i < data.length; i += chunkSize) {
            const chunk = data.slice(i, i + chunkSize);
            await new Promise((resolve) => {
                terminal.write(chunk, resolve);
            });
            
            // 次のチャンクの前に少し待機（UIのブロックを防ぐ）
            if (i + chunkSize < data.length) {
                await new Promise(resolve => setTimeout(resolve, 1));
            }
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

