// #123 を「決め打ちしたリポジトリ」の PR/Issue へ。
//
// 同梱の github-pr はセッションの origin から owner/repo を決めるため、
// origin の無いローカルリポジトリや、GitHub 以外を origin にしているフォルダでは
// 何もリンクにならない。こちらは飛び先をセッション設定の repoUrl で明示する。
//
// repoUrl の例: https://github.com/zio3/TerminalHub
//
// 同じ #123 を同梱の github-pr も拾うので、両方を有効にするならこちらを上に置くこと
// （上にあるほうが先に登録され、位置を取り合ったときに勝つ）。

export default {
    id: 'github-fixed',
    vars: ['repoUrl'],
    pattern: /#\d+\b/g,

    // Claude Code の画像貼り付け UI「[Image #1]」を誤検出しないための除外（同梱と同じ）
    accept: ({ before }) => !/\[Image\s$/.test(before),

    url: (text, ctx) => {
        const base = ctx.vars.repoUrl?.trim().replace(/\/+$/, '');
        if (!base) return null;   // 未設定ならリンクにしない（設定画面で入力する）
        // /pull/{n} は対象が Issue なら /issues/{n} へ自動リダイレクトされる
        return `${base}/pull/${text.slice(1)}`;
    }
};
