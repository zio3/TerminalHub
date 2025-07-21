// JavaScript helper functions to avoid using eval
window.terminalHubHelpers = {
    // DevWindow drag handling
    setupDevWindowDrag: function(dotNetRef) {
        window.devWindowMouseMove = function(e) {
            dotNetRef.invokeMethodAsync('OnDevWindowMouseMove', e.clientX, e.clientY);
        };
        window.devWindowMouseUp = function() {
            dotNetRef.invokeMethodAsync('OnDevWindowMouseUp');
        };
        
        document.addEventListener('mousemove', window.devWindowMouseMove);
        document.addEventListener('mouseup', window.devWindowMouseUp);
    },
    
    cleanupDevWindowDrag: function() {
        if (window.devWindowMouseMove) {
            document.removeEventListener('mousemove', window.devWindowMouseMove);
            window.devWindowMouseMove = null;
        }
        if (window.devWindowMouseUp) {
            document.removeEventListener('mouseup', window.devWindowMouseUp);
            window.devWindowMouseUp = null;
        }
    },
    
    // Keyboard shortcuts
    setupKeyboardShortcuts: function(dotNetRef) {
        window.terminalHubKeyHandler = function(e) {
            // Ctrl + Shift + D: デバッグ情報を表示
            if (e.ctrlKey && e.shiftKey && e.key === 'D') {
                e.preventDefault();
                console.log('Debug Info:');
                console.log('Sessions:', Object.keys(window.multiSessionTerminals || {}));
                console.log('Active terminals:', window.multiSessionTerminals);
                console.log('LocalStorage sessions:', localStorage.getItem('terminalHub_sessions'));
                console.log('LocalStorage activeSession:', localStorage.getItem('terminalHub_activeSession'));
            }
            // Ctrl + Shift + C: ローカルストレージをクリア
            else if (e.ctrlKey && e.shiftKey && e.key === 'C') {
                e.preventDefault();
                if (confirm('ローカルストレージをクリアしますか？')) {
                    localStorage.removeItem('terminalHub_sessions');
                    localStorage.removeItem('terminalHub_activeSession');
                    window.location.reload();
                }
            }
            // Ctrl + Shift + N: 新しいセッション
            else if (e.ctrlKey && e.shiftKey && e.key === 'N') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnShortcutNewSession');
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
    }
};