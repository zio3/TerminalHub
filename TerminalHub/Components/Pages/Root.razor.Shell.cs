using TerminalHub.Components.Shared.BottomPanels;

namespace TerminalHub.Components.Pages;

/// <summary>
/// Root.razor のシェルパネル関連機能
/// シェルパネル（コマンドプロンプト/PowerShell）は各ShellPanelコンポーネントが
/// 自身でConPTYセッションを管理する。ここではパネル参照の管理のみ行う。
/// </summary>
public partial class Root
{
    /// <summary>シェルパネルの参照（タブID → パネル）</summary>
    private Dictionary<string, ShellPanel> shellPanelRefs = new();
}
