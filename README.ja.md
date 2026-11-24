<img alt="TutorialOverlay —— どんな Windows アプリの上にも直接描かれるレッスン" src="docs/media/logo.webp" width="100%">

<a href="README.md">English</a> · <a href="README.ru.md">Русский</a> · <a href="README.es.md">Español</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.zh-CN.md">简体中文</a> · <b>日本語</b>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

どんな Windows アプリの上にも直接描かれる、AI エージェントが導くステップごとのチュートリアル。

<img alt="FL Studio でのレッスン：マスコットがステップのセルを指し、カードが各ステップにチェックを付けていく" src="docs/media/fl-lesson.webp" width="800" />

上のレッスンは、プロジェクトのテストロボットが FL Studio で実際に進めたものです。

**場所を示し、理由を説明する。** あなたの代わりにアプリをクリックしてくれるエージェントは作業を終わらせますが、
あなたは何も身につきません。TutorialOverlay はクリックをあなたに任せます。どこを押すかを一つずつ示し、なぜそれ
なのかを説明するので、次からは助けがいらなくなります。

すべてはあなたのコンピューター上で動きます。サーバーは `127.0.0.1` だけで待ち受け、どこにもデータを送りません。
画面を録画することはなく、エージェントが求めたときだけスクリーンショットを撮ります
（[詳細](docs/server.md#screenshots-and-privacy)）。

## はじめに

[TutorialOverlay をインストール](#インストール)して、AI エージェントにアプリでの操作方法を聞いてみましょう：

> [!TIP]
> *「メモ帳で右端で折り返す設定をオンにする方法を、ステップごとに教えて」*

エージェントは MCP でアプリの画面を読み取り、レッスンを書きます。ステップ、目標、そしてそれぞれの*理由*です：

```json
{
  "title": "メモ帳の右端で折り返す",
  "app": "Notepad",
  "lang": "ja",
  "steps": [
    {
      "title": "「表示」メニューを開きます",
      "why": "文字の表示に関する設定はすべて「表示」メニューにあります。",
      "targets": [{ "name": "表示", "anchor": { "element": { "name": "表示", "role": "MenuItem" } } }]
    },
    {
      "title": "Esc でメニューを閉じます",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

オーバーレイはアプリの上で各目標を囲み、マスコットがそれを指し、ステップのカードがレッスンを案内します。ステップ
はクリック、ピクセル、ウィンドウ、ショートカット、ドラッグ、スクロールで自動的に完了します。行き詰まったら
**ヘルプ**を押してください。エージェントがカードの中で直接答えます。最後にレッスンを保存すれば、通知領域から
エージェントなしでもう一度学べます。

<img width="420" alt="ステップのカード：ヘルプが目標に印をつけて説明し、次に「全部実行」—— カードの上部に青い AUTO ラベルが付き、ステップが自動でチェックされていく" src="docs/media/card-help.webp" />

まずはお手本を見たいですか？カードの**このステップを実行**（または**全部実行**）を押すと、マウスがオーバーレイに
渡ります。本物のポインターを動かし、囲まれた場所をクリックするのをあなたは見ているだけです。カード上部の青い
**AUTO** が、操作中であることを示します。マウスに触れれば、操作はすぐにあなたに戻ります。取り消せない操作
（支払い、注文、完全な削除、送信）は決して押しません。そのボタンは鍵マーク付きの赤で表示され、押すのはあなた
です。

カード、通知領域、設定は日本語、英語、ロシア語、スペイン語、ドイツ語、フランス語、ポルトガル語、中国語に対応して
います。自分の言語を追加することもできます（[方法](docs/server.md)）。

レッスンの完全な形式は [`docs/tutorials.md`](docs/tutorials.md)（英語）にあります。

## インストール

> [!NOTE]
> Windows 10 または 11、x64。ほかには何もいりません。.NET は exe に含まれています。

TutorialOverlay はローカルの MCP サーバーなので、MCP に対応したどのエージェントからでも操作できます。
[Releases][releases] から `TutorialOverlay-<バージョン>-win-x64.zip` をダウンロードして展開し（例：
`C:\Tools\TutorialOverlay`）、`tutorial-overlay` という名前で `PATH` に追加します：

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

次に **Claude Code** でプラグインをインストールします。サーバーと、エージェントに良いレッスンの書き方を教える
skill が入ります：

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

これで完了です。キーもポートも必要ありません。エージェントが必要なときにオーバーレイを起動し、通知領域のメニュー
から終了するまでそこに常駐します。

> [!TIP]
> いちばんうまく動くのはプラグイン付きの Claude Code です。ほかのエージェントも同じツールを使え、`guide` ツール
> （0.7.0 以降）で同じ skill を読めますが、レッスン中にエージェントの注意を保つプラグインの hook はありません。

<details>
<summary>Codex</summary>

`~/.codex/config.toml`（またはプロジェクトの `.codex/config.toml`）に追加します：

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

Claude Desktop で **Settings → Developer → Edit Config** を開き、`claude_desktop_config.json` にサーバーを追加
します（`mcpServers` にほかの項目があれば残してください）：

```json
{
  "mcpServers": {
    "tutorial-overlay": {
      "command": "C:\\Tools\\TutorialOverlay\\TutorialOverlay.exe",
      "args": ["--stdio"]
    }
  }
}
```

exe はフルパスで指定してください。Claude Desktop は起動中に変更された `PATH` を認識しないことがあります。Claude
Desktop を完全に終了し（ウィンドウを閉じても通知領域で動き続けることがあります）、もう一度開くと、新しいチャット
にツールが表示されます。あとはこう頼むだけです：*「メモ帳で右端で折り返す設定をオンにする方法を教えて。」*

</details>

<details>
<summary>プラグインなしの Claude Code、ほかの MCP クライアント</summary>

ローカル（stdio）の MCP サーバーを追加します。コマンドは `tutorial-overlay`（または `TutorialOverlay.exe` の
フルパス）、引数は `--stdio` です。Claude Code では：

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

プラグインと一緒にこの方法で追加しないでください。エージェントにすべてのツールが二重に見えてしまいます。
Streamable HTTP に対応したクライアントは、動作中のオーバーレイに直接接続することもできます。
[`docs/server.md`](docs/server.md) を参照してください。

</details>

<details>
<summary>アップデート</summary>

通知領域のメニューから TutorialOverlay を終了し、新しいバージョンを古いフォルダーの上に展開して、もう一度
`TutorialOverlay.exe` を `tutorial-overlay.exe` としてコピーします。設定、キー、保存したチュートリアルは
`%LOCALAPPDATA%\TutorialOverlay` にあり、そのまま残ります。エージェントの設定もそのまま使えます。

</details>

## ソースからビルド

[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。リポジトリのルートで：

```powershell
# ビルド：publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# テストを実行
dotnet run --project tests

# 自分のビルドを Claude Code で使う
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` は確認用にリリースの zip を作ります。公開されるものはリリースタグで
[GitHub Actions](docs/development.md#releases) がビルドします。プログラムの内部構造は
[`docs/development.md`](docs/development.md) にあります。

## リファレンス（英語）

- [サーバー](docs/server.md) —— コマンドラインオプション、通知領域のアイコン、設定、`config.json`、セキュリティ、ログ
- [MCP ツールとイベント](docs/tools.md) —— エージェントが呼び出せるものと、受け取るもの
- [チュートリアル](docs/tutorials.md) —— レッスンの形式：図形、アンカー、ステップと条件、マスコット、ヘルプ
- [開発](docs/development.md) —— プログラムの内部、テスト、テストロボット

## ライセンス

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="踊るマスコットの Shazhok" />

Shazhok からごあいさつ。ここにあるほかのすべてと同じく、コードで描かれています —— 画像もフォントもパッケージも
使っていません。

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
