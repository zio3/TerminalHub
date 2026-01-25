# 認証設定について

TerminalHubは、localhost以外からのアクセス（LAN内やトンネル経由）に
Basic認証をかけることができます。

## 設定方法

1. このフォルダ内の `auth.json` をテキストエディタで開く
2. UsernameとPasswordを設定して保存
3. TerminalHubを再起動

## 設定例

```json
{
  "Username": "admin",
  "Password": "your-password-here"
}
```

## 注意事項

- localhostからのアクセスは常に認証なしで利用可能
- UsernameとPasswordの両方を設定すると認証が有効になります
- 空のままにすると認証は無効（全アクセス許可）
- このファイルはアプリ更新時も上書きされません
