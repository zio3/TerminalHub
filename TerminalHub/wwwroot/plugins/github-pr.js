// 組み込みプラグイン: #123 を GitHub の PR/Issue ページへ。
//
// owner/repo はセッションの origin から決まるため、ctx.git.originUrl を解釈して組み立てる。
// origin の「解釈」をここに置いているのは、GitLab や社内 Gitea を使いたくなったときに
// C# を触らず、このファイルを真似たプラグインを1枚足すだけで済むようにするため。
// サーバーが供給するのは生の origin だけ。

const ORIGIN = /^(?:https?:\/\/|ssh:\/\/git@|git@)github\.com[:/](?<owner>[^/]+)\/(?<repo>[^/]+?)(?:\.git)?\/?$/i;

export default {
    id: 'github-pr',
    pattern: /#\d+\b/g,

    // 実データ調査より: 実ターミナル出力の #数字 は大半が本物の PR/Issue 参照で、
    // 唯一の恒常的な誤検出源は Claude Code の画像貼り付け UI「[Image #1]」だけだった。
    accept: ({ before }) => !/\[Image\s$/.test(before),

    url: (text, ctx) => {
        const m = ctx.git?.originUrl?.match(ORIGIN);
        if (!m) return null;   // github.com 以外・解析不能ならリンクにしない
        // /pull/{n} は対象が Issue なら /issues/{n} へ自動リダイレクトされるため区別は不要
        return `https://github.com/${m.groups.owner}/${m.groups.repo}/pull/${text.slice(1)}`;
    }
};
