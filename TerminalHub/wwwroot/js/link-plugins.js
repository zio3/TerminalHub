// リンクプラグイン: ターミナル出力中のトークンを検出して URL リンクにする仕組み。
//
// 設計の要点:
// - プラグインは副作用を持たない。検出ルールと「開くべき URL」を返すだけで、
//   実際に開くのはホスト側（既存の activateTerminalLink を通るので、
//   「クリックでコピー」設定などもそのまま効く）。
// - 桁の対応付け（全角混じりの行でも位置がズレない）と xterm への配線はホストが持つ。
//   プラグイン作者に触らせても事故るだけで、調整したい部分でもないため。
// - xterm のリンクプロバイダは登録順が優先度になる（先に登録した側が重なりで勝つ）。
//   ユーザープラグインを組み込みより先に登録することで、組み込みの解釈を上書きできる。

// 読み込み済みプラグイン（id -> モジュール）。ページ内で一度読めば使い回す。
const loadedPlugins = new Map();
// セッションごとの受け渡しデータ（Blazor 側から setSessionPluginContext で供給）
const sessionContexts = new Map();
// セッションごとに登録したリンクプロバイダの破棄用（採用を変えたら貼り直す）
const registeredProviders = new Map();

// プラグインを読み直す。編集→反映のループを速くするため、いつでも呼べるようにしておく。
window.reloadLinkPlugins = async function () {
    loadedPlugins.clear();
    return await loadLinkPlugins();
};

async function loadLinkPlugins() {
    let list;
    try {
        const res = await fetch('/api/plugins');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        list = await res.json();
    } catch (e) {
        console.error('[LinkPlugins] 一覧の取得に失敗:', e);
        return [];
    }

    const result = [];
    for (const entry of list) {
        try {
            // stamp を付けて import() のキャッシュを外す（保存し直せば次の読み込みで反映される）
            const mod = await import(`${entry.url}?v=${entry.stamp}`);
            const plugin = mod.default;
            if (!plugin || typeof plugin !== 'object') {
                console.error(`[LinkPlugins] ${entry.file}: default export がオブジェクトではありません`);
                continue;
            }
            if (!plugin.id) {
                console.error(`[LinkPlugins] ${entry.file}: id がありません`);
                continue;
            }
            if (typeof plugin.url !== 'function') {
                console.error(`[LinkPlugins] ${entry.file}: url(text, ctx) が関数ではありません`);
                continue;
            }
            // 同じ id が両方にあればユーザー側が勝つ（一覧がユーザー先頭で返るため）
            if (loadedPlugins.has(plugin.id)) continue;

            loadedPlugins.set(plugin.id, { ...entry, plugin });
            result.push({ id: plugin.id, source: entry.source, file: entry.file, vars: plugin.vars ?? [] });
        } catch (e) {
            // 自分で書いたものが動かないのが最悪なので、握り潰さず出す
            console.error(`[LinkPlugins] ${entry.file} の読み込みに失敗:`, e);
        }
    }
    console.log(`[LinkPlugins] ${result.length} 件読み込み:`, result.map(r => `${r.id}(${r.source})`).join(', '));
    return result;
}

// 設定画面から呼ぶ: 読み込み済みプラグインの一覧（id と宣言された変数）を返す
window.listLinkPlugins = async function () {
    if (loadedPlugins.size === 0) await loadLinkPlugins();
    return [...loadedPlugins.values()].map(e => ({
        id: e.plugin.id,
        source: e.source,
        file: e.file,
        vars: e.plugin.vars ?? []
    }));
};

// Blazor から供給される、セッションごとの受け渡しデータ。
// URL の判定は検出時（provideLinks 内）に同期で必要なため、事前に押し込んでおく。
window.setSessionPluginContext = function (sessionId, context) {
    sessionContexts.set(sessionId, context ?? {});
};

// プラグインを xterm に登録する。settings は Blazor 側が解決した「採用するものを順に並べた配列」。
window.applyLinkPlugins = async function (sessionId, settings) {
    const entry = window.multiSessionTerminals?.[sessionId];
    const term = entry?.terminal;
    if (!term || typeof term.registerLinkProvider !== "function") return;

    if (loadedPlugins.size === 0) await loadLinkPlugins();

    // 前回分を破棄してから貼り直す（採用や順序を変えたときのため）
    const previous = registeredProviders.get(sessionId);
    if (previous) {
        for (const d of previous) {
            try { d.dispose(); } catch { /* 破棄済みは無視 */ }
        }
    }

    const disposables = [];
    for (const setting of settings ?? []) {
        const entry = loadedPlugins.get(setting.id);
        if (!entry) {
            console.warn(`[LinkPlugins] ${setting.id} は見つかりません（ファイルが消えた可能性）`);
            continue;
        }
        try {
            disposables.push(registerPluginProvider(term, sessionId, entry.plugin, setting.vars ?? {}));
        } catch (e) {
            console.error(`[LinkPlugins] ${setting.id} の登録に失敗:`, e);
        }
    }
    registeredProviders.set(sessionId, disposables);
};

function registerPluginProvider(term, sessionId, plugin, vars) {
    const linkProvider = {
        provideLinks: (bufferLineNumber, callback) => {
            const line = term.buffer.active.getLine(bufferLineNumber - 1);
            if (!line) { callback(undefined); return; }

            // 桁の対応付けはホスト側の共通処理を使う（全角文字があっても位置がズレない）
            const { text: lineText, columns } = getBufferLineText(line);

            const ctx = {
                ...(sessionContexts.get(sessionId) ?? {}),
                sessionId,
                vars,
                line: lineText
            };

            const links = [];
            try {
                for (const hit of matchPlugin(plugin, lineText, ctx)) {
                    links.push({
                        range: {
                            start: { x: columns[hit.start] + 1, y: bufferLineNumber },
                            end: { x: columns[hit.end] + 1, y: bufferLineNumber }
                        },
                        text: hit.text,
                        activate: (e) => {
                            if (!isPrimaryClick(e)) return;
                            // 出口はホストに一本化。開く/コピーの切替設定もここで効く
                            activateTerminalLink(hit.url);
                        }
                    });
                }
            } catch (e) {
                console.error(`[LinkPlugins] ${plugin.id} の検出でエラー:`, e);
            }

            callback(links.length > 0 ? links : undefined);
        }
    };
    return term.registerLinkProvider(linkProvider);
}

// プラグインの宣言から実際のマッチを取り出す。
// detect() があればそれを使い、無ければ pattern + accept + url の糖衣として扱う。
function* matchPlugin(plugin, lineText, ctx) {
    if (typeof plugin.detect === 'function') {
        for (const hit of plugin.detect(lineText, ctx) ?? []) {
            if (hit && hit.url) yield hit;
        }
        return;
    }

    if (!plugin.pattern) return;
    // グローバル正規表現は lastIndex を持ち回るため、毎回リセットしてから使う
    const re = plugin.pattern;
    re.lastIndex = 0;
    let match;
    while ((match = re.exec(lineText)) !== null) {
        const text = match[0];
        const start = match.index;
        const end = start + text.length;

        if (typeof plugin.accept === 'function') {
            const before = lineText.slice(0, start);
            const after = lineText.slice(end);
            if (!plugin.accept({ text, before, after, line: lineText, ctx })) continue;
        }

        // url() が空を返したら「このトークンは自分の担当ではない」の意思表示。
        // 位置を占有しないので、後続のプラグインや組み込みの解釈にフォールバックする。
        const url = plugin.url(text, ctx);
        if (!url) continue;

        yield { start, end, text, url };

        // 空マッチで無限ループしないための保険
        if (re.lastIndex === start) re.lastIndex++;
    }
}
