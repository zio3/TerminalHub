// 注意: namespace はあえてアセンブリルート (TerminalHub) 直下に置いている。
// IStringLocalizer<T> の resx 解決規則は {baseNamespace}.{ResourcesPath}.{RelativeTypeName} を
// embedded resource 名として要求する。クラスを TerminalHub.Resources に置くと
// RelativeTypeName が "Resources.SharedResource" となり、ResourcesPath="Resources" と
// 組み合わさって "TerminalHub.Resources.Resources.SharedResource" を探しに行ってしまう
// (実際の埋め込みリソース名は "TerminalHub.Resources.SharedResource")。
// 結果としてリソース解決に失敗しキー文字列がそのまま返ってしまうため、ルート namespace に置く。
namespace TerminalHub
{
    /// <summary>
    /// 共有リソースのマーカークラス。
    /// IStringLocalizer&lt;SharedResource&gt; として注入することで、
    /// Resources/SharedResource.{culture}.resx から文字列を引ける。
    ///
    /// 使用例:
    ///   @inject IStringLocalizer&lt;SharedResource&gt; L
    ///   &lt;h1&gt;@L["Settings.Title"]&lt;/h1&gt;
    /// </summary>
    public class SharedResource
    {
    }
}
