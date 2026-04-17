// ===== Mock Data =====
const sessions = [
    {
        id: 'claude-main',
        name: 'TerminalHub',
        type: 'claude',
        branch: 'master',
        status: null,
        notification: false,
        memo: '',
    },
    {
        id: 'codex-api',
        name: 'api-server',
        type: 'codex',
        branch: 'main',
        status: null,
        notification: true,
        memo: 'エンドポイントのバリデーション追加中',
    },
    {
        id: 'gemini-shop',
        name: 'ShopService',
        type: 'gemini',
        branch: 'develop',
        status: 'Processing... 1:42',
        notification: false,
        memo: '',
    },
];

// Terminal scenarios - pre-recorded output for each session
const terminalScenarios = {
    'claude-main': {
        initial: [
            '\x1b[1;36m╭─────────────────────────────────────────────────────╮\x1b[0m',
            '\x1b[1;36m│\x1b[0m  \x1b[1;37mClaude Code\x1b[0m \x1b[2mv1.0.30\x1b[0m                               \x1b[1;36m│\x1b[0m',
            '\x1b[1;36m╰─────────────────────────────────────────────────────╯\x1b[0m',
            '',
            '\x1b[2mTips: /help for commands, /model to change model\x1b[0m',
            '',
            '\x1b[1;34m❯\x1b[0m \x1b[37mCLAUDE.md を読み込んでいます...\x1b[0m',
            '',
            '\x1b[33mC:\\Users\\dev\\source\\repos\\TerminalHub\x1b[0m',
            '\x1b[2m(master)\x1b[0m',
            '',
            '\x1b[1;32m>\x1b[0m ',
        ],
        responses: {
            'default': [
                '',
                '\x1b[1;35m⏺ \x1b[0m\x1b[1;37m考えています...\x1b[0m',
                '',
            ],
            'git status': [
                '',
                '\x1b[1;35m⏺ \x1b[0m`git status` を実行します。',
                '',
                '\x1b[2m  ❯ git status\x1b[0m',
                '  On branch master',
                '  Your branch is up to date with \'origin/master\'.',
                '',
                '  nothing to commit, working tree clean',
                '',
                '\x1b[1;32m✓\x1b[0m 作業ツリーはクリーンです。未コミットの変更はありません。',
                '',
                '\x1b[1;32m>\x1b[0m ',
            ],
            'ビルドして': [
                '',
                '\x1b[1;35m⏺ \x1b[0m\x1b[1;37mビルドを実行します。\x1b[0m',
                '',
                '\x1b[2m  ❯ dotnet build TerminalHub/TerminalHub.csproj\x1b[0m',
                '',
                '  MSBuild version 17.14.0+31b54ade3 for .NET',
                '    Determining projects to restore...',
                '    All projects are up-to-date for restore.',
                '    TerminalHub -> bin\\Debug\\net10.0-windows\\TerminalHub.dll',
                '',
                '  \x1b[1;32mビルドに成功しました。\x1b[0m',
                '      0 個の警告',
                '      0 エラー',
                '',
                '\x1b[1;32m✓\x1b[0m ビルドが成功しました。エラーなし、警告なしです。',
                '',
                '\x1b[1;32m>\x1b[0m ',
            ],
        },
    },
    'codex-api': {
        initial: [
            '\x1b[1;32mCodex\x1b[0m \x1b[2m(research preview)\x1b[0m',
            '',
            '\x1b[33mC:\\Users\\dev\\source\\repos\\api-server\x1b[0m',
            '\x1b[2m(main)\x1b[0m',
            '',
            '\x1b[1;32m>\x1b[0m POST /api/orders のバリデーションを追加して',
            '',
            '\x1b[1;33m⟡ \x1b[0m\x1b[1;37mコードを確認しています...\x1b[0m',
            '',
            '\x1b[2m  Reading: Controllers/OrdersController.cs\x1b[0m',
            '\x1b[2m  Reading: Models/OrderRequest.cs\x1b[0m',
            '',
            '\x1b[1;33m⟡ \x1b[0mリクエストボディのバリデーション属性を追加します。',
            '',
            '\x1b[2m  Editing: Models/OrderRequest.cs\x1b[0m',
            '\x1b[2m  Editing: Controllers/OrdersController.cs\x1b[0m',
            '',
            '\x1b[1;32m✓\x1b[0m バリデーション属性と ModelState チェックを追加しました。',
            '',
            '\x1b[1;32m>\x1b[0m ',
        ],
        responses: {
            'default': [
                '',
                '\x1b[1;33m⟡ \x1b[0m\x1b[1;37m処理中...\x1b[0m',
                '',
            ],
        },
    },
    'gemini-shop': {
        initial: [
            '\x1b[1;34m✦\x1b[0m \x1b[1;37mWelcome to Gemini CLI!\x1b[0m',
            '',
            '\x1b[33mC:\\Users\\dev\\source\\repos\\ShopService\x1b[0m',
            '\x1b[2m(develop)\x1b[0m',
            '',
            '\x1b[1;32m>\x1b[0m 在庫管理APIのパフォーマンスを改善して',
            '',
            '\x1b[1;34m✦ \x1b[0m\x1b[1;37m分析中...\x1b[0m',
            '',
            '\x1b[1;34m✦ \x1b[0mプロジェクト構造を確認しています...',
            '',
            '\x1b[2m  Reading: Services/InventoryService.cs\x1b[0m',
            '\x1b[2m  Reading: Repositories/ProductRepository.cs\x1b[0m',
            '\x1b[2m  Reading: Controllers/StockController.cs\x1b[0m',
            '',
            '\x1b[33m⟳ Processing...\x1b[0m \x1b[2m(1:42 elapsed)\x1b[0m',
        ],
        responses: {},
    },
};

// ===== State =====
let activeSessionId = 'claude-main';
let term = null;
let fitAddon = null;
const sessionBuffers = {}; // { lines: string[], played: bool }
let isPlaying = false;

// ===== Initialize =====
document.addEventListener('DOMContentLoaded', () => {
    initTerminal();
    renderSessionList();
    initBottomTabs();
    initInput();
    initDialogs();
    initScreenshotModal();

    // Auto-play: show initial content with typing effect
    setTimeout(() => {
        playScenario(activeSessionId);
    }, 500);
});

function initTerminal() {
    term = new Terminal({
        cursorBlink: true,
        fontSize: 13,
        fontFamily: "'JetBrains Mono', 'Consolas', monospace",
        scrollback: 5000,
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
            brightWhite: '#e5e5e5',
        },
        convertEol: true,
        disableStdin: true,
    });

    fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(document.getElementById('demoTerminal'));

    setTimeout(() => fitAddon.fit(), 100);
    window.addEventListener('resize', () => fitAddon.fit());

    // Initialize buffers
    sessions.forEach(s => {
        sessionBuffers[s.id] = { lines: [], played: false };
    });
}

// ===== Session List =====
function renderSessionList() {
    const list = document.getElementById('sessionList');
    list.innerHTML = '';

    sessions.forEach(s => {
        const item = document.createElement('div');
        item.className = `demo-session-item${s.id === activeSessionId ? ' active' : ''}`;
        item.dataset.sessionId = s.id;

        const badgeClass = s.type === 'claude' ? 'badge-claude'
            : s.type === 'codex' ? 'badge-codex'
            : s.type === 'gemini' ? 'badge-gemini'
            : 'badge-terminal';
        const badgeLabel = s.type === 'claude' ? 'Claude'
            : s.type === 'codex' ? 'Codex'
            : s.type === 'gemini' ? 'Gemini'
            : 'Terminal';

        let html = `<div class="demo-session-name">${s.name}`;
        if (s.notification) {
            html += ` <span class="demo-session-notification"><i class="bi bi-bell-fill"></i></span>`;
        }
        html += `</div>`;
        html += `<div class="demo-session-meta">`;
        html += `<span class="demo-session-badge ${badgeClass}">${badgeLabel}</span>`;
        if (s.branch) {
            html += `<span class="demo-session-branch"><i class="bi bi-git"></i> ${s.branch}</span>`;
        }
        html += `</div>`;
        if (s.status) {
            html += `<div class="demo-session-status"><i class="bi bi-clock-fill"></i> ${s.status}</div>`;
        }
        if (s.memo) {
            html += `<div class="demo-session-memo-preview"><i class="bi bi-journal-text"></i> ${s.memo.split('\n')[0]}</div>`;
        }

        item.innerHTML = html;
        item.addEventListener('click', () => switchSession(s.id));
        list.appendChild(item);
    });
}

function switchSession(sessionId) {
    if (sessionId === activeSessionId) return;

    // 再生中でも切替を許可（再生は中断される）
    isPlaying = false;

    activeSessionId = sessionId;
    renderSessionList();
    updateMemo();

    // ターミナルをクリアして、対象セッションのバッファを即時復元
    term.clear();
    term.reset();

    const buf = sessionBuffers[sessionId];
    if (buf.played) {
        // 保持済みの行を一括で書き戻す（アニメーションなし）
        buf.lines.forEach(line => term.writeln(line));
    } else {
        playScenario(sessionId);
    }
}

// 書き込みをラップして、バッファにも記録する
function writeToTerminal(line) {
    term.writeln(line);
    sessionBuffers[activeSessionId].lines.push(line);
}

// ===== Scenario Playback =====
async function playScenario(sessionId) {
    const scenario = terminalScenarios[sessionId];
    if (!scenario) return;

    isPlaying = true;
    sessionBuffers[sessionId].played = true;

    for (const line of scenario.initial) {
        // 再生中にセッションが切り替わった場合は中断
        if (activeSessionId !== sessionId) break;
        writeToTerminal(line);
        await sleep(40 + Math.random() * 30);
    }
    isPlaying = false;
}

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

// ===== Input Handling =====
function initInput() {
    const input = document.getElementById('demoInput');
    const sendBtn = document.getElementById('sendBtn');

    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendInput();
        }
    });

    sendBtn.addEventListener('click', sendInput);
}

async function sendInput() {
    if (isPlaying) return;
    const input = document.getElementById('demoInput');
    const text = input.value.trim();
    if (!text) return;

    input.value = '';

    const scenario = terminalScenarios[activeSessionId];
    if (!scenario) return;

    // Check if there's a specific response
    const responses = scenario.responses || {};
    const responseLines = responses[text] || responses['default'] || [
        '',
        `\x1b[2m> ${text}\x1b[0m`,
        '',
    ];

    // Type the command echo
    if (activeSessionId.startsWith('claude') || activeSessionId.startsWith('gemini') || activeSessionId.startsWith('codex')) {
        writeToTerminal(`\x1b[1;32m>\x1b[0m ${text}`);
    } else {
        writeToTerminal(text);
    }

    // Play response
    isPlaying = true;
    const currentSession = activeSessionId;
    for (const line of responseLines) {
        if (activeSessionId !== currentSession) break;
        writeToTerminal(line);
        await sleep(30 + Math.random() * 40);
    }
    isPlaying = false;
}

// ===== Bottom Tabs =====
function initBottomTabs() {
    document.querySelectorAll('#bottomTabs .nav-link').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('#bottomTabs .nav-link').forEach(b => b.classList.remove('active'));
            document.querySelectorAll('.demo-tab-pane').forEach(p => p.classList.remove('active'));
            btn.classList.add('active');
            document.getElementById(`tab-${btn.dataset.tab}`).classList.add('active');
        });
    });
}

// ===== Memo =====
function updateMemo() {
    const session = sessions.find(s => s.id === activeSessionId);
    const memoEl = document.getElementById('demoMemo');
    if (memoEl && session) {
        memoEl.value = session.memo || '';
    }
}

// ===== Demo Dialogs =====
function initDialogs() {
    const overlay = document.getElementById('demoDialogOverlay');
    const dialogTitle = document.getElementById('dialogTitle');
    const dialogBody = document.getElementById('dialogBody');
    const dialogClose = document.getElementById('dialogClose');

    document.getElementById('newSessionBtn').addEventListener('click', () => {
        dialogTitle.textContent = '新しいセッション';
        dialogBody.innerHTML = `
            <label>セッション名</label>
            <input type="text" value="NewProject" disabled />
            <label>ターミナルタイプ</label>
            <select disabled>
                <option>Claude Code</option>
                <option>Gemini CLI</option>
                <option>Codex CLI</option>
                <option>通常ターミナル</option>
            </select>
            <label>フォルダパス</label>
            <input type="text" value="C:\\Users\\dev\\source\\repos\\" disabled />
            <div class="demo-dialog-note">
                <i class="bi bi-info-circle"></i> デモのため操作できません
            </div>
        `;
        overlay.style.display = 'flex';
    });

    document.getElementById('settingsBtn').addEventListener('click', () => {
        dialogTitle.textContent = '設定';
        dialogBody.innerHTML = `
            <label>テーマ</label>
            <select disabled>
                <option>ダーク</option>
                <option>ライト</option>
            </select>
            <label>デフォルトフォルダ</label>
            <input type="text" value="C:\\Users\\dev\\source\\repos" disabled />
            <label>Webhook通知</label>
            <input type="text" value="https://hooks.example.com/notify" disabled />
            <div class="demo-dialog-note">
                <i class="bi bi-info-circle"></i> デモのため操作できません
            </div>
        `;
        overlay.style.display = 'flex';
    });

    dialogClose.addEventListener('click', () => {
        overlay.style.display = 'none';
    });

    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) overlay.style.display = 'none';
    });
}

// ===== Screenshot Modal =====
function initScreenshotModal() {
    const modal = document.getElementById('screenshotModal');
    const title = document.getElementById('screenshotTitle');
    const imageContainer = document.getElementById('screenshotImage');
    const closeBtn = modal.querySelector('.screenshot-modal-close');
    const backdrop = modal.querySelector('.screenshot-modal-backdrop');

    document.querySelectorAll('.feature-card[data-screenshot]').forEach(card => {
        card.addEventListener('click', () => {
            const src = card.dataset.screenshot;
            const name = card.querySelector('h3').textContent;
            title.textContent = name;

            // 画像の存在チェック
            const img = new Image();
            img.onload = () => {
                imageContainer.innerHTML = `<img src="${src}" alt="${name}" />`;
                modal.style.display = 'flex';
            };
            img.onerror = () => {
                imageContainer.innerHTML = `
                    <div class="screenshot-noimage">
                        <i class="bi bi-image"></i>
                        <span>No Image</span>
                        <span style="font-size: 0.75rem;">assets/ にスクリーンショットを配置してください</span>
                    </div>
                `;
                modal.style.display = 'flex';
            };
            img.src = src;
        });
    });

    closeBtn.addEventListener('click', () => {
        modal.style.display = 'none';
    });

    backdrop.addEventListener('click', () => {
        modal.style.display = 'none';
    });

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && modal.style.display === 'flex') {
            modal.style.display = 'none';
        }
    });
}
