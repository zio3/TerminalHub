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
                console.log(`  - fitAddon存在: ${!!termInfo.fitAddon}`);
            });
        } else {
            console.log('[診断] window.multiSessionTerminalsが存在しません');
        }
        
        console.log('[診断] ===== ターミナル診断終了 =====');
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

    // モバイルキーボード表示時のハンバーガーメニュー位置補正
    setupMobileKeyboardHandler: function() {
        if (window.visualViewport) {
            const updateButtonPosition = () => {
                const button = document.querySelector('.mobile-sidebar-toggle');
                if (button) {
                    // キーボード表示時はvisualViewport.offsetTopが変化する
                    button.style.top = `${10 + window.visualViewport.offsetTop}px`;
                }
            };

            window.visualViewport.addEventListener('resize', updateButtonPosition);
            window.visualViewport.addEventListener('scroll', updateButtonPosition);
        }
    },

    // スプリッタードラッグ初期化（イベント委譲方式 - DOM存在前でもOK）
    initSplitter: function(dotNetRef) {
        if (window._splitterInitialized) return;
        window._splitterInitialized = true;

        let isDragging = false;
        let dragType = null; // 'vertical' or 'horizontal'
        let activeSplitter = null;

        document.addEventListener('mousedown', (e) => {
            if (!e.target) return;
            if (e.target.id === 'panel-splitter') {
                isDragging = true;
                dragType = 'vertical';
                activeSplitter = e.target;
                document.body.style.cursor = 'row-resize';
                document.body.style.userSelect = 'none';
                e.preventDefault();
            } else if (e.target.id === 'sidebar-splitter') {
                isDragging = true;
                dragType = 'horizontal';
                activeSplitter = e.target;
                document.body.style.cursor = 'col-resize';
                document.body.style.userSelect = 'none';
                e.preventDefault();
            }
        });

        document.addEventListener('mousemove', (e) => {
            if (!isDragging || !activeSplitter) return;
            e.preventDefault();

            if (dragType === 'vertical') {
                const container = activeSplitter.parentElement;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                let percent = Math.round((y / rect.height) * 100);
                percent = Math.max(20, Math.min(90, percent));

                const terminalSection = container.querySelector('.terminal-section');
                const bottomSection = container.querySelector('.bottom-panel-section');
                if (terminalSection) terminalSection.style.height = percent + '%';
                if (bottomSection) bottomSection.style.height = (100 - percent) + '%';
                activeSplitter.dataset.percent = percent;
            } else if (dragType === 'horizontal') {
                const container = activeSplitter.parentElement;
                const rect = container.getBoundingClientRect();
                const x = e.clientX - rect.left;
                let percent = Math.round((x / rect.width) * 100);
                percent = Math.max(15, Math.min(70, percent)); // 上限はモバイルで広く使いたい要望に合わせ70%

                const sidebar = container.querySelector('.session-list-sidebar');
                if (sidebar) sidebar.style.width = percent + '%';
                activeSplitter.dataset.percent = percent;
            }
        });

        document.addEventListener('mouseup', () => {
            if (!isDragging) return;
            const type = dragType;
            isDragging = false;
            dragType = null;
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            const rawPercent = activeSplitter?.dataset.percent;
            activeSplitter = null;

            // mousemoveなしでmouseupした場合は何もしない
            if (!rawPercent) return;
            const percent = parseInt(rawPercent);

            if (type === 'vertical') {
                dotNetRef.invokeMethodAsync('OnSplitterDragEnd', percent);
            } else if (type === 'horizontal') {
                dotNetRef.invokeMethodAsync('OnSidebarSplitterDragEnd', percent);
            }

            // ターミナルをリサイズ
            if (window.multiSessionTerminals) {
                Object.values(window.multiSessionTerminals).forEach(t => {
                    if (t && t.fitAddon) {
                        try { t.fitAddon.fit(); } catch(e) {}
                    }
                });
            }
        });
    },

    // テーマ切替
    setTheme: function(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
    },

    // UI 言語の切替。.AspNetCore.Culture cookie を書き込んでページを reload。
    // ASP.NET Core 標準の CookieRequestCultureProvider が読む形式 (c=xx|uic=xx)。
    setUiCulture: function(culture) {
        try {
            var value = encodeURIComponent('c=' + culture + '|uic=' + culture);
            var oneYear = 60 * 60 * 24 * 365;
            document.cookie = '.AspNetCore.Culture=' + value +
                '; path=/; max-age=' + oneYear + '; samesite=lax';
        } catch (e) {
            // Cookie ブロック環境では書き込みが失敗する。reload 自体は走らせるが、
            // デバッグ時に原因を追えるよう console に警告を残す。
            console.warn('[TerminalHub i18n] setUiCulture: cookie write failed', e);
        }
        window.location.reload();
    }
};

// ===== スラッシュコマンド補完（スパイク） =====
// ポップアップ表示中の制御キー（↑↓/Tab/Enter/Esc）を keydown の capture 段階で
// 横取りして preventDefault し、.NET 側の OnSlashKey へ通知する。
// Blazor Server の @onkeydown:preventDefault は「今まさに押した打鍵」には確実に効かない
// （サーバー往復のため）ので、確実な横取りは JS 側で行う必要がある。
window.slashAutocomplete = {
    _handlers: {},
    register: function (textareaId, dotNetRef) {
        var ta = document.getElementById(textareaId);
        if (!ta) return;
        // 二重登録防止: 既に同じテキストエリアへ登録済みなら、古いリスナを外してから付け直す。
        if (this._handlers[textareaId]) {
            this.unregister(textareaId);
        }
        // 横取り対象キー。値部分は特に使わない。
        var keys = { 'ArrowDown': 1, 'ArrowUp': 1, 'Tab': 1, 'Enter': 1, 'Escape': 1 };
        var handler = function (e) {
            // ポップアップが開いているときだけ横取り（.NET が data 属性で状態を伝える）。
            if (ta.dataset.slashOpen !== '1') return;
            if (!keys[e.key]) return;
            // Enter は修飾キー併用時（Shift+Enter の改行など）は横取りしない。
            if (e.key === 'Enter' && (e.shiftKey || e.ctrlKey || e.altKey)) return;
            // Tab/ArrowはCtrl併用時（他機能）も尊重して素通し。
            if ((e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Tab') && e.ctrlKey) return;
            // Shift+Tab は Claude Code のモード切替に使われ得るので横取りしない。
            if (e.key === 'Tab' && e.shiftKey) return;
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnSlashKey', e.key);
        };
        // capture=true で、Blazor 側のリスナより先にブラウザ既定動作を止める。
        ta.addEventListener('keydown', handler, true);
        this._handlers[textareaId] = { ta: ta, handler: handler };
    },
    unregister: function (textareaId) {
        var h = this._handlers[textareaId];
        if (h) {
            h.ta.removeEventListener('keydown', h.handler, true);
            delete this._handlers[textareaId];
        }
    },
    // 候補をマウスクリックで確定した後、テキストエリアへフォーカスを戻す。
    focusInput: function (textareaId) {
        var ta = document.getElementById(textareaId);
        if (ta) ta.focus();
    }
};

// 入力欄へのファイルドロップ。ブラウザはドロップされたファイルのフルパスを渡さない（名前と中身だけ）ので、
// 中身を /api/drop に送ってアプリ設定フォルダ配下へ保存してもらい、返ってきた保存先パスを入力欄の
// カーソル位置へ挿入する（Windows Terminal にファイルを落としたときの「パスが入る」に相当）。
// テキストのドロップ（エディタからパス文字列を引きずってきた等）は files が無いので触らず、
// ブラウザの既定動作（そのまま挿入）に任せる。
// ドロップが入力欄から外れても、ブラウザがファイルを開いて画面ごと置き換わらないよう、
// 最初の register でページ全体の file ドロップの既定動作を止める（1 回だけ）。
window.fileDrop = {
    _handlers: {},
    _pageGuardInstalled: false,
    _hasFiles: function (e) {
        var dt = e.dataTransfer;
        if (!dt || !dt.types) return false;
        for (var i = 0; i < dt.types.length; i++) {
            if (dt.types[i] === 'Files') return true;
        }
        return false;
    },
    _installPageGuard: function () {
        if (this._pageGuardInstalled) return;
        this._pageGuardInstalled = true;
        var self = this;
        document.addEventListener('dragover', function (e) {
            if (self._hasFiles(e)) { e.preventDefault(); }
        });
        document.addEventListener('drop', function (e) {
            if (self._hasFiles(e)) { e.preventDefault(); }
        });
    },
    // 空白や引用符などを含むパスは二重引用符で包む（CLI にそのまま渡せる形）。
    _quote: function (path) {
        if (/[\s"'&|<>^()]/.test(path)) {
            return '"' + path.replace(/"/g, '""') + '"';
        }
        return path;
    },
    _insert: function (ta, text) {
        var start = ta.selectionStart, end = ta.selectionEnd;
        if (typeof start !== 'number') { start = end = ta.value.length; }
        ta.setRangeText(text, start, end, 'end');
        // Blazor の @bind:event="oninput" に拾わせる
        ta.dispatchEvent(new Event('input', { bubbles: true }));
        ta.focus();
    },
    _showMessage: function (ta, text) {
        var host = ta.parentElement || document.body;
        var old = host.querySelector('.file-drop-msg');
        if (old) old.remove();
        var div = document.createElement('div');
        div.className = 'file-drop-msg';
        div.textContent = text;
        host.appendChild(div);
        setTimeout(function () { if (div.parentElement) div.remove(); }, 5000);
    },
    _format: function (template, name) {
        return (template || '').replace('{0}', name);
    },
    // opts: { maxBytes, tooLarge('{0}'=名前), folderNotSupported, failed('{0}'=名前) }
    register: function (textareaId, opts) {
        var ta = document.getElementById(textareaId);
        if (!ta) return;
        if (this._handlers[textareaId]) { this.unregister(textareaId); }
        this._installPageGuard();
        var self = this;
        opts = opts || {};

        var onDragOver = function (e) {
            if (!self._hasFiles(e)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            ta.classList.add('file-drop-over');
        };
        var onDragLeave = function () { ta.classList.remove('file-drop-over'); };
        var onDrop = function (e) {
            ta.classList.remove('file-drop-over');
            if (!self._hasFiles(e)) return;   // テキストのドロップは既定動作に任せる
            e.preventDefault();
            e.stopPropagation();

            // フォルダは中身が取れないので断る（Chrome では size 0・type 空の File として届く）
            var items = e.dataTransfer.items;
            var files = [];
            var hadFolder = false;
            for (var i = 0; i < e.dataTransfer.files.length; i++) {
                var f = e.dataTransfer.files[i];
                var entry = items && items[i] && items[i].webkitGetAsEntry ? items[i].webkitGetAsEntry() : null;
                if (entry && entry.isDirectory) { hadFolder = true; continue; }
                files.push(f);
            }
            if (hadFolder) { self._showMessage(ta, opts.folderNotSupported || 'Folders are not supported'); }
            if (files.length === 0) return;

            var maxBytes = opts.maxBytes || (20 * 1024 * 1024);
            var pending = files.slice();
            var next = function () {
                if (pending.length === 0) return;
                var file = pending.shift();
                if (file.size > maxBytes) {
                    self._showMessage(ta, self._format(opts.tooLarge, file.name) || (file.name + ' is too large'));
                    next();
                    return;
                }
                fetch('/api/drop?name=' + encodeURIComponent(file.name), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/octet-stream' },
                    body: file
                }).then(function (res) {
                    if (!res.ok) throw new Error('HTTP ' + res.status);
                    return res.json();
                }).then(function (json) {
                    if (!json || !json.path) throw new Error('no path');
                    self._insert(ta, self._quote(json.path) + ' ');
                }).catch(function (err) {
                    console.warn('[fileDrop] 保存に失敗:', file.name, err);
                    self._showMessage(ta, self._format(opts.failed, file.name) || ('Failed: ' + file.name));
                }).then(next);
            };
            next();
        };

        ta.addEventListener('dragover', onDragOver);
        ta.addEventListener('dragleave', onDragLeave);
        ta.addEventListener('drop', onDrop);
        this._handlers[textareaId] = { ta: ta, onDragOver: onDragOver, onDragLeave: onDragLeave, onDrop: onDrop };
    },
    unregister: function (textareaId) {
        var h = this._handlers[textareaId];
        if (!h) return;
        h.ta.removeEventListener('dragover', h.onDragOver);
        h.ta.removeEventListener('dragleave', h.onDragLeave);
        h.ta.removeEventListener('drop', h.onDrop);
        delete this._handlers[textareaId];
    }
};
