namespace TerminalHub.Resources
{
    /// <summary>
    /// 共有リソースのマーカークラス。
    /// IStringLocalizer&lt;SharedResource&gt; として注入することで、
    /// Resources/SharedResource.{culture}.resx から文字列を引ける。
    ///
    /// 使用例:
    ///   @inject IStringLocalizer&lt;SharedResource&gt; L
    ///   &lt;h1&gt;@L["SettingsDialog.Title"]&lt;/h1&gt;
    /// </summary>
    public class SharedResource
    {
    }
}
