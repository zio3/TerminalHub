// PROJ-123 を Backlog の課題ページへ。
//
// spaceUrl の例: https://your-space.backlog.jp
//
// 課題キーの形は Jira と同じなので、両方を有効にすると同じ文字列を取り合う。
// 使うほうを上に置く（上にあるほうが先に登録され、位置を取り合ったときに勝つ）か、
// 使わないほうのチェックを外すこと。
//
// projectKeys を入れると、そのプロジェクトのキーだけをリンクにする（例: PROJ,OPS）。
// 空なら形が合うものを全部拾う。

export default {
    id: 'backlog',
    vars: ['spaceUrl', 'projectKeys'],
    pattern: /\b[A-Z][A-Z0-9]+-\d+\b/g,

    accept: ({ text, before, after, ctx }) => {
        // より大きな識別子の一部（FOO_ABC-123 や ABC-123-456）は対象外
        if (/[A-Za-z0-9_]$/.test(before)) return false;
        if (/^[-_A-Za-z0-9]/.test(after)) return false;

        const keys = (ctx.vars.projectKeys ?? '')
            .split(/[,\s]+/)
            .map(k => k.trim().toUpperCase())
            .filter(Boolean);
        if (keys.length === 0) return true;

        return keys.includes(text.slice(0, text.lastIndexOf('-')).toUpperCase());
    },

    url: (text, ctx) => {
        const base = ctx.vars.spaceUrl?.trim().replace(/\/+$/, '');
        if (!base) return null;
        return `${base}/view/${text}`;
    }
};
