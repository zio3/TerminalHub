// Unicode11 + VS16 拡幅プロバイダの登録（本体 terminal.js と診断ツール rawring-replay.html の共通実装）。
//
// VS16（U+FE0F）が幅1セルの直後に来たらそのセルを幅2へ広げる。ConPTY(conhost) の
// 実測挙動（2026-08-06・experiments/ConPtyWidthProbe）に合わせたもので、これが無いと
// Claude Code 等が幅2前提で組んだ表の罫線がズレ、絵文字グリフが隣の文字に重なる。
// エミュレータ側（TerminalHub.Terminal の VtParser → TerminalGrid.ApplyVs16 経路）と同一規則。
// 変更時は必ず両方揃えること（片方だけ変えると切替時の再生パリティが壊れる）。
//
// 依存: @xterm/addon-unicode11 が先に読み込まれていること（グローバル Unicode11Addon）。
(function () {
    'use strict';

    /**
     * term に '11-vs16' プロバイダを登録して有効化する。
     * Unicode11Addon が無い・失敗した場合は素の Unicode11（それも無理なら既定 V6）へフォールバック。
     * @returns {string} 実際に有効化された unicode.activeVersion
     */
    window.registerUnicode11Vs16 = function (term) {
        if (typeof Unicode11Addon === 'undefined' || !Unicode11Addon.Unicode11Addon) {
            return term.unicode.activeVersion;
        }
        try {
            // activate はプロバイダを register するだけなので、スタブに差し込んで
            // UnicodeV11 プロバイダ本体を取り出し、VS16 規則だけ上乗せしたラッパーを登録する
            const cap = { provider: null };
            new Unicode11Addon.Unicode11Addon().activate({ unicode: { register: (p) => { cap.provider = p; } } });
            const base = cap.provider;
            term.unicode.register({
                version: '11-vs16',
                wcwidth: (cp) => base.wcwidth(cp),
                charProperties: (cp, preceding) => {
                    // preceding のパック形式: bit0=join / bit1-2=幅 / bit3〜=状態
                    if (cp === 0xFE0F && ((preceding >> 1) & 3) === 1) {
                        return (2 << 1) | 1; // 幅2 + join → コアが直前セルを拡幅し1桁前進
                    }
                    return base.charProperties(cp, preceding);
                },
            });
            term.unicode.activeVersion = '11-vs16';
        } catch (error) {
            console.error('[Unicode11] Failed to load Unicode11(+VS16) provider:', error);
            // フォールバック: 素の Unicode11（VS16 拡幅なし）
            try {
                term.loadAddon(new Unicode11Addon.Unicode11Addon());
                term.unicode.activeVersion = '11';
            } catch (e2) { /* 既定(V6)のまま続行 */ }
        }
        return term.unicode.activeVersion;
    };
})();
