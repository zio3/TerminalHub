// JavaScript helper functions to avoid using eval
window.terminalHubHelpers = {
    // ターミナル数を取得
    getTerminalCount: function() {
        if (window.multiSessionTerminals) {
            return Object.keys(window.multiSessionTerminals).length;
        }
        return 0;
    },
    
    // 全ターミナルの状態を診断
    diagnoseTerminals: function() {
        console.log('[診断] ===== ターミナル診断開始 =====');
        
        // DOM要素の確認
        const allTerminalDivs = document.querySelectorAll('[id^="terminal-"]');
        console.log(`[診断] DOM上のターミナル要素数: ${allTerminalDivs.length}`);
        allTerminalDivs.forEach(div => {
            console.log(`[診断] DOM要素: ${div.id}, display=${div.style.display}, 子要素数=${div.children.length}`);
            // 子要素の詳細も確認
            if (div.children.length > 0) {
                for (let i = 0; i < div.children.length; i++) {
                    const child = div.children[i];
                    console.log(`  - 子要素[${i}]: ${child.className}, display=${child.style.display}`);
                }
            }
            // xterm-screenクラスの存在確認
            const xtermScreen = div.querySelector('.xterm-screen');
            if (xtermScreen) {
                console.log(`  - xterm-screen存在: true, 表示=${xtermScreen.style.display || 'default'}`);
            } else {
                console.log(`  - xterm-screen存在: false`);
            }
        });
        
        // JavaScript側のターミナル確認
        if (window.multiSessionTerminals) {
            const terminalIds = Object.keys(window.multiSessionTerminals);
            console.log(`[診断] JS側のターミナル数: ${terminalIds.length}`);
            
            terminalIds.forEach(id => {
                const termInfo = window.multiSessionTerminals[id];
                console.log(`[診断] JS側ターミナル: ${id}`);
                console.log(`  - terminal存在: ${!!termInfo.terminal}`);
                console.log(`  - terminal.element存在: ${!!(termInfo.terminal && termInfo.terminal.element)}`);
                if (termInfo.terminal && termInfo.terminal.element) {
                    console.log(`  - element.parentNode: ${!!termInfo.terminal.element.parentNode}`);
                    console.log(`  - rows: ${termInfo.terminal.rows}, cols: ${termInfo.terminal.cols}`);
                }
                console.log(`  - isFirstWrite: ${termInfo.isFirstWrite}`);
                console.log(`  - fitAddon存在: ${!!termInfo.fitAddon}`);
            });
        } else {
            console.log('[診断] window.multiSessionTerminalsが存在しません');
        }
        
        console.log('[診断] ===== ターミナル診断終了 =====');
    },
    
    // ターミナルの修復を試みる
    repairTerminal: function(sessionId) {
        console.log(`[修復] セッション ${sessionId} の修復開始`);
        
        const terminalDiv = document.getElementById(`terminal-${sessionId}`);
        if (!terminalDiv) {
            console.log(`[修復] DOM要素が見つかりません`);
            return;
        }
        
        if (window.multiSessionTerminals && window.multiSessionTerminals[sessionId]) {
            const termInfo = window.multiSessionTerminals[sessionId];
            if (termInfo.terminal && termInfo.terminal.element) {
                // xtermの要素が正しい親要素に存在するか確認
                if (termInfo.terminal.element.parentNode !== terminalDiv) {
                    console.log(`[修復] xterm要素を正しいDOM要素に再アタッチ`);
                    terminalDiv.appendChild(termInfo.terminal.element);
                }
                
                // リフレッシュを実行
                console.log(`[修復] ターミナルをリフレッシュ`);
                termInfo.terminal.refresh(0, termInfo.terminal.rows - 1);
                
                // fitAddonがあればfit実行
                if (termInfo.fitAddon) {
                    console.log(`[修復] fitAddon.fit()実行`);
                    termInfo.fitAddon.fit();
                }
                
                console.log(`[修復] 修復完了`);
            } else {
                console.log(`[修復] xtermインスタンスが破損しています`);
            }
        } else {
            console.log(`[修復] JS側のターミナル情報が見つかりません`);
        }
    },

    // Keyboard shortcuts
    setupKeyboardShortcuts: function(dotNetRef) {
        window.terminalHubKeyHandler = function(e) {
            // Ctrl + Shift + D: デバッグ情報を表示（開発機能内で利用）
            if (e.ctrlKey && e.shiftKey && e.key === 'D') {
                e.preventDefault();
                console.log('[TerminalHub] Debug shortcut triggered');
                // デバッグ情報をコンソールに出力
                console.log('Debug Info:');
                console.log('Sessions:', Object.keys(window.multiSessionTerminals || {}));
                console.log('Active terminals:', window.multiSessionTerminals);
                console.log('LocalStorage sessions:', localStorage.getItem('terminalHub_sessions'));
                console.log('LocalStorage activeSession:', localStorage.getItem('terminalHub_activeSession'));
            }
        };
        
        document.addEventListener('keydown', window.terminalHubKeyHandler);
    },
    
    cleanupKeyboardShortcuts: function() {
        if (window.terminalHubKeyHandler) {
            document.removeEventListener('keydown', window.terminalHubKeyHandler);
            window.terminalHubKeyHandler = null;
        }
    },
    
    // Get window dimensions
    getWindowSize: function() {
        return {
            width: window.innerWidth,
            height: window.innerHeight
        };
    },
    
    // Check if element exists
    checkElementExists: function(elementId) {
        return document.getElementById(elementId) !== null;
    },
    
    // Notification handling
    checkNotificationPermission: function() {
        if (!("Notification" in window)) {
            console.log("This browser does not support desktop notification");
            return "unsupported";
        }
        
        return Notification.permission;
    },
    
    requestNotificationPermission: async function() {
        if (!("Notification" in window)) {
            console.log("This browser does not support desktop notification");
            return "unsupported";
        }
        
        if (Notification.permission === "granted") {
            return "granted";
        }
        
        if (Notification.permission !== "denied") {
            const permission = await Notification.requestPermission();
            return permission;
        }
        
        return Notification.permission;
    },
    
    showNotification: function(title, body, tag) {
        if (!("Notification" in window)) {
            console.log("This browser does not support desktop notification");
            return;
        }

        if (Notification.permission === "granted") {
            const notification = new Notification(title, {
                body: body,
                icon: '/favicon.ico',
                tag: tag,
                requireInteraction: true // 通知を自動的に閉じない
            });

            // 通知をクリックしたときの処理
            notification.onclick = function(event) {
                event.preventDefault();
                window.focus(); // ブラウザウィンドウをフォーカス
                notification.close();

                // Blazorのメソッドを呼び出してセッションを選択
                if (window.terminalHubDotNetRef) {
                    window.terminalHubDotNetRef.invokeMethodAsync('OnNotificationClick', tag);
                }
            };
        }
    },

    openNotificationSettings: function() {
        // ブラウザの通知設定を開く案内
        // 直接設定画面を開くことはできないため、ユーザーに案内を表示
        const isChrome = /Chrome/.test(navigator.userAgent) && /Google Inc/.test(navigator.vendor);
        const isEdge = /Edg/.test(navigator.userAgent);
        const isFirefox = /Firefox/.test(navigator.userAgent);

        let message = "通知許可を解除するには：\n\n";

        if (isChrome || isEdge) {
            message += "1. アドレスバー左側の鍵アイコン（🔒）をクリック\n";
            message += "2.「サイトの設定」をクリック\n";
            message += "3.「通知」を「ブロック」に変更";
        } else if (isFirefox) {
            message += "1. アドレスバー左側の鍵アイコン（🔒）をクリック\n";
            message += "2.「通知」の権限を「ブロック」に変更";
        } else {
            message += "アドレスバー左側のサイト情報アイコンをクリックして、\n通知の設定を変更してください。";
        }

        alert(message);
    },

    // ターミナル診断用関数
    scrollAllTerminalsToBottom: function() {
        const activeTerminals = window.multiSessionTerminals;
        if (activeTerminals) {
            Object.keys(activeTerminals).forEach(sessionId => {
                if (activeTerminals[sessionId] && activeTerminals[sessionId].terminal) {
                    activeTerminals[sessionId].terminal.scrollToBottom();
                }
            });
        }
    },

    scrollAllTerminalsToTop: function() {
        const activeTerminals = window.multiSessionTerminals;
        if (activeTerminals) {
            Object.keys(activeTerminals).forEach(sessionId => {
                if (activeTerminals[sessionId] && activeTerminals[sessionId].terminal) {
                    activeTerminals[sessionId].terminal.scrollToTop();
                }
            });
        }
    },

    getAllTerminalScrollPositions: function() {
        const activeTerminals = window.multiSessionTerminals;
        const positions = {};
        if (activeTerminals) {
            Object.keys(activeTerminals).forEach(sessionId => {
                const term = activeTerminals[sessionId]?.terminal;
                if (term && term.buffer && term.buffer.active) {
                    positions[sessionId] = {
                        viewportY: term.buffer.active.viewportY,
                        baseY: term.buffer.active.baseY,
                        length: term.buffer.active.length
                    };
                }
            });
        }
        return positions;
    },

    downloadBase64File: function(base64, filename, mimeType) {
        try {
            const link = document.createElement('a');
            link.href = `data:${mimeType};charset=utf-8;base64,${base64}`;
            link.download = filename;
            link.click();
        } catch (e) {
            console.error('downloadBase64File error:', e);
            throw e;
        }
    },

    // Store DotNetRef for notification callback
    setDotNetRef: function(dotNetRef) {
        window.terminalHubDotNetRef = dotNetRef;
    },
    
    // LocalStorage settings management
    getSettings: function() {
        const settings = localStorage.getItem('terminalHub_settings');
        if (settings) {
            return JSON.parse(settings);
        }
        
        // Default settings
        return {
            notifications: {
                enableBrowserNotifications: true,
                processingTimeThresholdSeconds: 5
            },
            webhook: {
                enabled: false,
                url: "",
                headers: {
                    "Content-Type": "application/json"
                }
            },
            claudeHook: {
                enabled: true,
                events: {
                    stop: true,
                    userPromptSubmit: true,
                    notification: true
                }
            },
            special: {
                claudeModeSwitchKey: "altM"
            }
        };
    },
    
    saveSettings: function(settings) {
        localStorage.setItem('terminalHub_settings', JSON.stringify(settings));
    },
    
    // JSON download helper
    downloadJson: function(jsonString, filename) {
        const blob = new Blob([jsonString], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    },
    
    updateNotificationSettings: function(enableBrowserNotifications, processingTimeThresholdSeconds) {
        const settings = this.getSettings();
        settings.notifications.enableBrowserNotifications = enableBrowserNotifications;
        settings.notifications.processingTimeThresholdSeconds = processingTimeThresholdSeconds;
        this.saveSettings(settings);
    },
    
    updateWebhookSettings: function(enabled, url, headers) {
        const settings = this.getSettings();
        settings.webhook.enabled = enabled;
        settings.webhook.url = url;
        settings.webhook.headers = headers || { "Content-Type": "application/json" };
        this.saveSettings(settings);
    },
    
    getNotificationSettings: function() {
        return this.getSettings().notifications;
    },
    
    getWebhookSettings: function() {
        return this.getSettings().webhook;
    },

    updateSpecialSettings: function(claudeModeSwitchKey) {
        const settings = this.getSettings();
        if (!settings.special) {
            settings.special = {};
        }
        settings.special.claudeModeSwitchKey = claudeModeSwitchKey;
        this.saveSettings(settings);
    },

    getSpecialSettings: function() {
        const settings = this.getSettings();
        return settings.special || { claudeModeSwitchKey: "altM" };
    },

    updateClaudeHookSettings: function(enabled, eventStop, eventUserPromptSubmit, eventNotification) {
        const settings = this.getSettings();
        if (!settings.claudeHook) {
            settings.claudeHook = { enabled: true, events: {} };
        }
        settings.claudeHook.enabled = enabled;
        settings.claudeHook.events = {
            stop: eventStop,
            userPromptSubmit: eventUserPromptSubmit,
            notification: eventNotification
        };
        this.saveSettings(settings);
    },

    getClaudeHookSettings: function() {
        const settings = this.getSettings();
        return settings.claudeHook || {
            enabled: true,
            events: {
                stop: true,
                userPromptSubmit: true,
                notification: true
            }
        };
    },

    updateSessionSettings: function(sortMode) {
        const settings = this.getSettings();
        if (!settings.sessions) {
            settings.sessions = {};
        }
        settings.sessions.sortMode = sortMode;
        this.saveSettings(settings);
    },

    getSessionSettings: function() {
        const settings = this.getSettings();
        return settings.sessions || { sortMode: "createdAt" };
    },

    updateDevToolsSettings: function(enabled) {
        const settings = this.getSettings();
        if (!settings.devTools) {
            settings.devTools = {};
        }
        settings.devTools.enabled = enabled;
        this.saveSettings(settings);
    },

    getDevToolsSettings: function() {
        const settings = this.getSettings();
        return settings.devTools || { enabled: false };
    }
};