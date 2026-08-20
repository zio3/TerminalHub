// ABC-123 のような課題キーを Jira の課題ページへ。
//
// baseUrl の例: https://your-company.atlassian.net
//
// 課題キーは「大文字の英数字 + ハイフン + 数字」。この形は普通の英単語には出ないが、
// UUID の一部や定数名に紛れることがあるので、前後が識別子として続いている場合は弾く。

export default {
    id: 'jira',
    vars: ['baseUrl'],
    pattern: /\b[A-Z][A-Z0-9]+-\d+\b/g,

    accept: ({ before, after }) => {
        // ABC-123-456 や FOO_ABC-123 のような、より大きな識別子の一部は対象外
        if (/[A-Za-z0-9_]$/.test(before)) return false;
        if (/^[-_A-Za-z0-9]/.test(after)) return false;
        return true;
    },

    url: (text, ctx) => {
        const base = ctx.vars.baseUrl?.trim().replace(/\/+$/, '');
        if (!base) return null;
        return `${base}/browse/${text}`;
    }
};
